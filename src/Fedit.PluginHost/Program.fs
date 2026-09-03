module Fedit.PluginHost.Program

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Fedit
open Fedit.PluginApi

/// Out-of-process plugin host. Reads newline-delimited JSON-RPC requests on
/// stdin, serves them, writes responses on stdout. stderr is the log channel.
///
/// Holds the loaded PluginRegistry (with each command's `Run` closure) in this
/// JIT process so the editor can stay NativeAOT — only command SPECS and
/// PluginAction results cross the wire.
///
/// Requests carry an `id` and are served concurrently: each one runs on the
/// pool and answers when done, so a slow command never stalls another. The
/// response writer is the only serialized point. `cancel` fires the token
/// of an in-flight request; `shutdown` cancels everything and exits.
[<EntryPoint>]
let main _argv =
    let stdin = Console.In
    let stdout = Console.Out
    let log (s: string) = Console.Error.WriteLine s

    // Path to the Fedit.PluginApi.dll the auto-generated plugin fsproj resolves
    // as its HintPath (see Plugins.fs). Prefer the sidecar beside the host:
    // a single-file/self-contained host bundles PluginApi, so Assembly.Location
    // is empty — but the .dll ships next to the host (release + AOT bundle +
    // Homebrew all place it there), so build it from there.
    let apiDll =
        let beside = Path.Combine(AppContext.BaseDirectory, "Fedit.PluginApi.dll")

        if File.Exists beside then
            beside
        else
            typeof<IPluginHost>.Assembly.Location

    let mutable registry = PluginRegistry.empty
    let writeLock = obj ()
    let inFlight = ConcurrentDictionary<int, CancellationTokenSource>()
    let shutdown = new CancellationTokenSource()

    let respond (json: string) =
        lock writeLock (fun () -> PluginProtocol.writeFrame stdout json)

    // Read-backs: the host asks the editor and a plugin's Run blocks (on its
    // pool thread) until the editor's response arrives on stdin.
    let mutable nextEditorRequestId = 0
    let editorReplies = ConcurrentDictionary<int, TaskCompletionSource<string>>()

    let askEditor (method: string) : string =
        let id = Interlocked.Increment &nextEditorRequestId

        let tcs =
            TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

        editorReplies[id] <- tcs
        respond (PluginProtocol.editorRequest id method)

        match PluginProtocol.parseValueResult (tcs.Task.GetAwaiter().GetResult()) with
        | Ok value -> value
        | Result.Error message -> failwith message

    let services: HostServices = { ReadClipboard = fun () -> askEditor "readClipboard" }

    /// Serve one request to completion (may await plugin work).
    let serve (id: int) (root: JsonElement) : Task<string> =
        task {
            match PluginProtocol.methodOf root with
            | "scan" ->
                let pluginsRoot, disabled = PluginProtocol.parseScanRequest root
                registry <- Plugins.scanAndLoadWith services pluginsRoot apiDll disabled log
                return PluginProtocol.scanResultJson id registry
            | "invoke" ->
                let command, ctx = PluginProtocol.parseInvokeRequest root

                match registry.Commands.TryFind command with
                | Some binding ->
                    use cts = CancellationTokenSource.CreateLinkedTokenSource shutdown.Token
                    inFlight[id] <- cts

                    try
                        try
                            let! actions = binding.Invoke ctx cts.Token
                            return PluginProtocol.invokeResultJson id actions
                        with
                        | :? OperationCanceledException -> return PluginProtocol.errorJson id "cancelled"
                        | ex ->
                            return PluginProtocol.errorJson id ("plugin '" + binding.Source + "' threw: " + ex.Message)
                    finally
                        inFlight.TryRemove id |> ignore
                | None -> return PluginProtocol.errorJson id ("unknown command: " + command)
            | "completions" ->
                let source, ctx = PluginProtocol.parseCompletionsRequest root

                match registry.CompletionRunners.TryFind source with
                | Some runners ->
                    use cts = CancellationTokenSource.CreateLinkedTokenSource shutdown.Token
                    inFlight[id] <- cts

                    try
                        try
                            let mutable items = []

                            for run in runners do
                                let! got = run ctx cts.Token
                                items <- items @ got

                            return PluginProtocol.completionsResultJson id items
                        with
                        | :? OperationCanceledException -> return PluginProtocol.errorJson id "cancelled"
                        | ex -> return PluginProtocol.errorJson id ("provider '" + source + "' threw: " + ex.Message)
                    finally
                        inFlight.TryRemove id |> ignore
                | None -> return PluginProtocol.completionsResultJson id []
            | "cancel" ->
                match inFlight.TryGetValue(PluginProtocol.parseCancelRequest root) with
                | true, cts -> cts.Cancel()
                | _ -> ()

                return PluginProtocol.invokeResultJson id []
            | other -> return PluginProtocol.errorJson id ("unknown method: " + other)
        }

    let mutable running = true

    while running do
        match PluginProtocol.readFrame stdin with
        | None -> running <- false
        | Some line ->
            // Parse on the reader so a malformed frame answers immediately;
            // the JsonDocument must outlive the served task, so no `use`.
            match
                (try
                    Ok(JsonDocument.Parse line)
                 with ex ->
                     Result.Error ex.Message)
            with
            | Result.Error message -> respond (PluginProtocol.errorJson 0 ("host error: " + message))
            | Ok doc when not (PluginProtocol.isRequest doc.RootElement) ->
                // The editor answering one of our read-backs.
                let id = PluginProtocol.idOf doc.RootElement

                match editorReplies.TryRemove id with
                | true, tcs -> tcs.TrySetResult line |> ignore
                | _ -> ()

                doc.Dispose()
            | Ok doc ->
                let root = doc.RootElement
                let id = PluginProtocol.idOf root

                if PluginProtocol.methodOf root = "shutdown" then
                    running <- false
                    shutdown.Cancel()

                    for KeyValue(_, tcs) in editorReplies do
                        tcs.TrySetResult(PluginProtocol.errorJson 0 "shutting down") |> ignore

                    respond (PluginProtocol.errorJson id "shutting down")
                else
                    Task.Run(fun () ->
                        task {
                            try
                                try
                                    let! response = serve id root
                                    respond response
                                with ex ->
                                    respond (PluginProtocol.errorJson id ("host error: " + ex.Message))
                            finally
                                doc.Dispose()
                        }
                        :> Task)
                    |> ignore

    0
