module Fedit.Tests.LspClientTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Fedit
open Xunit

// End-to-end gate for the LSP client against a real server: `sema lsp`
// (stdio, Content-Length framing, full-text sync, unsolicited
// publishDiagnostics). Trivially passes when sema is not installed.

let private commandOnPath (command: string) =
    match Environment.GetEnvironmentVariable "PATH" with
    | null -> false
    | searchPath ->
        searchPath.Split Path.PathSeparator
        |> Array.exists (fun dir ->
            dir <> ""
            && (File.Exists(Path.Combine(dir, command))
                || File.Exists(Path.Combine(dir, command + ".exe"))))

/// True when the command resolves on PATH and actually runs (`--version`
/// exits 0). A rustup proxy shim without the underlying component resolves
/// on PATH but fails at launch — that counts as absent.
let private commandRuns (command: string) =
    if not (commandOnPath command) then
        false
    else
        try
            let startInfo = Diagnostics.ProcessStartInfo command
            startInfo.ArgumentList.Add "--version"
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            use versionProcess = Diagnostics.Process.Start startInfo

            if versionProcess.WaitForExit 10000 then
                versionProcess.ExitCode = 0
            else
                versionProcess.Kill true
                false
        with _ ->
            false

let private pollUntil (timeout: TimeSpan) (condition: unit -> bool) : bool =
    let deadline = DateTime.UtcNow + timeout
    let mutable satisfied = condition ()

    while not satisfied && DateTime.UtcNow < deadline do
        Thread.Sleep 50
        satisfied <- condition ()

    satisfied

/// Fire one request and poll for its callback; None on timeout.
let private awaitRequest (send: (Result<'a, string> -> unit) -> unit) : Result<'a, string> option =
    let resultCell = ref None
    send (fun result -> lock resultCell (fun () -> resultCell.Value <- Some result))

    pollUntil (TimeSpan.FromSeconds 10.0) (fun () -> lock resultCell (fun () -> resultCell.Value.IsSome))
    |> ignore

    lock resultCell (fun () -> resultCell.Value)

// `greet` defined on line 0 (name at characters 6..11), used on line 3.
let private sourceText =
    "(defn greet (name)\n  (str \"hello \" name))\n\n(greet \"world\")\n"

[<Fact>]
let ``sema lsp end to end: running, definition, hover, diagnostics, shutdown`` () =
    if not (commandOnPath "sema") then
        Console.Error.WriteLine "skipping LspClient integration test: sema is not on PATH"
    else
        let root = Paths.norm (Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        Directory.CreateDirectory root |> ignore

        try
            File.WriteAllText(root + "/sema.toml", "[package]\nname = \"fedit-test\"\n")
            let documentPath = root + "/main.sema"
            File.WriteAllText(documentPath, sourceText)

            let diagnosticsByPath = ConcurrentDictionary<string, LspDiagnostic list>()
            let statuses = ConcurrentQueue<LspServerStatus>()

            let callbacks =
                { OnDiagnostics = fun (path, diagnostics) -> diagnosticsByPath.[path] <- diagnostics
                  OnStatusChanged = fun status -> statuses.Enqueue status
                  OnLog = ignore }

            let config =
                { Name = "sema"
                  Command = "sema"
                  Args = [ "lsp" ]
                  FileTypes = [ "sema" ]
                  RootMarkers = [ "sema.toml" ] }

            use client = LspClient.create config root callbacks

            Assert.True(
                pollUntil (TimeSpan.FromSeconds 10.0) (fun () -> client.Status = LspServerStatus.Running),
                "server never reached Running; recent stderr: "
                + String.concat " | " (client.RecentLog())
            )

            Assert.True client.Capabilities.DefinitionProvider
            Assert.True client.Capabilities.HoverProvider
            Assert.True client.Capabilities.ReferencesProvider

            client.NotifyOpened(documentPath, "sema", 1, sourceText)

            // Definition from the usage `(greet "world")`. The server indexes
            // the workspace just after `initialized`, so retry until it
            // resolves.
            let usagePosition = { Line = 3; Column = 2 }

            let queryDefinition () =
                match awaitRequest (fun reply -> client.SendDefinition(documentPath, usagePosition, reply)) with
                | Some(Result.Ok locations) -> locations
                | _ -> []

            let mutable definitionLocations = queryDefinition ()
            let definitionDeadline = DateTime.UtcNow.AddSeconds 10.0

            while List.isEmpty definitionLocations && DateTime.UtcNow < definitionDeadline do
                Thread.Sleep 200
                definitionLocations <- queryDefinition ()

            match definitionLocations with
            | [] -> Assert.Fail "definition from the usage position returned no locations"
            | location :: _ ->
                Assert.Equal<string option>(Some documentPath, LspUri.toPath location.Uri)
                Assert.Equal(0, location.Range.Start.Line)

            let queryHover () =
                match awaitRequest (fun reply -> client.SendHover(documentPath, usagePosition, reply)) with
                | Some(Result.Ok lines) -> lines |> List.filter (fun line -> line.Trim() <> "")
                | _ -> []

            let mutable hoverLines = queryHover ()
            let hoverDeadline = DateTime.UtcNow.AddSeconds 10.0

            while List.isEmpty hoverLines && DateTime.UtcNow < hoverDeadline do
                Thread.Sleep 200
                hoverLines <- queryHover ()

            Assert.False(List.isEmpty hoverLines, "hover at the usage position returned no content")

            // References from the same usage position (declaration included)
            // must resolve to at least one location in the document.
            let queryReferences () =
                match awaitRequest (fun reply -> client.SendReferences(documentPath, usagePosition, reply)) with
                | Some(Result.Ok locations) -> locations
                | _ -> []

            let mutable referenceLocations = queryReferences ()
            let referencesDeadline = DateTime.UtcNow.AddSeconds 10.0

            while List.isEmpty referenceLocations && DateTime.UtcNow < referencesDeadline do
                Thread.Sleep 200
                referenceLocations <- queryReferences ()

            match referenceLocations with
            | [] -> Assert.Fail "references from the usage position returned no locations"
            | locations ->
                // Suffix match: sema canonicalizes symlinked temp paths on
                // macOS (/var -> /private/var), so exact equality is fragile.
                Assert.All(
                    locations,
                    fun location ->
                        match LspUri.toPath location.Uri with
                        | Some path -> Assert.EndsWith("/main.sema", path)
                        | None -> Assert.Fail $"non-file reference URI: {location.Uri}"
                )

            // A syntactically broken change must publish >= 1 diagnostic.
            client.NotifyChanged(documentPath, 2, "(defn broken (")

            Assert.True(
                pollUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                    match diagnosticsByPath.TryGetValue documentPath with
                    | true, diagnostics -> not (List.isEmpty diagnostics)
                    | _ -> false),
                "no diagnostics arrived after a broken didChange"
            )

            // Polite teardown; sema may force-exit early — Stopped, no zombie,
            // and never a Failed transition.
            client.Shutdown()
            Assert.Equal(LspServerStatus.Stopped, client.Status)
            Assert.True(client.ProcessHasExited, "server process is still alive after Shutdown")

            let observed = List.ofSeq statuses
            Assert.Contains(LspServerStatus.Starting, observed)
            Assert.Contains(LspServerStatus.Running, observed)

            Assert.DoesNotContain(
                observed,
                fun status ->
                    match status with
                    | LspServerStatus.Failed _ -> true
                    | _ -> false
            )
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

// `greet` lives in library.sema; main.sema calls it. sema indexes the
// workspace just after `initialized`, so cross-file queries need a settle
// retry. Note: sema 1.30.0's definition provider is same-file only —
// verified against a raw LSP session, it answers null from a call site
// whose definition lives in another file (every cursor position, plain
// defn and module/export/import forms, both files open, scan settled).
// Cross-file navigation rides on references, which the workspace index
// does serve across files.
let private libraryText = "(defn greet (name)\n  (str \"hello \" name))\n"
let private mainText = "(greet \"world\")\n"

let private pathEndsWith (suffix: string) (uri: string) : bool =
    match LspUri.toPath uri with
    | Some path -> path.EndsWith suffix
    | None -> false

[<Fact>]
let ``sema lsp cross file: references across files, diagnostics clear on fix`` () =
    if not (commandOnPath "sema") then
        Console.Error.WriteLine "skipping LspClient cross-file test: sema is not on PATH"
    else
        let root = Paths.norm (Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        Directory.CreateDirectory root |> ignore

        try
            File.WriteAllText(root + "/sema.toml", "[package]\nname = \"fedit-cross-file\"\n")
            let libraryPath = root + "/library.sema"
            let mainPath = root + "/main.sema"
            File.WriteAllText(libraryPath, libraryText)
            File.WriteAllText(mainPath, mainText)

            let diagnosticsByPath = ConcurrentDictionary<string, LspDiagnostic list>()

            let callbacks =
                { OnDiagnostics = fun (path, diagnostics) -> diagnosticsByPath.[path] <- diagnostics
                  OnStatusChanged = ignore
                  OnLog = ignore }

            let config =
                { Name = "sema"
                  Command = "sema"
                  Args = [ "lsp" ]
                  FileTypes = [ "sema" ]
                  RootMarkers = [ "sema.toml" ] }

            use client = LspClient.create config root callbacks

            Assert.True(
                pollUntil (TimeSpan.FromSeconds 10.0) (fun () -> client.Status = LspServerStatus.Running),
                "server never reached Running; recent stderr: "
                + String.concat " | " (client.RecentLog())
            )

            client.NotifyOpened(mainPath, "sema", 1, mainText)

            // Usage `(greet "world")` on line 0 of main.sema. References
            // from it must cover both sites — the definition in
            // library.sema (via the workspace index) and the call in
            // main.sema.
            let usagePosition = { Line = 0; Column = 2 }

            let queryReferences () =
                match awaitRequest (fun reply -> client.SendReferences(mainPath, usagePosition, reply)) with
                | Some(Result.Ok locations) -> locations
                | _ -> []

            let coversBothFiles (locations: LspLocation list) =
                locations
                |> List.exists (fun location -> pathEndsWith "/library.sema" location.Uri)
                && locations
                   |> List.exists (fun location -> pathEndsWith "/main.sema" location.Uri)

            let mutable referenceLocations = queryReferences ()
            let referencesDeadline = DateTime.UtcNow.AddSeconds 10.0

            while not (coversBothFiles referenceLocations) && DateTime.UtcNow < referencesDeadline do
                Thread.Sleep 200
                referenceLocations <- queryReferences ()

            Assert.True(
                coversBothFiles referenceLocations,
                "references never covered both files; got: "
                + String.concat ", " (referenceLocations |> List.map (fun location -> location.Uri))
            )

            // The reference into library.sema points at the defn name on
            // line 0 — the location the editor would jump to.
            let libraryReference =
                referenceLocations
                |> List.find (fun location -> pathEndsWith "/library.sema" location.Uri)

            Assert.Equal(0, libraryReference.Range.Start.Line)

            // Definition from the same usage round-trips cleanly, but sema
            // 1.30.0 cannot resolve it across files (see the module note);
            // assert the request itself succeeds rather than its payload.
            match awaitRequest (fun reply -> client.SendDefinition(mainPath, usagePosition, reply)) with
            | Some(Result.Ok _) -> ()
            | Some(Result.Error e) -> Assert.Fail("cross-file definition request failed: " + e)
            | None -> Assert.Fail "cross-file definition request timed out"

            // Break main.sema -> diagnostics arrive; restore the valid text
            // -> an empty diagnostic set replaces them.
            let diagnosticsForMain () =
                diagnosticsByPath
                |> Seq.tryPick (fun (KeyValue(path, diagnostics)) ->
                    if path.EndsWith "/main.sema" then
                        Some diagnostics
                    else
                        None)

            client.NotifyChanged(mainPath, 2, "(defn broken (")

            Assert.True(
                pollUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                    match diagnosticsForMain () with
                    | Some diagnostics -> not (List.isEmpty diagnostics)
                    | None -> false),
                "no diagnostics arrived after a broken didChange"
            )

            client.NotifyChanged(mainPath, 3, mainText)

            Assert.True(
                pollUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                    match diagnosticsForMain () with
                    | Some [] -> true
                    | _ -> false),
                "diagnostics never cleared after the fixed didChange"
            )

            client.Shutdown()
            Assert.Equal(LspServerStatus.Stopped, client.Status)
            Assert.True(client.ProcessHasExited, "server process is still alive after Shutdown")
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

/// Minimal per-server scratch project rooted at the built-in default
/// config's root marker; returns the document to open and its language id.
let private writeScratchProject (serverName: string) (root: string) : string * string =
    match serverName with
    | "typescript" ->
        File.WriteAllText(root + "/package.json", "{ \"name\": \"fedit-handshake\", \"version\": \"0.0.0\" }\n")
        let documentPath = root + "/main.ts"
        File.WriteAllText(documentPath, "export const greeting: string = \"hello\"\n")
        documentPath, "typescript"
    | "rust" ->
        File.WriteAllText(
            root + "/Cargo.toml",
            "[package]\nname = \"fedit-handshake\"\nversion = \"0.0.0\"\nedition = \"2021\"\n"
        )

        Directory.CreateDirectory(root + "/src") |> ignore
        let documentPath = root + "/src/main.rs"
        File.WriteAllText(documentPath, "fn main() {\n    println!(\"hello\");\n}\n")
        documentPath, "rust"
    | other -> failwith ("no scratch project defined for server: " + other)

// Handshake gate against the other built-in defaults: initialize ->
// Running -> didOpen -> clean shutdown. Trivially passes when the server
// binary is not installed.
[<Theory>]
[<InlineData "typescript">]
[<InlineData "rust">]
let ``built-in server handshake: initialize, didOpen, clean shutdown`` (serverName: string) =
    let config =
        LanguageServers.defaults |> List.find (fun server -> server.Name = serverName)

    if not (commandRuns config.Command) then
        Console.Error.WriteLine("skipping handshake test: " + config.Command + " is not installed")
    else
        let root = Paths.norm (Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        Directory.CreateDirectory root |> ignore

        try
            let documentPath, languageId = writeScratchProject serverName root
            let statuses = ConcurrentQueue<LspServerStatus>()

            let callbacks =
                { OnDiagnostics = ignore
                  OnStatusChanged = fun status -> statuses.Enqueue status
                  OnLog = ignore }

            use client = LspClient.create config root callbacks

            Assert.True(
                pollUntil (TimeSpan.FromSeconds 15.0) (fun () -> client.Status = LspServerStatus.Running),
                config.Command
                + " never reached Running; recent stderr: "
                + String.concat " | " (client.RecentLog())
            )

            Assert.True(client.Capabilities.DefinitionProvider, config.Command + " does not advertise definition")

            client.NotifyOpened(documentPath, languageId, 1, File.ReadAllText documentPath)

            // Give the server a beat to process the didOpen: a crash here
            // must fail the test, not hide behind the shutdown.
            Thread.Sleep 500
            Assert.Equal(LspServerStatus.Running, client.Status)
            Assert.False(client.ProcessHasExited, config.Command + " exited after didOpen")

            client.Shutdown()
            Assert.Equal(LspServerStatus.Stopped, client.Status)
            Assert.True(client.ProcessHasExited, config.Command + " is still alive after Shutdown")

            let observed = List.ofSeq statuses
            Assert.Contains(LspServerStatus.Starting, observed)
            Assert.Contains(LspServerStatus.Running, observed)

            Assert.DoesNotContain(
                observed,
                fun status ->
                    match status with
                    | LspServerStatus.Failed _ -> true
                    | _ -> false
            )
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

// ── Runtime.canonicalizePath: symlink resolution at the server boundary ──
//
// Servers publish URIs for the symlink-resolved path (sema realpaths
// macOS's /tmp -> /private/tmp); the Runtime canonicalizes both sides so
// received paths map back onto the editor's buffer paths. Skips when the
// platform refuses symlink creation (Windows without developer mode).

[<Fact>]
let ``canonicalizePath resolves a symlinked directory component`` () =
    let scratch =
        Paths.norm (Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))

    let realDirectory = scratch + "/real"
    let linkDirectory = scratch + "/link"
    Directory.CreateDirectory realDirectory |> ignore

    let linked =
        try
            Directory.CreateSymbolicLink(linkDirectory, realDirectory) |> ignore
            true
        with _ ->
            false

    try
        if not linked then
            Console.Error.WriteLine "skipping canonicalizePath test: symlink creation not permitted"
        else
            File.WriteAllText(realDirectory + "/main.sema", "(def x 1)")

            // The file itself is not a link — only the directory component
            // is — so this exercises the per-component walk.
            let resolved = Runtime.canonicalizePath (linkDirectory + "/main.sema")
            let expected = Runtime.canonicalizePath (realDirectory + "/main.sema")
            Assert.Equal(expected, resolved)
            Assert.EndsWith("/real/main.sema", resolved)

            // Components that exist nowhere pass through unchanged (the
            // scratch prefix itself may still resolve — macOS tempdirs live
            // under the /var -> /private/var symlink).
            let canonicalScratch = Runtime.canonicalizePath scratch

            Assert.Equal(
                canonicalScratch + "/missing/file.sema",
                Runtime.canonicalizePath (scratch + "/missing/file.sema")
            )
    finally
        try
            Directory.Delete(scratch, true)
        with _ ->
            ()
