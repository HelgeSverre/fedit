namespace Fedit

open System
open System.IO
open System.Diagnostics
open Fedit.PluginApi

/// Editor-side handle to the out-of-process plugin host. Spawns the host child
/// lazily, then talks newline-delimited JSON-RPC over its stdio. Requests are
/// serialized under a lock (one outstanding request at a time), which matches
/// how the editor drives it — from thread-pool effects, never per-frame.
///
/// `hostPath` is either the host apphost binary (shipped beside an AOT editor)
/// or its `.dll` (run via `dotnet` during development); the extension decides.
type PluginHostClient(hostPath: string) =
    let gate = obj ()
    let mutable proc: Process option = None

    let makeStartInfo () =
        let psi =
            if hostPath.EndsWith(".dll", StringComparison.Ordinal) then
                let p = ProcessStartInfo("dotnet")
                p.ArgumentList.Add hostPath
                p
            else
                ProcessStartInfo(hostPath)

        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi

    /// Start the child if not already running (or if it has exited).
    let ensure () : Process =
        match proc with
        | Some p when not p.HasExited -> p
        | _ ->
            match Process.Start(makeStartInfo ()) with
            | null -> failwith "failed to start plugin host"
            | p ->
                proc <- Some p
                p

    /// One request -> one response, serialized. Returns Error on a dead host.
    let roundtrip (request: string) : Result<string, string> =
        lock gate (fun () ->
            try
                let p = ensure ()
                PluginProtocol.writeFrame p.StandardInput request

                match PluginProtocol.readFrame p.StandardOutput with
                | Some line -> Result.Ok line
                | None -> Result.Error "plugin host closed the connection"
            with ex ->
                Result.Error("plugin host error: " + ex.Message))

    /// Discover/build/load plugins under `pluginsRoot`, returning their specs.
    member _.Scan(pluginsRoot: string, disabled: Set<string>) : Result<PluginProtocol.ScanResult, string> =
        match roundtrip (PluginProtocol.scanRequest pluginsRoot disabled) with
        | Result.Ok line -> PluginProtocol.parseScanResult line
        | Result.Error e -> Result.Error e

    /// Run a registered command against `ctx`, returning its PluginAction list.
    member _.Invoke(command: string, ctx: PluginContext) : Result<PluginAction list, string> =
        match roundtrip (PluginProtocol.invokeRequest command ctx) with
        | Result.Ok line -> PluginProtocol.parseInvokeResult line
        | Result.Error e -> Result.Error e

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                match proc with
                | Some p when not p.HasExited ->
                    try
                        PluginProtocol.writeFrame p.StandardInput PluginProtocol.shutdownRequest
                        p.WaitForExit 1000 |> ignore
                    with _ ->
                        ()

                    try
                        if not p.HasExited then
                            p.Kill()
                    with _ ->
                        ()
                | _ -> ()

                proc <- None)

/// Locate the host beside the running editor binary, preferring the native
/// apphost (AOT/self-contained ship) and falling back to the framework dll.
[<RequireQualifiedAccess>]
module PluginHostClient =

    let defaultHostPath () : string =
        let dir = AppContext.BaseDirectory
        let apphost = Path.Combine(dir, "Fedit.PluginHost")
        let dll = Path.Combine(dir, "Fedit.PluginHost.dll")

        if File.Exists apphost then apphost
        elif File.Exists dll then dll
        else apphost

    /// Hidden self-test: spawn the host, scan a plugins dir, invoke `wc`, print
    /// the result. Runs inside the AOT binary to prove the client spawns a
    /// child and round-trips RPC where reflective JSON would crash.
    let selfTest (pluginsRoot: string) (hostPath: string) : bool =
        use client = new PluginHostClient(hostPath)

        match client.Scan(pluginsRoot, Set.empty) with
        | Result.Error e ->
            Console.Error.WriteLine("scan failed: " + e)
            false
        | Result.Ok scan ->
            Console.Error.WriteLine(
                "scanned commands: "
                + String.Join(", ", scan.Specs |> List.map (fun s -> s.Name))
            )

            let ctx: PluginContext =
                { ActiveBuffer =
                    { Id = 1
                      Name = "a.txt"
                      FilePath = None
                      Text = "one two three"
                      Cursor = { Line = 1; Column = 1 }
                      Selection = None }
                  AllBuffers = []
                  Workspace =
                    { RootPath = "/tmp"
                      SelectedPath = None
                      Files = [] } }

            match client.Invoke("wc", ctx) with
            | Result.Error e ->
                Console.Error.WriteLine("invoke failed: " + e)
                false
            | Result.Ok actions ->
                match actions with
                | [ Notify(Info, msg) ] when msg = "3 words" ->
                    Console.Error.WriteLine("invoke wc -> " + msg)
                    true
                | other ->
                    Console.Error.WriteLine("unexpected actions: " + string (List.length other))
                    false
