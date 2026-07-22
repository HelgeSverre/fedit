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

let private semaOnPath () =
    match Environment.GetEnvironmentVariable "PATH" with
    | null -> false
    | searchPath ->
        searchPath.Split Path.PathSeparator
        |> Array.exists (fun dir ->
            dir <> ""
            && (File.Exists(Path.Combine(dir, "sema"))
                || File.Exists(Path.Combine(dir, "sema.exe"))))

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
    if not (semaOnPath ()) then
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
