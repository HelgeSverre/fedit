module Fedit.Tests.PluginsTests

open System.IO
open Xunit
open Fedit

// ---------------------------------------------------------------------------
// Manifest parsing
// ---------------------------------------------------------------------------

let private writeTemp (json: string) =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json")
    File.WriteAllText(path, json)
    path

[<Fact>]
let ``tryParseManifest accepts a minimal valid manifest`` () =
    let path =
        writeTemp
            """{
              "name": "wc",
              "version": "0.1.0",
              "apiVersion": "1",
              "entryAssembly": "wc.dll",
              "entryType": "Wc.Plugin"
            }"""

    match Plugins.tryParseManifest path with
    | Result.Ok manifest ->
        Assert.Equal("wc", manifest.Name)
        Assert.Equal("0.1.0", manifest.Version)
        Assert.Equal("1", manifest.ApiVersion)
        Assert.Equal("wc.dll", manifest.EntryAssembly)
        Assert.Equal("Wc.Plugin", manifest.EntryType)
        Assert.Equal("", manifest.Description) // optional, defaulted
    | Result.Error reason -> Assert.Fail $"expected Ok, got Error '{reason}'"

[<Fact>]
let ``tryParseManifest rejects a manifest missing required fields`` () =
    let path = writeTemp """{ "name": "wc" }"""

    match Plugins.tryParseManifest path with
    | Result.Ok _ -> Assert.Fail "expected Error for missing fields"
    | Result.Error reason -> Assert.Contains("missing required field", reason)

[<Fact>]
let ``tryParseManifest rejects an incompatible apiVersion`` () =
    let path =
        writeTemp
            """{
              "name": "wc",
              "version": "0.1.0",
              "apiVersion": "2",
              "entryAssembly": "wc.dll",
              "entryType": "Wc.Plugin"
            }"""

    match Plugins.tryParseManifest path with
    | Result.Ok _ -> Assert.Fail "expected Error for unsupported apiVersion"
    | Result.Error reason -> Assert.Contains("apiVersion", reason)

[<Theory>]
[<InlineData("../wc")>]
[<InlineData("wc/nested")>]
[<InlineData("/tmp/wc")>]
let ``tryParseManifest rejects plugin names that are not single safe segments`` name =
    let path =
        writeTemp
            $"""{{
              "name": "{name}",
              "version": "0.1.0",
              "apiVersion": "1",
              "entryAssembly": "wc.dll",
              "entryType": "Wc.Plugin"
            }}"""

    match Plugins.tryParseManifest path with
    | Result.Ok _ -> Assert.Fail "expected Error for unsafe plugin name"
    | Result.Error reason -> Assert.Contains("plugin name", reason)

[<Fact>]
let ``tryParseManifest rejects entryAssembly paths`` () =
    let path =
        writeTemp
            """{
              "name": "wc",
              "version": "0.1.0",
              "apiVersion": "1",
              "entryAssembly": "../wc.dll",
              "entryType": "Wc.Plugin"
            }"""

    match Plugins.tryParseManifest path with
    | Result.Ok _ -> Assert.Fail "expected Error for unsafe entryAssembly"
    | Result.Error reason -> Assert.Contains("entryAssembly", reason)

[<Fact>]
let ``tryParseManifest surfaces malformed JSON as Error`` () =
    let path = writeTemp "{ not valid json"

    match Plugins.tryParseManifest path with
    | Result.Ok _ -> Assert.Fail "expected Error for malformed JSON"
    | Result.Error reason -> Assert.Contains("failed to parse plugin.json", reason)

// ---------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------

let private mkPluginRoot (subdirs: (string * (string * string) list) list) =
    let root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory root |> ignore

    for name, files in subdirs do
        let pluginDir = Path.Combine(root, name)
        Directory.CreateDirectory pluginDir |> ignore

        for filename, contents in files do
            File.WriteAllText(Path.Combine(pluginDir, filename), contents)

    root

[<Fact>]
let ``discover returns empty when the plugins root does not exist`` () =
    let nonexistent = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let plugins = Plugins.discover nonexistent
    Assert.Empty plugins

[<Fact>]
let ``discover finds plugin folders with a manifest`` () =
    let manifest =
        """{ "name":"alpha","version":"0.1.0","apiVersion":"1",
             "entryAssembly":"alpha.dll","entryType":"Alpha.Plugin" }"""

    let root =
        mkPluginRoot [ "alpha", [ "plugin.json", manifest; "Plugin.fs", "module Alpha.Plugin" ] ]

    let plugins = Plugins.discover root
    Assert.Equal(1, plugins.Length)
    Assert.Equal("alpha", plugins.[0].Manifest.Name)
    Assert.Equal(PluginLoadStatus.Disabled, plugins.[0].Status) // resolved later by scanAndLoad

[<Fact>]
let ``discover skips folders without a manifest`` () =
    let root = mkPluginRoot [ "ignored", [ "README.md", "no manifest here" ] ]
    let plugins = Plugins.discover root
    Assert.Empty plugins

[<Fact>]
let ``discover surfaces malformed manifests as Failed plugins`` () =
    let root =
        mkPluginRoot [ "broken", [ "plugin.json", "{ not valid json"; "Plugin.fs", "module Broken.Plugin" ] ]

    let plugins = Plugins.discover root
    Assert.Equal(1, plugins.Length)

    match plugins.[0].Status with
    | PluginLoadStatus.Failed reason -> Assert.Contains("failed to parse plugin.json", reason)
    | other -> Assert.Fail $"expected Failed, got {other}"

[<Fact>]
let ``discover skips staging directories`` () =
    let manifest =
        """{ "name":"alpha","version":"0.1.0","apiVersion":"1",
             "entryAssembly":"alpha.dll","entryType":"Alpha.Plugin" }"""

    let root =
        mkPluginRoot
            [ "alpha", [ "plugin.json", manifest ]
              ".staging-1234", [ "plugin.json", manifest ] ]

    let plugins = Plugins.discover root
    Assert.Equal(1, plugins.Length)
    Assert.Equal("alpha", plugins.[0].Manifest.Name)

// ---------------------------------------------------------------------------
// Build staleness
// ---------------------------------------------------------------------------

[<Fact>]
let ``isBuildStale returns true when the DLL does not exist`` () =
    let root = mkPluginRoot [ "beta", [ "Plugin.fs", "module Beta.Plugin" ] ]
    let pluginDir = Path.Combine(root, "beta")
    Assert.True(Plugins.isBuildStale pluginDir "beta.dll")

[<Fact>]
let ``isBuildStale returns true when an .fs file is newer than the DLL`` () =
    let root = mkPluginRoot [ "beta", [ "Plugin.fs", "module Beta.Plugin" ] ]
    let pluginDir = Path.Combine(root, "beta")
    let binDir = Path.Combine(pluginDir, "bin", "Release", "net10.0")
    Directory.CreateDirectory binDir |> ignore
    let dllPath = Path.Combine(binDir, "beta.dll")
    File.WriteAllBytes(dllPath, [||])
    // Touch the source so its mtime is strictly newer than the DLL.
    System.Threading.Thread.Sleep 25
    File.WriteAllText(Path.Combine(pluginDir, "Plugin.fs"), "module Beta.Plugin // touched")
    Assert.True(Plugins.isBuildStale pluginDir "beta.dll")

// ---------------------------------------------------------------------------
// Command parsing for the `plugin` built-in
// ---------------------------------------------------------------------------

[<Fact>]
let ``Commands.parse 'plugin list' yields Plugin("list","")`` () =
    match Commands.parse "plugin list" with
    | Ready(Command.Plugin("list", "")) -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``Commands.parse 'plugin install foo' carries the argument`` () =
    match Commands.parse "plugin install foo" with
    | Ready(Command.Plugin("install", "foo")) -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``Commands.parse bare 'plugin' is Pending`` () =
    match Commands.parse "plugin" with
    | Pending _ -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``Commands.parse rejects an unknown verb`` () =
    match Commands.parse "plugin nuke" with
    | Invalid msg -> Assert.Contains("Unknown plugin verb", msg)
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``Commands.parseWith routes plugin-registered names to PluginInvoke`` () =
    let pluginSpecs = Commands.pluginSpecs [ "wc", "Count words.", "wordcount" ]

    let combined = Commands.specs @ pluginSpecs

    match Commands.parseWith combined "wc some-arg" with
    | Ready(PluginInvoke("wordcount", "wc", "some-arg")) -> ()
    | other -> Assert.Fail $"unexpected: %A{other}"

[<Fact>]
let ``allSpecs excludes plugin names that collide with builtins`` () =
    // "open" is a built-in; a plugin trying to register it should not
    // shadow the built-in spec.
    let specs = Commands.allSpecs [ "open", "Plugin trying to shadow", "evil" ]

    let opens = specs |> List.filter (fun s -> s.Name = "open")

    Assert.Equal(1, opens.Length)
    Assert.DoesNotContain("evil", opens.[0].Summary)

[<Fact>]
let ``uninstall rejects names that escape the plugin root`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let victim = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore
    Directory.CreateDirectory victim |> ignore

    try
        let ex =
            Assert.Throws<System.Exception>(fun () -> Plugins.uninstall pluginsRoot victim)

        Assert.Contains("plugin name", ex.Message)
        Assert.True(Directory.Exists victim)
    finally
        if Directory.Exists pluginsRoot then
            Directory.Delete(pluginsRoot, recursive = true)

        if Directory.Exists victim then
            Directory.Delete(victim, recursive = true)

// ---------------------------------------------------------------------------
// End-to-end: build and load the wordcount example through scanAndLoad
// ---------------------------------------------------------------------------

let private repoRoot =
    let rec walk (dir: string) =
        if File.Exists(Path.Combine(dir, "Fedit.slnx")) then
            dir
        else
            match Path.GetDirectoryName dir with
            | null -> failwith "could not locate repo root from test bin dir"
            | parent when parent = dir -> failwith "could not locate repo root from test bin dir"
            | parent -> walk parent

    walk System.AppContext.BaseDirectory

let private apiDllPath =
    Path.Combine(System.AppContext.BaseDirectory, "Fedit.PluginApi.dll")

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

[<Fact>]
let ``scanAndLoad builds and loads the wordcount example end-to-end`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore

    let source = Path.Combine(repoRoot, "examples", "wordcount")
    let target = Path.Combine(pluginsRoot, "wordcount")
    copyDir source target

    let messages = System.Collections.Concurrent.ConcurrentQueue<string>()
    let log (s: string) = messages.Enqueue s

    let registry = Plugins.scanAndLoad pluginsRoot apiDllPath Set.empty log

    Assert.True(registry.Loaded.ContainsKey "wordcount", "expected wordcount in registry.Loaded")

    let plugin = registry.Loaded.["wordcount"]

    match plugin.Status with
    | PluginLoadStatus.Loaded -> ()
    | other -> Assert.Fail $"expected Loaded, got {other}"

    Assert.Contains(plugin.Commands, fun cmd -> cmd.Name = "wc")
    Assert.True(registry.Commands.ContainsKey "wc")

    try
        Directory.Delete(pluginsRoot, recursive = true)
    with _ ->
        ()

[<Fact>]
let ``scanAndLoad builds and loads the todo-list example end-to-end`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore

    let source = Path.Combine(repoRoot, "examples", "todo-list")
    let target = Path.Combine(pluginsRoot, "todo-list")
    copyDir source target

    let messages = System.Collections.Concurrent.ConcurrentQueue<string>()
    let log (s: string) = messages.Enqueue s

    let registry = Plugins.scanAndLoad pluginsRoot apiDllPath Set.empty log

    Assert.True(registry.Loaded.ContainsKey "todo-list", "expected todo-list in registry.Loaded")

    let plugin = registry.Loaded.["todo-list"]

    match plugin.Status with
    | PluginLoadStatus.Loaded -> ()
    | other -> Assert.Fail $"expected Loaded, got {other}"

    Assert.Contains(plugin.Commands, fun cmd -> cmd.Name = "todolist")
    Assert.Contains(plugin.Commands, fun cmd -> cmd.Name = "todo-jump")
    Assert.True(registry.Commands.ContainsKey "todolist")
    Assert.True(registry.Commands.ContainsKey "todo-jump")

    try
        Directory.Delete(pluginsRoot, recursive = true)
    with _ ->
        ()

[<Fact>]
let ``scanAndLoad surfaces conflicts collected inside one plugin`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let pluginDir = Path.Combine(pluginsRoot, "conflicter")
    Directory.CreateDirectory pluginDir |> ignore

    File.WriteAllText(
        Path.Combine(pluginDir, "plugin.json"),
        """{ "name":"conflicter","version":"0.1.0","apiVersion":"1",
             "entryAssembly":"conflicter.dll","entryType":"Conflicter.Plugin" }"""
    )

    File.WriteAllText(
        Path.Combine(pluginDir, "Plugin.fs"),
        """namespace Conflicter

open Fedit.PluginApi

type Plugin =
    static member register(host: IPluginHost) =
        let cmd =
            { Name = "dup"
              Usage = ""
              Summary = "duplicate"
              Run = fun _ -> [] }

        host.RegisterCommand cmd
        host.RegisterCommand cmd
        host.RegisterKeybinding(KeyChord.Char 'x', "dup")
"""
    )

    let messages = System.Collections.Concurrent.ConcurrentQueue<string>()
    let log (s: string) = messages.Enqueue s
    let registry = Plugins.scanAndLoad pluginsRoot apiDllPath Set.empty log

    try
        Assert.Contains(registry.Conflicts, fun c -> c.Contains("duplicate command 'dup'"))
        Assert.Contains(registry.Conflicts, fun c -> c.Contains("reserved chord"))
    finally
        if Directory.Exists pluginsRoot then
            Directory.Delete(pluginsRoot, recursive = true)

[<Fact>]
let ``scanAndLoad leaves disabled plugins unloaded and unregistered`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let pluginDir = Path.Combine(pluginsRoot, "alpha")
    Directory.CreateDirectory pluginDir |> ignore

    File.WriteAllText(
        Path.Combine(pluginDir, "plugin.json"),
        """{ "name":"alpha","version":"0.1.0","apiVersion":"1",
             "entryAssembly":"alpha.dll","entryType":"Alpha.Plugin" }"""
    )

    File.WriteAllText(
        Path.Combine(pluginDir, "Plugin.fs"),
        """namespace Alpha

open Fedit.PluginApi

type Plugin =
    static member register(host: IPluginHost) =
        host.RegisterCommand { Name = "alpha"; Usage = ""; Summary = ""; Run = fun _ -> [] }
"""
    )

    let messages = System.Collections.Concurrent.ConcurrentQueue<string>()
    let log (s: string) = messages.Enqueue s

    let registry =
        Plugins.scanAndLoad pluginsRoot apiDllPath (Set.ofList [ "alpha" ]) log

    try
        match registry.Loaded.["alpha"].Status with
        | PluginLoadStatus.Disabled -> ()
        | other -> Assert.Fail $"expected Disabled, got {other}"

        Assert.False(registry.Commands.ContainsKey "alpha")
        Assert.Empty(registry.Keybindings)
        Assert.DoesNotContain("alpha", registry.Enabled)
    finally
        if Directory.Exists pluginsRoot then
            Directory.Delete(pluginsRoot, recursive = true)

// ---------------------------------------------------------------------------
// Plugin dispatch plumbing
// ---------------------------------------------------------------------------

/// Drives a single `Editor.update` dispatch with a custom registry. `setup`
/// shapes the freshly-initialized model; `registry` replaces the default
/// empty plugin registry.
let private dispatchWithRegistry
    (setup: Model -> Model)
    (registry: PluginRegistry)
    (msg: Msg)
    : Model * Effect list =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

    let prepared = setup { model with Plugins = registry }
    Editor.update msg prepared

/// Drives a synthetic plugin binding through `Editor.update`. `setup` shapes
/// the freshly-initialized model (root "/root", 80×24); `run` is the probe
/// plugin's body. Dispatches Ctrl+J, which is not reserved by the top-level
/// KeyPressed handler and so falls through to plugin keybinding dispatch in
/// `runEditor`.
let private dispatchProbe
    (setup: Model -> Model)
    (run: Fedit.PluginApi.PluginContext -> Fedit.PluginApi.PluginAction list)
    : Model * Effect list =
    let spec: Fedit.PluginApi.PluginCommand =
        { Name = "probe"
          Usage = ""
          Summary = ""
          Run = run }

    let binding: PluginCommandBinding = { Source = "probe-plugin"; Spec = spec }

    let registry =
        { PluginRegistry.empty with
            Commands = Map.ofList [ "probe", binding ]
            Keybindings = [ Fedit.PluginApi.KeyChord.Ctrl 'j', "probe" ] }

    dispatchWithRegistry setup registry
        (KeyPressed
            { Mods = Set.ofList [ Ctrl ]
              Key = Key.Char 'j' })

/// Add `buffer` to the model's open set and make it active.
let private withActiveBuffer (buffer: BufferState) (model: Model) =
    { model with
        Editors =
            { model.Editors with
                Buffers = model.Editors.Buffers |> Map.add buffer.Id buffer
                ActiveBufferId = buffer.Id } }

/// Add `opened` to the model's buffer set while keeping `probe` active.
let private withOpenBuffer (opened: BufferState) (probe: BufferState) (model: Model) =
    let withProbe = withActiveBuffer probe model

    { withProbe with
        Editors =
            { withProbe.Editors with
                Buffers = withProbe.Editors.Buffers |> Map.add opened.Id opened } }

let private sampleWorkspaceTree () : FileNode =
    { Path = "/root"
      Name = "root"
      IsDirectory = true
      Children =
        [ { Path = "/root/src"
            Name = "src"
            IsDirectory = true
            Children =
              [ { Path = "/root/src/a.fs"
                  Name = "a.fs"
                  IsDirectory = false
                  Children = [] } ] } ] }

/// Run a fixed plugin-action list with `buffer` active.
let private runActionsFor buffer actions =
    dispatchProbe (withActiveBuffer buffer) (fun _ -> actions)

/// Capture the `PluginContext` the host hands to the probe's `Run`.
let private captureCtx (setup: Model -> Model) =
    let captured = ref None

    dispatchProbe setup (fun ctx ->
        captured.Value <- Some ctx
        [])
    |> ignore

    captured.Value

let private captureCtxFor (buffer: BufferState) = captureCtx (withActiveBuffer buffer)

// ---------------------------------------------------------------------------
// PluginContext snapshot: Selection field
// ---------------------------------------------------------------------------

[<Fact>]
let ``toPluginContext leaves Selection None when the buffer has no selection`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    match captureCtxFor buffer with
    | Some ctx -> Assert.Equal(None, ctx.ActiveBuffer.Selection)
    | None -> Assert.Fail "plugin Run was not invoked"

[<Fact>]
let ``toPluginContext surfaces forward selection as 1-based start->end`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"
    // Anchor at offset 0, cursor at offset 5 (just past "hello"). Forward drag.
    let withSel =
        { buffer with
            Selection = Some { Anchor = 0; Head = 5 }
            Cursor = { Line = 0; Column = 5 } }

    match captureCtxFor withSel with
    | Some ctx ->
        match ctx.ActiveBuffer.Selection with
        | Some(startPos, endPos) ->
            Assert.Equal(1, startPos.Line)
            Assert.Equal(1, startPos.Column)
            Assert.Equal(1, endPos.Line)
            Assert.Equal(6, endPos.Column) // 1-based: column 5 + 1
        | None -> Assert.Fail "expected Selection to be Some"
    | None -> Assert.Fail "plugin Run was not invoked"

[<Fact>]
let ``toPluginContext orders selection start->end regardless of drag direction`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"
    // Anchor at offset 5, cursor at offset 0. Reverse drag — caret is BEFORE
    // the anchor. The plugin should still see (0, 5) in document order.
    let withSel =
        { buffer with
            Selection = Some { Anchor = 5; Head = 0 }
            Cursor = { Line = 0; Column = 0 } }

    match captureCtxFor withSel with
    | Some ctx ->
        match ctx.ActiveBuffer.Selection with
        | Some(startPos, endPos) ->
            Assert.Equal(1, startPos.Column)
            Assert.Equal(6, endPos.Column)
        | None -> Assert.Fail "expected Selection to be Some"
    | None -> Assert.Fail "plugin Run was not invoked"

[<Fact>]
let ``toPluginContext crosses line boundaries with 1-based line numbers`` () =
    // Two lines: "ab\ncd" — offsets 0..4. Select from offset 1 ("b") through
    // offset 4 (just past "d"), spanning the newline.
    let buffer = Buffer.fromText 7 None "scratch" "ab\ncd" "\n"

    let withSel =
        { buffer with
            Selection = Some { Anchor = 1; Head = 5 }
            Cursor = { Line = 1; Column = 2 } }

    match captureCtxFor withSel with
    | Some ctx ->
        match ctx.ActiveBuffer.Selection with
        | Some(startPos, endPos) ->
            Assert.Equal(1, startPos.Line)
            Assert.Equal(2, startPos.Column)
            Assert.Equal(2, endPos.Line)
            Assert.Equal(3, endPos.Column)
        | None -> Assert.Fail "expected Selection to be Some"
    | None -> Assert.Fail "plugin Run was not invoked"

// ---------------------------------------------------------------------------
// Plugin actions (API v1.1): editing, buffers, workspace
// ---------------------------------------------------------------------------

let private pos line column : Fedit.PluginApi.CursorPosition = { Line = line; Column = column }

let private activeBuffer (model: Model) =
    model.Editors.Buffers[model.Editors.ActiveBufferId]

[<Fact>]
let ``ReplaceRange replaces between 1-based positions as one undo entry`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ =
        runActionsFor buffer [ Fedit.PluginApi.ReplaceRange(pos 1 1, pos 1 6, "goodbye") ]

    let result = activeBuffer next
    Assert.Equal("goodbye world", Buffer.text result)
    Assert.Equal(1, result.Undo.Length)
    Assert.Equal(({ Line = 0; Column = 7 }: Position), result.Cursor)

[<Fact>]
let ``ReplaceRange clamps out-of-range coordinates`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello\nworld" "\n"

    let next, _ =
        runActionsFor buffer [ Fedit.PluginApi.ReplaceRange(pos 1 1, pos 99 99, "x") ]

    Assert.Equal("x", Buffer.text (activeBuffer next))

[<Fact>]
let ``ReplaceRange swaps ends when from is after to`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ =
        runActionsFor buffer [ Fedit.PluginApi.ReplaceRange(pos 1 6, pos 1 1, "bye") ]

    Assert.Equal("bye world", Buffer.text (activeBuffer next))

[<Fact>]
let ``ClearSelection collapses the selection without editing`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let withSel =
        { buffer with
            Selection = Some { Anchor = 0; Head = 5 } }

    let next, _ = runActionsFor withSel [ Fedit.PluginApi.ClearSelection ]

    let result = activeBuffer next
    Assert.Equal(None, result.Selection)
    Assert.Equal("hello world", Buffer.text result)
    Assert.Equal(buffer.EditTick, result.EditTick)

[<Fact>]
let ``ClearSelection is a no-op without a selection`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ = runActionsFor buffer [ Fedit.PluginApi.ClearSelection ]

    let result = activeBuffer next
    Assert.Equal(None, result.Selection)
    Assert.Equal("hello world", Buffer.text result)
    Assert.Equal(buffer.EditTick, result.EditTick)

[<Fact>]
let ``DeleteSelection removes the selected text`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let withSel =
        { buffer with
            Selection = Some { Anchor = 0; Head = 5 } }

    let next, _ = runActionsFor withSel [ Fedit.PluginApi.DeleteSelection ]

    let result = activeBuffer next
    Assert.Equal(" world", Buffer.text result)
    Assert.Equal(None, result.Selection)
    Assert.Equal(1, result.Undo.Length)

[<Fact>]
let ``DeleteSelection is a no-op without a selection`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ = runActionsFor buffer [ Fedit.PluginApi.DeleteSelection ]

    let result = activeBuffer next
    Assert.Equal("hello world", Buffer.text result)
    Assert.Equal(buffer.EditTick, result.EditTick)

[<Fact>]
let ``SwitchBuffer activates a known buffer id`` () =
    // init seeds scratch buffer 1; the probe buffer (7) is active.
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ = runActionsFor buffer [ Fedit.PluginApi.SwitchBuffer 1 ]

    Assert.Equal(1, next.Editors.ActiveBufferId)

[<Fact>]
let ``SwitchBuffer notifies an error for an unknown id`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ = runActionsFor buffer [ Fedit.PluginApi.SwitchBuffer 99 ]

    Assert.Equal(7, next.Editors.ActiveBufferId)

    match next.Notification with
    | Some notif ->
        Assert.Equal(Severity.Error, notif.Severity)
        Assert.Contains("Unknown buffer", notif.Message)
    | None -> Assert.Fail "expected an error notification"

[<Fact>]
let ``NewBuffer creates an active scratch buffer and bumps NextBufferId`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ = runActionsFor buffer [ Fedit.PluginApi.NewBuffer("notes", "hi") ]

    let result = activeBuffer next
    Assert.Equal("notes", result.Name)
    Assert.Equal(None, result.FilePath)
    Assert.Equal("hi", Buffer.text result)
    // init's NextBufferId is 2; the probe buffer is grafted in as id 7
    // without bumping it, so the new buffer takes id 2 and bumps to 3.
    Assert.Equal(2, result.Id)
    Assert.Equal(3, next.Editors.NextBufferId)
    Assert.Equal(None, next.Editors.PreviewBufferId)
    Assert.Equal(3, next.Editors.Buffers.Count)

[<Fact>]
let ``NewBuffer defaults an empty name to plugin`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ = runActionsFor buffer [ Fedit.PluginApi.NewBuffer("  ", "") ]

    Assert.Equal("plugin", (activeBuffer next).Name)

[<Fact>]
let ``NewBuffer makes later actions in the list target the new buffer`` () =
    let buffer = Buffer.fromText 7 None "scratch" "original" "\n"

    let next, _ =
        runActionsFor buffer [ Fedit.PluginApi.NewBuffer("notes", ""); Fedit.PluginApi.InsertText "hello" ]

    Assert.Equal("hello", Buffer.text (activeBuffer next))
    Assert.Equal("original", Buffer.text next.Editors.Buffers[7])

[<Fact>]
let ``SetBufferActivation registers on the active buffer`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ =
        runActionsFor
            buffer
            [ Fedit.PluginApi.NewBuffer("notes", "")
              Fedit.PluginApi.SetBufferActivation "probe" ]

    match Map.tryFind next.Editors.ActiveBufferId next.Editors.BufferActivations with
    | Some("probe-plugin", "probe") -> ()
    | other -> Assert.Fail $"expected activation, got %A{other}"

[<Fact>]
let ``OpenFileAt emits LoadFile with target for a new path`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let setup model =
        { withActiveBuffer buffer model with
            Workspace = Workspace.setTree (sampleWorkspaceTree ()) model.Workspace }

    let _, effects =
        dispatchProbe setup (fun _ ->
            [ Fedit.PluginApi.OpenFileAt(
                  "src/a.fs",
                  { Line = 2; Column = 3 },
                  false
              ) ])

    Assert.Contains(
        LoadFile("/root/src/a.fs", OpenPermanent, Some { Line = 1; Column = 2 }),
        effects
    )

[<Fact>]
let ``OpenFileAt activates an existing buffer and applies the target`` () =
    let probe = Buffer.fromText 7 None "scratch" "hello" "\n"

    let opened =
        Buffer.fromText 9 (Some "/root/a.fs") "a.fs" "line one\nline two\n" "\n"

    let setup model = withOpenBuffer opened probe model

    let next, effects =
        dispatchProbe setup (fun _ ->
            [ Fedit.PluginApi.OpenFileAt(
                  "a.fs",
                  { Line = 2; Column = 3 },
                  false
              ) ])

    Assert.Equal(9, next.Editors.ActiveBufferId)
    Assert.True(effects |> List.isEmpty)
    let active = activeBuffer next
    Assert.Equal({ Line = 1; Column = 2 }, active.Cursor)

[<Fact>]
let ``OpenFileAt preview opens into the preview slot`` () =
    let probe = Buffer.fromText 7 None "scratch" "hello" "\n"

    let setup model =
        { withActiveBuffer probe model with
            Workspace = Workspace.setTree (sampleWorkspaceTree ()) model.Workspace }

    let _, effects =
        dispatchProbe setup (fun _ ->
            [ Fedit.PluginApi.OpenFileAt(
                  "src/a.fs",
                  { Line = 1; Column = 1 },
                  true
              ) ])

    Assert.Contains(
        LoadFile("/root/src/a.fs", OpenPreview, Some { Line = 0; Column = 0 }),
        effects
    )

[<Fact>]
let ``Enter in an activated buffer runs the registered plugin command`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let activateSpec: Fedit.PluginApi.PluginCommand =
        { Name = "activate"
          Usage = ""
          Summary = ""
          Run = fun _ -> [ Fedit.PluginApi.InsertText "d" ] }

    let setupSpec: Fedit.PluginApi.PluginCommand =
        { Name = "setup"
          Usage = ""
          Summary = ""
          Run = fun _ ->
            [ Fedit.PluginApi.NewBuffer("notes", "")
              Fedit.PluginApi.SetBufferActivation "activate" ] }

    let registry =
        { PluginRegistry.empty with
            Commands =
                Map.ofList
                    [ "activate", { Source = "test-plugin"; Spec = activateSpec }
                      "setup", { Source = "test-plugin"; Spec = setupSpec } ]
            Keybindings = [ Fedit.PluginApi.KeyChord.Ctrl 'j', "setup" ] }

    let setup model = withActiveBuffer buffer model

    let activated, _ =
        dispatchWithRegistry setup registry
            (KeyPressed { Mods = Set.ofList [ Ctrl ]; Key = Key.Char 'j' })

    let next, _ =
        Editor.update
            (KeyPressed { Mods = Set.empty; Key = Key.Named Enter })
            activated

    Assert.Equal("d", Buffer.text (activeBuffer next))

[<Fact>]
let ``OpenFilePreview emits LoadFile with preview intent for a new path`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let setup model =
        { withActiveBuffer buffer model with
            Workspace = Workspace.setTree (sampleWorkspaceTree ()) model.Workspace }

    let _, effects =
        dispatchProbe setup (fun _ -> [ Fedit.PluginApi.OpenFilePreview "src/a.fs" ])

    Assert.Contains(LoadFile("/root/src/a.fs", OpenPreview, None), effects)

[<Fact>]
let ``OpenFilePreview activates an already-open buffer without loading`` () =
    let probe = Buffer.fromText 7 None "scratch" "hello" "\n"
    let opened = Buffer.fromText 9 (Some "/root/a.fs") "a.fs" "contents" "\n"

    let setup model = withOpenBuffer opened probe model

    let next, effects =
        dispatchProbe setup (fun _ -> [ Fedit.PluginApi.OpenFilePreview "a.fs" ])

    Assert.Equal(9, next.Editors.ActiveBufferId)

    Assert.True(
        effects
        |> List.forall (fun effect ->
            match effect with
            | LoadFile _ -> false
            | _ -> true),
        "expected no LoadFile effect for an already-open buffer"
    )

[<Fact>]
let ``RevealPath shows the sidebar and selects the path without stealing focus`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let setup model =
        { withActiveBuffer buffer model with
            Workspace = Workspace.setTree (sampleWorkspaceTree ()) model.Workspace
            Panels =
                { model.Panels with
                    SidebarVisible = false } }

    let next, _ =
        dispatchProbe setup (fun _ -> [ Fedit.PluginApi.RevealPath "src/a.fs" ])

    Assert.True next.Panels.SidebarVisible
    Assert.Equal(Some "/root/src/a.fs", next.Workspace.SelectedPath)
    Assert.Contains("/root/src", next.Workspace.Expanded)
    Assert.Equal(FocusTarget.Editor, next.Focus)

[<Fact>]
let ``RevealPath outside the workspace is a no-op`` () =
    let tree: FileNode =
        { Path = "/root"
          Name = "root"
          IsDirectory = true
          Children = [] }

    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let setup model =
        { withActiveBuffer buffer model with
            Workspace = Workspace.setTree tree model.Workspace
            Panels =
                { model.Panels with
                    SidebarVisible = false } }

    let next, _ =
        dispatchProbe setup (fun _ -> [ Fedit.PluginApi.RevealPath "/elsewhere/x.fs" ])

    Assert.False next.Panels.SidebarVisible
    // setTree auto-selects the root; the failed reveal must not move it.
    Assert.Equal(Some "/root", next.Workspace.SelectedPath)

[<Fact>]
let ``toPluginContext carries workspace SelectedPath and Files`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let setup model =
        { withActiveBuffer buffer model with
            Workspace =
                { model.Workspace with
                    SelectedPath = Some "/root/picked.fs"
                    Files = [ "a.fs"; "sub/b.fs" ] } }

    match captureCtx setup with
    | Some ctx ->
        Assert.Equal(Some "/root/picked.fs", ctx.Workspace.SelectedPath)
        Assert.Equal<string list>([ "a.fs"; "sub/b.fs" ], ctx.Workspace.Files)
    | None -> Assert.Fail "plugin Run was not invoked"
