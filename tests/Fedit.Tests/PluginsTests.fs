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

    let registry = Plugins.scanAndLoad pluginsRoot apiDllPath Map.empty log

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
