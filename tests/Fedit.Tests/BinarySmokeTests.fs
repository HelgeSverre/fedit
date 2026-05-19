module Fedit.Tests.BinarySmokeTests

open System
open System.Diagnostics
open System.IO
open Xunit
open FsUnit.Xunit

// Phase 8: Tier 3 binary smoke. These tests cross the Runtime.run boundary
// that Tier 1 (unit) and Tier 2 (snapshot) can't reach. They spawn the
// actual built binary via `dotnet exec` and assert on exit code / stdout.
//
// Interactive scenarios (cold-start → Ctrl+Q quit, etc.) need a real PTY
// and are deferred to Phase 19's VHS investigation. These smoke tests
// only exercise short-circuit code paths that don't require an
// interactive console (--help, --version).

let private repoRoot =
    let mutable dir = DirectoryInfo(AppContext.BaseDirectory)

    while dir <> null && not (File.Exists(Path.Combine(dir.FullName, "Fedit.slnx"))) do
        dir <- dir.Parent

    if dir = null then
        failwith "Could not locate repo root from test base directory"

    dir.FullName

let private feditProject =
    Path.Combine(repoRoot, "src", "Fedit", "Fedit.fsproj")

let private runFedit (args: string list) =
    let info = ProcessStartInfo("dotnet")
    info.ArgumentList.Add("run")
    info.ArgumentList.Add("--project")
    info.ArgumentList.Add(feditProject)
    info.ArgumentList.Add("--no-build")
    info.ArgumentList.Add("--")

    for arg in args do
        info.ArgumentList.Add(arg)

    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false

    use proc = Process.Start info
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    let exited = proc.WaitForExit 15000

    if not exited then
        try
            proc.Kill()
        with _ ->
            ()

        failwith "fedit did not exit within 15 seconds"

    proc.ExitCode, stdout, stderr

[<Trait("Category", "slow")>]
[<Fact>]
let ``--help exits 0 and prints usage`` () =
    let exitCode, stdout, _ = runFedit [ "--help" ]
    exitCode |> should equal 0
    stdout |> should haveSubstring "Usage: fedit"
    stdout |> should haveSubstring "--log"

[<Trait("Category", "slow")>]
[<Fact>]
let ``-h short flag works the same as --help`` () =
    let exitCode, stdout, _ = runFedit [ "-h" ]
    exitCode |> should equal 0
    stdout |> should haveSubstring "Usage: fedit"

[<Trait("Category", "slow")>]
[<Fact>]
let ``--version exits 0 and prints version string`` () =
    let exitCode, stdout, _ = runFedit [ "--version" ]
    exitCode |> should equal 0
    stdout |> should haveSubstring "fedit"
