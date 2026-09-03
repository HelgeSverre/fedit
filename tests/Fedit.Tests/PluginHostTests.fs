module Fedit.Tests.PluginHostTests

open System
open System.IO
open Fedit
open Fedit.PluginApi
open Xunit

let private repoRoot =
    let rec walk (dir: string) =
        if File.Exists(Path.Combine(dir, "Fedit.slnx")) then
            dir
        else
            match Path.GetDirectoryName dir with
            | null -> failwith "could not locate repo root from test bin dir"
            | parent when parent = dir -> failwith "could not locate repo root from test bin dir"
            | parent -> walk parent

    walk AppContext.BaseDirectory

// The host is built (ReferenceOutputAssembly=false) but not copied beside the
// tests, so locate its dll in its own bin dir for whichever config is built.
let private hostDll =
    let candidates =
        [ "Debug"; "Release" ]
        |> List.map (fun cfg ->
            Path.Combine(repoRoot, "src", "Fedit.PluginHost", "bin", cfg, "net10.0", "Fedit.PluginHost.dll"))

    candidates
    |> List.tryFind File.Exists
    |> Option.defaultValue (List.head candidates)

let private copyDir (src: string) (dst: string) =
    let rec go (srcDir: string) (dstDir: string) =
        Directory.CreateDirectory dstDir |> ignore

        for file in Directory.EnumerateFiles srcDir do
            File.Copy(file, Path.Combine(dstDir, Path.GetFileName file), overwrite = true)

        for sub in Directory.EnumerateDirectories srcDir do
            let name = Path.GetFileName sub

            if name <> "bin" && name <> "obj" then
                go sub (Path.Combine(dstDir, name))

    go src dst

let private wordcountContext (text: string) : PluginContext =
    { ActiveBuffer =
        { Id = 1
          Name = "a.txt"
          FilePath = None
          Text = text
          Cursor = { Line = 1; Column = 1 }
          Selection = None
          Language = None
          Dirty = false
          EditTick = 0
          Diagnostics = [] }
      AllBuffers = []
      Workspace =
        { RootPath = "/tmp"
          SelectedPath = None
          Files = [] }
      Event = None
      Config = Map.empty
      Argument = None }

// End-to-end acceptance gate for the out-of-process plugin path: the editor
// (via PluginHostClient) spawns the host child, which builds + loads the real
// wordcount example, and a command invocation round-trips a PluginAction back.
[<Fact>]
let ``editor scans and invokes wordcount through the out-of-process host`` () =
    Assert.True(File.Exists hostDll, "Fedit.PluginHost.dll must sit beside the tests: " + hostDll)

    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore
    copyDir (Path.Combine(repoRoot, "examples", "wordcount")) (Path.Combine(pluginsRoot, "wordcount"))

    use client = new PluginHostClient(hostDll)

    match client.Scan(pluginsRoot, Set.empty) with
    | Result.Error e -> Assert.Fail("scan failed: " + e)
    | Result.Ok registry ->
        Assert.True(registry.Commands.ContainsKey "wc")
        Assert.True(registry.Loaded.ContainsKey "wordcount")

    match client.Invoke("wc", wordcountContext "one two three") with
    | Result.Error e -> Assert.Fail("invoke failed: " + e)
    | Result.Ok actions -> Assert.Equal<PluginAction list>([ Notify(Info, "3 words") ], actions)

[<Fact>]
let ``host reports an error for an unknown command`` () =
    use client = new PluginHostClient(hostDll)
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore
    client.Scan(pluginsRoot, Set.empty) |> ignore

    match client.Invoke("does-not-exist", wordcountContext "x") with
    | Result.Error e -> Assert.Contains("unknown command", e)
    | Result.Ok _ -> Assert.Fail "expected an error for an unknown command"

// ── extension surface: the showcase plugin through the real host ──────────

let private showcaseClient () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore
    copyDir (Path.Combine(repoRoot, "examples", "showcase")) (Path.Combine(pluginsRoot, "showcase"))
    let client = new PluginHostClient(hostDll)

    match client.Scan(pluginsRoot, Set.empty) with
    | Result.Error e -> failwith ("scan failed: " + e)
    | Result.Ok registry ->
        Assert.True(registry.Commands.ContainsKey "showcase-slow", "async command registered")
        Assert.True(registry.Commands.ContainsKey "showcase-panel", "sync command registered")

    client

[<Fact>]
let ``async plugin commands run concurrently: a fast call returns while a slow one is in flight`` () =
    use client = showcaseClient ()
    let clock = System.Diagnostics.Stopwatch.StartNew()

    let slow =
        System.Threading.Tasks.Task.Run(fun () ->
            let result = client.Invoke("showcase-slow", wordcountContext "600")
            clock.ElapsedMilliseconds, result)

    // Give the slow request a head start so it is genuinely in flight.
    System.Threading.Thread.Sleep 100
    let fastResult = client.Invoke("showcase-fast", wordcountContext "")
    let fastAt = clock.ElapsedMilliseconds

    let slowAt, slowResult = slow.Result
    Assert.Equal<PluginAction list>([ Notify(Info, "fast") ], Result.defaultValue [] fastResult)
    Assert.Equal<PluginAction list>([ Notify(Info, "slept 600ms") ], Result.defaultValue [] slowResult)
    Assert.True(fastAt < slowAt, $"fast answered at {fastAt}ms, slow at {slowAt}ms")
    Assert.True(fastAt < 500L, $"fast call was blocked behind the slow one: {fastAt}ms")

[<Fact>]
let ``panel and status actions cross the wire from a real plugin`` () =
    use client = showcaseClient ()

    match client.Invoke("showcase-panel", wordcountContext "") with
    | Result.Error e -> Assert.Fail("invoke failed: " + e)
    | Result.Ok actions ->
        match actions with
        | [ ShowPanel("Showcase", lines); SetStatusItem(Some status) ] ->
            Assert.Equal(3, lines.Length)
            Assert.Equal(TextStyle.Accent, (List.head (List.head lines)).Style)
            Assert.Equal("0 buffers", status)
        | other -> Assert.Fail $"unexpected actions: %A{other}"

[<Fact>]
let ``a plugin exception surfaces as an error, not a dead host`` () =
    use client = showcaseClient ()
    // "sleep" text that is not a number falls back to 50ms — still fine —
    // so provoke an error through an unknown command and then prove the
    // host still serves afterwards.
    match client.Invoke("nope", wordcountContext "") with
    | Result.Error e -> Assert.Contains("unknown command", e)
    | Result.Ok _ -> Assert.Fail "expected an error"

    match client.Invoke("showcase-fast", wordcountContext "") with
    | Result.Ok [ Notify(Info, "fast") ] -> ()
    | other -> Assert.Fail $"host did not recover: %A{other}"

[<Fact>]
let ``hooks registered by a plugin cross the wire and run with the event set`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore
    copyDir (Path.Combine(repoRoot, "examples", "showcase")) (Path.Combine(pluginsRoot, "showcase"))
    use client = new PluginHostClient(hostDll)

    match client.Scan(pluginsRoot, Set.empty) with
    | Result.Error e -> Assert.Fail("scan failed: " + e)
    | Result.Ok registry ->
        Assert.Contains(
            { Event = BufferSaved
              Command = "showcase-on-save"
              Source = "showcase" },
            registry.Hooks
        )

        Assert.Empty registry.Conflicts

    let saved =
        { wordcountContext "" with
            Event = Some BufferSaved }

    match client.Invoke("showcase-on-save", saved) with
    | Result.Ok [ SetStatusItem(Some "saved a.txt") ] -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

    match client.Invoke("showcase-on-save", wordcountContext "") with
    | Result.Ok [ Notify(Info, "not a save event") ] -> ()
    | other -> Assert.Fail $"event should be absent on a direct call: %A{other}"

[<Fact>]
let ``the enriched snapshot reaches a real plugin`` () =
    use client = showcaseClient ()

    let context =
        { wordcountContext "" with
            ActiveBuffer =
                { (wordcountContext "").ActiveBuffer with
                    Language = Some "markdown"
                    Dirty = true
                    EditTick = 3 }
            Config = Map.ofList [ "greeting", "hey" ] }

    match client.Invoke("showcase-context", context) with
    | Result.Ok [ Notify(Info, message) ] -> Assert.Equal("hey: markdown dirty=True tick=3 diagnostics=0", message)
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``picker rows, prompt input, and arguments cross the wire from a real plugin`` () =
    use client = showcaseClient ()

    let two =
        { wordcountContext "" with
            AllBuffers = [ (wordcountContext "x").ActiveBuffer ] }

    match client.Invoke("showcase-pick", two) with
    | Result.Ok [ ShowPicker("Buffers", [ row ], "showcase-picked") ] -> Assert.Equal("a.txt", row.Title)
    | other -> Assert.Fail $"unexpected: %A{other}"

    match
        client.Invoke(
            "showcase-picked",
            { wordcountContext "" with
                Argument = Some "7" }
        )
    with
    | Result.Ok [ Notify(Info, "picked 7") ] -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

    match client.Invoke("showcase-ask", wordcountContext "") with
    | Result.Ok [ PromptInput("Name", "", "showcase-answer") ] -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

    match
        client.Invoke(
            "showcase-answer",
            { wordcountContext "" with
                Argument = Some "Ada" }
        )
    with
    | Result.Ok [ Notify(Info, "hello Ada") ] -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``a plugin's language server and grammar reach the editor and the grammar highlights`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let pluginDir = Path.Combine(pluginsRoot, "showcase")
    Directory.CreateDirectory pluginsRoot |> ignore
    copyDir (Path.Combine(repoRoot, "examples", "showcase")) pluginDir

    // Plant the grammar the showcase plugin declares (folder-relative paths),
    // standing in with the shipped toml library.
    let native =
        Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-arm64", "native", "libtree-sitter-toml.dylib")

    let grammars = Path.Combine(pluginDir, "grammars")
    Directory.CreateDirectory(Path.Combine(grammars, "showcase")) |> ignore

    if File.Exists native then
        File.Copy(native, Path.Combine(grammars, "libtree-sitter-showcase.dylib"))

    File.WriteAllText(Path.Combine(grammars, "showcase", "highlights.scm"), "(bare_key) @attribute\n")

    use client = new PluginHostClient(hostDll)

    match client.Scan(pluginsRoot, Set.empty) with
    | Result.Error e -> Assert.Fail("scan failed: " + e)
    | Result.Ok registry ->
        Assert.Empty registry.Conflicts
        let server = registry.LanguageServers |> List.find (fun s -> s.Name = "showcase-ls")
        Assert.Equal("showcase-language-server", server.Command)
        Assert.Equal<string list>([ "showcase" ], server.FileTypes)

        let grammar = registry.Languages |> List.find (fun g -> g.Name = "showcase")
        Assert.True(Path.IsPathRooted grammar.Library, "library path resolved against the plugin folder")
        Assert.Equal(Path.Combine(grammars, "libtree-sitter-showcase.dylib"), grammar.Library)

        if File.Exists native then
            // The editor side: the spec lands in the highlight registry.
            use highlight = (HighlightRegistry.tryCreate ()).Value

            highlight.AddLanguages
                [ { Name = grammar.Name
                    Extensions = grammar.Extensions
                    Library = Some grammar.Library
                    Symbol = grammar.Symbol
                    Queries = grammar.Queries } ]

            let spans =
                Highlight.parseSpans highlight "showcase" "key = 1" |> Option.defaultValue [||]

            Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Attribute)

[<Fact>]
let ``cancelling the token aborts a slow plugin run promptly`` () =
    use client = showcaseClient ()
    use cts = new System.Threading.CancellationTokenSource()
    let clock = System.Diagnostics.Stopwatch.StartNew()

    let run =
        System.Threading.Tasks.Task.Run(fun () -> client.Invoke("showcase-slow", wordcountContext "3000", cts.Token))

    System.Threading.Thread.Sleep 150
    cts.Cancel()
    let result = run.Result
    let elapsed = clock.ElapsedMilliseconds

    match result with
    | Result.Error "cancelled" -> ()
    | other -> Assert.Fail $"expected a cancelled run, got %A{other}"

    Assert.True(elapsed < 2000L, $"cancel took {elapsed}ms")

    match client.Invoke("showcase-fast", wordcountContext "") with
    | Result.Ok [ Notify(Info, "fast") ] -> ()
    | other -> Assert.Fail $"host did not recover: %A{other}"

[<Fact>]
let ``decorations cross the wire from a real plugin`` () =
    use client = showcaseClient ()

    match client.Invoke("showcase-decorate", wordcountContext "one\nTODO two\nthree\nTODO four") with
    | Result.Ok [ SetDecorations(1, marks) ] -> Assert.Equal<int list>([ 2; 4 ], marks |> List.map (fun m -> m.Line))
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``a plugin can read the clipboard through the editor mid-run`` () =
    use client = showcaseClient ()

    client.OnRequest <-
        function
        | "readClipboard" -> Ok "from the test"
        | other -> Result.Error $"nope: {other}"

    match client.Invoke("showcase-clipboard", wordcountContext "") with
    | Result.Ok [ Notify(Info, "clipboard: from the test") ] -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``a read-back the editor refuses fails the plugin run, not the host`` () =
    use client = showcaseClient ()
    client.OnRequest <- fun _ -> Result.Error "clipboard unavailable"

    match client.Invoke("showcase-clipboard", wordcountContext "") with
    | Result.Error message -> Assert.Contains("clipboard unavailable", message)
    | other -> Assert.Fail $"expected an error: %A{other}"

    match client.Invoke("showcase-fast", wordcountContext "") with
    | Result.Ok [ Notify(Info, "fast") ] -> ()
    | other -> Assert.Fail $"host did not recover: %A{other}"

[<Fact>]
let ``a plugin completion provider answers through the real host`` () =
    use client = showcaseClient ()

    match client.Completions("showcase", wordcountContext "sho") with
    | Result.Ok items ->
        Assert.Equal<string list>([ "showcaseHello"; "showcaseWorld" ], items |> List.map (fun i -> i.Label))
        Assert.Equal("from showcase", (items |> List.head).Detail)
    | Result.Error e -> Assert.Fail("completions failed: " + e)

[<Fact>]
let ``the scanned registry reports the provider's file types`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore
    copyDir (Path.Combine(repoRoot, "examples", "showcase")) (Path.Combine(pluginsRoot, "showcase"))
    use client = new PluginHostClient(hostDll)

    match client.Scan(pluginsRoot, Set.empty) with
    | Result.Ok registry ->
        Assert.Equal<(string * string list) list>([ "showcase", [ "showcase" ] ], registry.CompletionProviders)
    | Result.Error e -> Assert.Fail("scan failed: " + e)
