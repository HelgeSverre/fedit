namespace Fedit

open System
open System.Collections.Concurrent
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Fedit.PluginApi

/// Editor-side handle to the out-of-process plugin host. Spawns the host child
/// lazily, then talks newline-delimited JSON-RPC over its stdio. Requests
/// carry ids and may be in flight together: a reader thread matches each
/// response to its waiter, so the editor's thread-pool effects can call
/// `Invoke` concurrently and a slow plugin never stalls a fast one.
///
/// `hostPath` is either the host apphost binary (shipped beside an AOT editor)
/// or its `.dll` (run via `dotnet` during development); the extension decides.
type PluginHostClient(hostPath: string) =
    let gate = obj ()
    let mutable proc: Process option = None
    let mutable nextId = 0
    let pending = ConcurrentDictionary<int, TaskCompletionSource<string>>()

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

    /// Every pending request fails when the host goes away.
    let failAll (reason: string) =
        for KeyValue(_, tcs) in pending do
            tcs.TrySetResult(PluginProtocol.errorJson 0 reason) |> ignore

        pending.Clear()

    /// Editor-side handler for host read-backs (`readClipboard`, ...):
    /// method name in, value or error out. Runs off the reader thread.
    let mutable onRequest: string -> Result<string, string> =
        fun method -> Result.Error $"unsupported request: {method}"

    let answer (p: Process) (json: string) =
        lock gate (fun () ->
            try
                PluginProtocol.writeFrame p.StandardInput json
            with _ ->
                ())

    /// Reader thread: match each response line to its request by id, and
    /// serve the host's own requests on the pool so reading never stalls.
    let pump (p: Process) =
        let thread =
            Thread(
                (fun () ->
                    let mutable alive = true

                    while alive do
                        match
                            (try
                                PluginProtocol.readFrame p.StandardOutput
                             with _ ->
                                 None)
                        with
                        | None -> alive <- false
                        | Some line ->
                            let id, request =
                                try
                                    use doc = JsonDocument.Parse line
                                    let root = doc.RootElement

                                    PluginProtocol.idOf root,
                                    (if PluginProtocol.isRequest root then
                                         Some(PluginProtocol.methodOf root)
                                     else
                                         None)
                                with _ ->
                                    0, None

                            match request with
                            | Some method ->
                                Task.Run(fun () ->
                                    let reply =
                                        match
                                            (try
                                                onRequest method
                                             with ex ->
                                                 Result.Error ex.Message)
                                        with
                                        | Ok value -> PluginProtocol.valueResultJson id value
                                        | Result.Error message -> PluginProtocol.errorJson id message

                                    answer p reply)
                                |> ignore
                            | None ->
                                match pending.TryRemove id with
                                | true, tcs -> tcs.TrySetResult line |> ignore
                                | _ -> ()

                    failAll "plugin host closed the connection"),
                IsBackground = true,
                Name = "fedit-plugin-host-reader"
            )

        thread.Start()

    /// Start the child if not already running (or if it has exited).
    let ensure () : Process =
        match proc with
        | Some p when not p.HasExited -> p
        | _ ->
            match Process.Start(makeStartInfo ()) with
            | null -> failwith "failed to start plugin host"
            | p ->
                proc <- Some p
                pump p
                p

    /// Send one request and wait for its response. Requests interleave —
    /// the host serves them concurrently — so a slow plugin command never
    /// blocks another; only the write is serialized.
    /// Cancelling `token` sends a cancel request; the host still answers
    /// (with "cancelled" unless the command finished first).
    let roundtrip (token: CancellationToken) (request: int -> string) : Result<string, string> =
        let id = Interlocked.Increment &nextId

        let tcs =
            TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

        pending[id] <- tcs

        let sent =
            lock gate (fun () ->
                try
                    let p = ensure ()
                    PluginProtocol.writeFrame p.StandardInput (request id)
                    Ok()
                with ex ->
                    Result.Error("plugin host error: " + ex.Message))

        match sent with
        | Result.Error e ->
            pending.TryRemove id |> ignore
            Result.Error e
        | Ok() ->
            use _registration =
                token.Register(fun () ->
                    lock gate (fun () ->
                        match proc with
                        | Some p when not p.HasExited ->
                            try
                                PluginProtocol.writeFrame p.StandardInput (PluginProtocol.cancelRequest 0 id)
                            with _ ->
                                ()
                        | _ -> ()))

            let line = tcs.Task.GetAwaiter().GetResult()

            if line.Contains "\"plugin host closed the connection\"" then
                Result.Error "plugin host closed the connection"
            else
                Ok line

    /// Set the handler for the host's read-back requests.
    member _.OnRequest
        with get () = onRequest
        and set (handler: string -> Result<string, string>) = onRequest <- handler

    /// Discover/build/load plugins under `pluginsRoot`, returning the registry
    /// (command Run closures are stubbed editor-side; invocation goes back to
    /// the host via Invoke).
    member _.Scan(pluginsRoot: string, disabled: Set<string>) : Result<PluginRegistry, string> =
        match roundtrip CancellationToken.None (fun id -> PluginProtocol.scanRequest id pluginsRoot disabled) with
        | Result.Ok line -> PluginProtocol.parseScanResult line
        | Result.Error e -> Result.Error e

    /// Run a registered command against `ctx`, returning its PluginAction
    /// list. Cancelling `token` asks the host to cancel the run.
    member _.Invoke(command: string, ctx: PluginContext, token: CancellationToken) : Result<PluginAction list, string> =
        match roundtrip token (fun id -> PluginProtocol.invokeRequest id command ctx) with
        | Result.Ok line -> PluginProtocol.parseInvokeResult line
        | Result.Error e -> Result.Error e

    member this.Invoke(command: string, ctx: PluginContext) : Result<PluginAction list, string> =
        this.Invoke(command, ctx, CancellationToken.None)

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

                proc <- None
                failAll "plugin host stopped")

/// Locate the host beside the running editor binary, preferring the native
/// apphost (AOT/self-contained ship) and falling back to the framework dll.
[<RequireQualifiedAccess>]
module PluginHostClient =

    let defaultHostPath () : string =
        let dir = AppContext.BaseDirectory
        // Production / shipped bundle: the host sits beside the editor (native
        // apphost preferred for an AOT/self-contained ship, else the dll run
        // via `dotnet`). Both the R2R release and `just aot` co-locate it.
        let beside =
            [ Path.Combine(dir, "Fedit.PluginHost")
              Path.Combine(dir, "Fedit.PluginHost.exe")
              Path.Combine(dir, "Fedit.PluginHost.dll") ]

        // Dev fallback: when run straight from the build tree
        // (src/Fedit/bin/<cfg>/net10.0[/<rid>]/), the host built by the
        // solution lives at src/Fedit.PluginHost/bin/<cfg>/net10.0/. Walk up
        // to the repo's `src/` and look there.
        let devFallback () =
            let rec findSrc (d: string) =
                if File.Exists(Path.Combine(d, "Fedit.slnx")) then
                    Some(Path.Combine(d, "src"))
                else
                    match Path.GetDirectoryName d with
                    | null -> None
                    | parent when parent = d -> None
                    | parent -> findSrc parent

            match findSrc dir with
            | None -> []
            | Some src ->
                [ "Debug"; "Release" ]
                |> List.map (fun cfg ->
                    Path.Combine(src, "Fedit.PluginHost", "bin", cfg, "net10.0", "Fedit.PluginHost.dll"))

        match (beside @ devFallback ()) |> List.tryFind File.Exists with
        | Some path -> path
        | None -> List.head beside

    /// Hidden self-test: spawn the host, scan a plugins dir, invoke `wc`, print
    /// the result. Runs inside the AOT binary to prove the client spawns a
    /// child and round-trips RPC where reflective JSON would crash.
    let selfTest (pluginsRoot: string) (hostPath: string) : bool =
        use client = new PluginHostClient(hostPath)

        match client.Scan(pluginsRoot, Set.empty) with
        | Result.Error e ->
            Console.Error.WriteLine("scan failed: " + e)
            false
        | Result.Ok registry ->
            Console.Error.WriteLine(
                "scanned commands: "
                + String.Join(", ", registry.Commands |> Map.toList |> List.map fst)
            )

            let ctx: PluginContext =
                { ActiveBuffer =
                    { Id = 1
                      Name = "a.txt"
                      FilePath = None
                      Text = "one two three"
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
