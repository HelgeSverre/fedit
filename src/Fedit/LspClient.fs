namespace Fedit

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading

/// Callbacks a running language server posts into. This layer compiles
/// before Model, so it cannot know Msg — Runtime wraps these with closures
/// that enqueue Msgs (the PluginHostClient wrap-by-Runtime pattern).
/// Implementations must be cheap (enqueue and return): they run on the
/// client's reader threads.
type LspClientCallbacks =
    {
        /// Full diagnostic set for one document: canonical path + diagnostics.
        /// Replaces any previous set for that document.
        OnDiagnostics: string * LspDiagnostic list -> unit
        OnStatusChanged: LspServerStatus -> unit
        /// One stderr line from the server process (sema prints a startup
        /// banner there — never a failure signal).
        OnLog: string -> unit
    }

/// A didOpen deferred while the server is still starting.
type private QueuedDocument =
    { LanguageId: string
      Version: int
      Text: string }

/// One out-of-process language server: the configured command spawned with
/// redirected stdio, speaking JSON-RPC under Content-Length framing. One
/// instance per server name + workspace root pair (`LspClient.key`);
/// Runtime owns creation and restart policy — this layer never restarts.
type LspClient(config: LanguageServerConfig, rootPath: string, callbacks: LspClientCallbacks) =
    let gate = obj ()
    let pending = ConcurrentDictionary<int, Result<JsonElement, string> -> unit>()
    let queuedDocuments = Dictionary<string, QueuedDocument>()
    let recentLogCapacity = 200
    let recentLog = Queue<string>()
    let mutable nextRequestId = 0
    let mutable status = LspServerStatus.NotStarted
    let mutable capabilities = LspServerCapabilities.none
    let mutable serverProcess: Process option = None
    let mutable shuttingDown = false

    let allocateRequestId () = Interlocked.Increment &nextRequestId

    let setStatus (next: LspServerStatus) =
        let changed =
            lock gate (fun () ->
                if status = next then
                    false
                else
                    status <- next
                    true)

        if changed then
            callbacks.OnStatusChanged next

    let appendLog (line: string) =
        lock recentLog (fun () ->
            recentLog.Enqueue line

            while recentLog.Count > recentLogCapacity do
                recentLog.Dequeue() |> ignore)

        callbacks.OnLog line

    /// Write one framed message to the server's stdin (LspTransport holds
    /// the per-stream writer lock). Error when the server is not running or
    /// the pipe is gone.
    let writeMessage (json: string) : Result<unit, string> =
        match lock gate (fun () -> serverProcess) with
        | None -> Result.Error "language server is not running"
        | Some p ->
            try
                LspTransport.writeFrame p.StandardInput.BaseStream json
                Result.Ok()
            with ex ->
                Result.Error("failed to write to language server: " + ex.Message)

    let failPending (reason: string) =
        for KeyValue(id, _) in pending do
            match pending.TryRemove id with
            | true, continuation -> continuation (Result.Error reason)
            | _ -> ()

    let sendRequest (makeJson: int -> string) (continuation: Result<JsonElement, string> -> unit) =
        if lock gate (fun () -> status) <> LspServerStatus.Running then
            continuation (Result.Error "language server is not running")
        else
            let id = allocateRequestId ()
            pending.[id] <- continuation

            let failRegistered (reason: string) =
                match pending.TryRemove id with
                | true, registered -> registered (Result.Error reason)
                | _ -> ()

            match writeMessage (makeJson id) with
            | Result.Ok() ->
                // The reader thread may have swept `pending` (EOF -> status
                // flip -> failPending) between the status check above and
                // the insert. The reader publishes the non-Running status
                // BEFORE it sweeps, so re-checking here after the write
                // guarantees every continuation is either swept there or
                // failed here — never orphaned.
                if lock gate (fun () -> status) <> LspServerStatus.Running then
                    failRegistered "language server is not running"
            | Result.Error e -> failRegistered e

    // Runs on the reader thread when the initialize response arrives:
    // store capabilities, send initialized, flush deferred didOpens, go
    // Running. The flush and the status flip are ONE gate section so a
    // concurrent Notify* either sees Starting (and folds into the queue
    // drained here) or sees Running strictly after every queued didOpen is
    // on the wire — a didChange can never outrun its didOpen. Monitor is
    // reentrant, so writeMessage's inner `lock gate` is safe, and
    // LspTransport's per-stream writer lock never wraps the gate, so there
    // is no lock-order inversion.
    let onInitializeResponse (outcome: Result<JsonElement, string>) =
        match outcome with
        | Result.Error e -> setStatus (LspServerStatus.Failed("initialize failed: " + e))
        | Result.Ok result ->
            let parsed = LspWire.readInitializeResult result

            let becameRunning =
                lock gate (fun () ->
                    capabilities <- parsed
                    writeMessage LspWire.initializedNotification |> ignore

                    let queued = [ for KeyValue(path, document) in queuedDocuments -> path, document ]

                    queuedDocuments.Clear()

                    for path, document in queued do
                        writeMessage (
                            LspWire.didOpenNotification
                                (LspUri.fromPath path)
                                document.LanguageId
                                document.Version
                                document.Text
                        )
                        |> ignore

                    // Only Starting flips to Running: a concurrent Shutdown
                    // may already have moved the client to Stopped.
                    if status = LspServerStatus.Starting then
                        status <- LspServerStatus.Running
                        true
                    else
                        false)

            if becameRunning then
                callbacks.OnStatusChanged LspServerStatus.Running

    // Route one message off the server's stdout. The JsonDocument lives only
    // for this call, so continuations decode their payload synchronously.
    let handleMessage (json: string) =
        use document = JsonDocument.Parse json

        match LspWire.classifyMessage document.RootElement with
        | LspIncomingMessage.Response(id, outcome) ->
            match pending.TryRemove id with
            | true, continuation -> continuation outcome
            | _ -> ()
        | LspIncomingMessage.Notification(methodName, parameters) ->
            match methodName, parameters with
            | "textDocument/publishDiagnostics", Some p ->
                let uri, diagnostics = LspWire.readPublishDiagnostics p

                match LspUri.toPath uri with
                | Some path -> callbacks.OnDiagnostics(path, diagnostics)
                | None -> ()
            | _ -> ()
        | LspIncomingMessage.Request(rawId, methodName, _) ->
            // Some servers stall until every request is answered; decline
            // anything we don't implement.
            writeMessage (LspWire.methodNotFoundResponse rawId methodName) |> ignore

    let readerLoop (stdout: Stream) () =
        let mutable running = true

        while running do
            match
                (try
                    LspTransport.readFrame stdout
                 with _ ->
                     None)
            with
            | None -> running <- false
            | Some json ->
                try
                    handleMessage json
                with ex ->
                    appendLog ("malformed message from language server: " + ex.Message)

        // stdout closed. During/after shutdown that is normal (sema
        // force-exits ~2s after `shutdown`); while active it is a crash.
        // The status flips BEFORE the pending sweep so sendRequest's
        // post-write re-check pairs with the sweep (see sendRequest).
        if lock gate (fun () -> shuttingDown) then
            setStatus LspServerStatus.Stopped
            failPending "language server stopped"
        else
            let reason =
                match lock gate (fun () -> serverProcess) with
                | Some p when p.WaitForExit 500 -> sprintf "language server exited unexpectedly (code %d)" p.ExitCode
                | _ -> "language server closed its output stream"

            setStatus (LspServerStatus.Failed reason)
            failPending reason

    let rec drainStandardError (reader: StreamReader) =
        match reader.ReadLine() with
        | null -> ()
        | line ->
            appendLog line
            drainStandardError reader

    let startBackgroundThread (name: string) (body: unit -> unit) =
        let thread = Thread(ThreadStart body)
        thread.IsBackground <- true
        thread.Name <- name
        thread.Start()

    member _.Config = config
    member _.RootPath = rootPath
    member _.Status = lock gate (fun () -> status)
    member _.Capabilities = lock gate (fun () -> capabilities)

    /// The tail of the server's stderr (bounded ring buffer), oldest first.
    member _.RecentLog() : string list =
        lock recentLog (fun () -> List.ofSeq recentLog)

    member _.ProcessHasExited: bool =
        match lock gate (fun () -> serverProcess) with
        | Some p -> p.HasExited
        | None -> true

    /// Spawn the server and start the initialize handshake. Non-blocking:
    /// Running is reached on the reader thread when the initialize response
    /// arrives. Idempotent — only the first call in NotStarted proceeds.
    member _.Start() : unit =
        let proceed =
            lock gate (fun () ->
                if status = LspServerStatus.NotStarted then
                    status <- LspServerStatus.Starting
                    true
                else
                    false)

        if proceed then
            callbacks.OnStatusChanged LspServerStatus.Starting

            try
                let startInfo = ProcessStartInfo(config.Command)

                for argument in config.Args do
                    startInfo.ArgumentList.Add argument

                startInfo.WorkingDirectory <- rootPath
                startInfo.RedirectStandardInput <- true
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.StandardErrorEncoding <- Encoding.UTF8
                startInfo.UseShellExecute <- false

                match Process.Start startInfo with
                | null -> setStatus (LspServerStatus.Failed "failed to start the server process")
                | p ->
                    lock gate (fun () -> serverProcess <- Some p)
                    startBackgroundThread ("lsp-stderr-" + config.Name) (fun () -> drainStandardError p.StandardError)

                    startBackgroundThread
                        ("lsp-stdout-" + config.Name)
                        (readerLoop (new BufferedStream(p.StandardOutput.BaseStream)))

                    let initializeId = allocateRequestId ()
                    pending.[initializeId] <- onInitializeResponse

                    writeMessage (
                        LspWire.initializeRequest initializeId Environment.ProcessId (LspUri.fromPath rootPath)
                    )
                    |> ignore
            with ex ->
                setStatus (LspServerStatus.Failed("failed to start: " + ex.Message))

    /// textDocument/didOpen with the buffer's full LF-normalized text.
    /// Deferred (and coalesced) while the server is still starting.
    member _.NotifyOpened(path: string, languageId: string, version: int, text: string) : unit =
        let sendNow =
            lock gate (fun () ->
                match status with
                | LspServerStatus.Running -> true
                | LspServerStatus.NotStarted
                | LspServerStatus.Starting ->
                    queuedDocuments.[Paths.norm path] <-
                        { LanguageId = languageId
                          Version = version
                          Text = text }

                    false
                | _ -> false)

        if sendNow then
            writeMessage (LspWire.didOpenNotification (LspUri.fromPath path) languageId version text)
            |> ignore

    /// textDocument/didChange carrying the full new text. Before Running it
    /// folds into the document's queued didOpen (a change for a document
    /// never opened is dropped).
    member _.NotifyChanged(path: string, version: int, text: string) : unit =
        let sendNow =
            lock gate (fun () ->
                match status with
                | LspServerStatus.Running -> true
                | LspServerStatus.NotStarted
                | LspServerStatus.Starting ->
                    let key = Paths.norm path

                    match queuedDocuments.TryGetValue key with
                    | true, queued ->
                        queuedDocuments.[key] <-
                            { queued with
                                Version = version
                                Text = text }
                    | _ -> ()

                    false
                | _ -> false)

        if sendNow then
            writeMessage (LspWire.didChangeNotification (LspUri.fromPath path) version text)
            |> ignore

    member _.NotifyClosed(path: string) : unit =
        let sendNow =
            lock gate (fun () ->
                match status with
                | LspServerStatus.Running -> true
                | LspServerStatus.NotStarted
                | LspServerStatus.Starting ->
                    queuedDocuments.Remove(Paths.norm path) |> ignore
                    false
                | _ -> false)

        if sendNow then
            writeMessage (LspWire.didCloseNotification (LspUri.fromPath path)) |> ignore

    member _.SendDefinition
        (path: string, position: Position, callback: Result<LspLocation list, string> -> unit)
        : unit =
        sendRequest
            (fun id -> LspWire.definitionRequest id (LspUri.fromPath path) (LspPosition.ofPosition position))
            (fun outcome -> callback (Result.map LspWire.readLocations outcome))

    member _.SendHover(path: string, position: Position, callback: Result<string list, string> -> unit) : unit =
        sendRequest
            (fun id -> LspWire.hoverRequest id (LspUri.fromPath path) (LspPosition.ofPosition position))
            (fun outcome -> callback (Result.map LspWire.readHoverResult outcome))

    member _.SendReferences
        (path: string, position: Position, callback: Result<LspLocation list, string> -> unit)
        : unit =
        sendRequest
            (fun id -> LspWire.referencesRequest id (LspUri.fromPath path) (LspPosition.ofPosition position))
            (fun outcome -> callback (Result.map LspWire.readLocations outcome))

    /// Polite shutdown: `shutdown` request, `exit` notification, then
    /// WaitForExit(3000) with a kill fallback. An abrupt child exit here is
    /// normal (sema force-exits ~2s after `shutdown`) — final status is
    /// Stopped, never Failed.
    member _.Shutdown() : unit =
        let runningProcess =
            lock gate (fun () ->
                shuttingDown <- true
                serverProcess)

        match runningProcess with
        | None -> setStatus LspServerStatus.Stopped
        | Some p ->
            if not p.HasExited then
                writeMessage (LspWire.shutdownRequest (allocateRequestId ())) |> ignore
                writeMessage LspWire.exitNotification |> ignore

                if not (p.WaitForExit 3000) then
                    try
                        p.Kill true
                    with _ ->
                        ()

                    p.WaitForExit 1000 |> ignore

            setStatus LspServerStatus.Stopped
            failPending "language server stopped"

    interface IDisposable with
        member this.Dispose() = this.Shutdown()

[<RequireQualifiedAccess>]
module LspClient =

    /// One client instance per server + workspace root pair.
    let key (config: LanguageServerConfig) (rootPath: string) : string = config.Name + "@" + Paths.norm rootPath

    /// Construct and immediately spawn/handshake. Status flows through
    /// callbacks.OnStatusChanged (Starting, then Running or Failed).
    let create (config: LanguageServerConfig) (rootPath: string) (callbacks: LspClientCallbacks) : LspClient =
        let client = new LspClient(config, Paths.norm rootPath, callbacks)
        client.Start()
        client
