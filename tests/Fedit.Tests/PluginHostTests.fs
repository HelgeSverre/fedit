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
          Selection = None }
      AllBuffers = []
      Workspace =
        { RootPath = "/tmp"
          SelectedPath = None
          Files = [] } }

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
