namespace Fedit

// FS3261: BCL APIs like AppContext.BaseDirectory and Path.Combine surface
// nullable strings under net10. The plugin paths feed runtime-time only —
// guard them later if a null actually appears.
#nowarn "3261"

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent

[<RequireQualifiedAccess>]
module Runtime =
    let private utf8WithoutBom = UTF8Encoding false

    let private isMac =
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)

    let private startProcessOrFail (info: System.Diagnostics.ProcessStartInfo) =
        match System.Diagnostics.Process.Start info with
        | null -> failwith $"Failed to start {info.FileName}"
        | proc -> proc

    let private clipboardCopy (text: string) =
        let info = System.Diagnostics.ProcessStartInfo()

        if isMac then
            info.FileName <- "pbcopy"
        else
            info.FileName <- "xclip"
            info.ArgumentList.Add "-selection"
            info.ArgumentList.Add "clipboard"

        info.RedirectStandardInput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        use proc = startProcessOrFail info
        proc.StandardInput.Write text
        proc.StandardInput.Close()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwith $"clipboard copy failed (exit {proc.ExitCode}): {stderr}"

    let private clipboardPaste () =
        let info = System.Diagnostics.ProcessStartInfo()

        if isMac then
            info.FileName <- "pbpaste"
        else
            info.FileName <- "xclip"
            info.ArgumentList.Add "-selection"
            info.ArgumentList.Add "clipboard"
            info.ArgumentList.Add "-out"

        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        use proc = startProcessOrFail info
        let output = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwith $"clipboard paste failed (exit {proc.ExitCode}): {stderr}"

        output

    let private renderTextResult (result: Result<string, string>) =
        match result with
        | Result.Ok text -> $"Ok(<len={text.Length}>)"
        | Result.Error error -> $"Error({error})"

    let private renderUnitResult (result: Result<unit, string>) =
        match result with
        | Result.Ok() -> "Ok"
        | Result.Error error -> $"Error({error})"

    let private renderTarget (target: Position option) =
        match target with
        | Some pos -> $"{pos.Line}:{pos.Column}"
        | None -> "-"

    let private renderIntent intent =
        match intent with
        | OpenPermanent -> "permanent"
        | OpenPreview -> "preview"

    let private renderLspServerStatus (status: LspServerStatus) =
        match status with
        | LspServerStatus.NotStarted -> "NotStarted"
        | LspServerStatus.Starting -> "Starting"
        | LspServerStatus.Running -> "Running"
        | LspServerStatus.Failed reason -> $"Failed({reason})"
        | LspServerStatus.Stopped -> "Stopped"

    let private renderLocationsResult (result: Result<LspResolvedLocation list, string>) =
        match result with
        | Result.Ok locations -> $"Ok(count={locations.Length})"
        | Result.Error error -> $"Error({error})"

    let private renderHoverResult (result: Result<string list, string>) =
        match result with
        | Result.Ok lines -> $"Ok(lines={lines.Length})"
        | Result.Error error -> $"Error({error})"

    let private renderLspPositionRequest (request: LspPositionRequest) =
        $"{request.Path}:{request.Position.Line}:{request.Position.Column}, tick={request.EditTick}, buffer={request.BufferId}"

    // NOTE: every case is rendered explicitly — there is deliberately NO `_`
    // catch-all. A wildcard would have to interpolate the bare DU (`$"{msg}"`),
    // which F# lowers to reflective structured printing — fine under JIT but a
    // hard crash under NativeAOT. Keeping the match exhaustive means a newly
    // added Msg/Effect case fails the build here instead of silently
    // reintroducing the reflective path. Only scalars/strings/lengths and the
    // AOT-safe helpers (Chord.render, renderIntent, render*Result) reach a hole.
    let private renderMsg msg =
        match msg with
        | KeyPressed chord -> $"KeyPressed({Chord.render chord})"
        | SequenceTimedOut -> "SequenceTimedOut"
        | Resize size -> $"Resize({size.Width}x{size.Height})"
        | MouseScrolled(ticks, position) -> $"MouseScrolled(ticks={ticks}, at={position.Line}:{position.Column})"
        | MousePressed e -> $"MousePressed({e.Position.Line}:{e.Position.Column})"
        | MouseReleased e -> $"MouseReleased({e.Position.Line}:{e.Position.Column})"
        | MouseDragged e -> $"MouseDragged({e.Position.Line}:{e.Position.Column})"
        | FocusGained -> "FocusGained"
        | FocusLost -> "FocusLost"
        | WorkspaceLoaded(Result.Ok _) -> "WorkspaceLoaded(Ok)"
        | WorkspaceLoaded(Result.Error error) -> $"WorkspaceLoaded(Error({error}))"
        | FileOpened(path, intent, target, result) ->
            $"FileOpened({path}, {renderIntent intent}, target={renderTarget target}, {renderTextResult result})"
        | BufferSaved(bufferId, path, revision, result) ->
            $"BufferSaved(buffer={bufferId}, path={path}, revision={revision}, {renderUnitResult result})"
        | ConfigSaved result -> $"ConfigSaved({renderUnitResult result})"
        | ConfigFileReady(Result.Ok path) -> $"ConfigFileReady(Ok({path}))"
        | ConfigFileReady(Result.Error error) -> $"ConfigFileReady(Error({error}))"
        | ClipboardCopied result -> $"ClipboardCopied({renderUnitResult result})"
        | ClipboardPasted result -> $"ClipboardPasted({renderTextResult result})"
        | PastedText text -> $"PastedText(<len={text.Length}>)"
        | SearchCompleted(bufferId, query, matches) ->
            $"SearchCompleted(buffer={bufferId}, queryLen={query.Length}, matches={matches.Length})"
        | WorkspaceChangedExternally -> "WorkspaceChangedExternally"
        | MacroReplayStart -> "MacroReplayStart"
        | MacroReplayEnd -> "MacroReplayEnd"
        | HighlightParsed(bufferId, editTick, spans) ->
            $"HighlightParsed(buffer={bufferId}, tick={editTick}, spans={spans.Length})"
        | PluginsScanned(Result.Ok _) -> "PluginsScanned(Ok)"
        | PluginsScanned(Result.Error error) -> $"PluginsScanned(Error({error}))"
        | PluginActionsReady(source, Result.Ok actions) ->
            $"PluginActionsReady(source={source}, actions={actions.Length})"
        | PluginActionsReady(source, Result.Error error) -> $"PluginActionsReady(source={source}, Error({error}))"
        | PluginInstalled(name, result) -> $"PluginInstalled(name={name}, {renderUnitResult result})"
        | PluginRemoved(name, result) -> $"PluginRemoved(name={name}, {renderUnitResult result})"
        | PluginBuildFinished(name, result) -> $"PluginBuildFinished(name={name}, {renderUnitResult result})"
        | PluginValidated(Result.Ok report) -> $"PluginValidated(Ok(<len={report.Length}>))"
        | PluginValidated(Result.Error error) -> $"PluginValidated(Error({error}))"
        | KeybindsLoaded(_, errors) -> $"KeybindsLoaded(errors={errors.Length})"
        | LspServerStatusChanged(name, status) -> $"LspServerStatusChanged({name}, {renderLspServerStatus status})"
        | LspDiagnosticsPublished(path, diagnostics) -> $"LspDiagnosticsPublished({path}, count={diagnostics.Length})"
        | LspDefinitionResolved(outcome, requestedEditTick, bufferId) ->
            $"LspDefinitionResolved(buffer={bufferId}, tick={requestedEditTick}, {renderLocationsResult outcome})"
        | LspReferencesResolved(outcome, requestedEditTick, bufferId) ->
            $"LspReferencesResolved(buffer={bufferId}, tick={requestedEditTick}, {renderLocationsResult outcome})"
        | LspHoverResolved(outcome, requestedEditTick, bufferId) ->
            $"LspHoverResolved(buffer={bufferId}, tick={requestedEditTick}, {renderHoverResult outcome})"
        | LspLogFetched(title, lines) -> $"LspLogFetched({title}, lines={lines.Length})"

    let private renderEffect effect =
        match effect with
        | ScanWorkspace path -> $"ScanWorkspace({path})"
        | LoadFile(path, intent, target) -> $"LoadFile({path}, {renderIntent intent}, target={renderTarget target})"
        | SaveBuffer(bufferId, path, revision, contents) ->
            $"SaveBuffer(buffer={bufferId}, path={path}, revision={revision}, contentsLen={contents.Length})"
        | SaveConfig _ -> "SaveConfig(<config>)"
        | EnsureConfigFile _ -> "EnsureConfigFile(<config>)"
        | ClipboardCopy text -> $"ClipboardCopy(<len={text.Length}>)"
        | ClipboardPaste -> "ClipboardPaste"
        | RunSearch(bufferId, query, document) ->
            $"RunSearch(buffer={bufferId}, queryLen={query.Length}, haystackLen={PieceTable.length document})"
        | ParseHighlight(bufferId, language, document, editTick) ->
            $"ParseHighlight(buffer={bufferId}, lang={language}, tick={editTick}, docLen={PieceTable.length document})"
        | ScanPlugins disabled -> $"ScanPlugins(disabled={disabled.Count})"
        | RunPluginCommand(source, command, _) -> $"RunPluginCommand(source={source}, command={command})"
        | InstallPluginFromSource _ -> "InstallPluginFromSource(<source>)"
        | RemovePluginDir name -> $"RemovePluginDir({name})"
        | BuildPlugin pluginPath -> $"BuildPlugin({pluginPath})"
        | ValidatePlugin path -> $"ValidatePlugin({path})"
        | LoadKeybinds -> "LoadKeybinds"
        | ReplayKeys(chords, count) -> $"ReplayKeys(chords={chords.Length}, count={count})"
        | LspSyncDocuments(workspaceRoot, documents) ->
            $"LspSyncDocuments(root={workspaceRoot}, documents={documents.Length})"
        | LspRestart name ->
            let target =
                match name with
                | Some serverName -> serverName
                | None -> "all"

            $"LspRestart({target})"
        | LspRequestDefinition request -> $"LspRequestDefinition({renderLspPositionRequest request})"
        | LspRequestHover request -> $"LspRequestHover({renderLspPositionRequest request})"
        | LspRequestReferences request -> $"LspRequestReferences({renderLspPositionRequest request})"
        | LspFetchLog name ->
            let target =
                match name with
                | Some serverName -> serverName
                | None -> "all"

            $"LspFetchLog({target})"

    /// Build a FileNode, using the basename (or full path when the name is
    /// empty). Paths are canonicalized to `/` here — this is the OS boundary
    /// where tree paths enter from `Directory.Enumerate*` (native separators).
    let private makeNode (path: string) isDirectory children : FileNode =
        let path = Paths.norm path
        let rawName = Path.GetFileName path |> Text.optStr |> Option.defaultValue path

        { Path = path
          Name = if String.IsNullOrWhiteSpace rawName then path else rawName
          IsDirectory = isDirectory
          Children = children }

    /// True if the path's last segment is in the workspace exclusion set.
    let private shouldSkip (path: string) =
        Path.GetFileName path
        |> Text.optStr
        |> Option.map Workspace.excludedNames.Contains
        |> Option.defaultValue false

    /// Recursively build a FileNode tree, counting skipped/unreadable entries.
    let rec private scanNode (path: string) : FileNode * int =
        let attributes = File.GetAttributes path
        let isDirectory = attributes.HasFlag FileAttributes.Directory

        if isDirectory then
            if attributes.HasFlag FileAttributes.ReparsePoint then
                makeNode path true [], 0
            else
                let mutable skipped = 0
                let children = ResizeArray<FileNode>()

                try
                    for childDir in Directory.EnumerateDirectories path do
                        if not (shouldSkip childDir) then
                            try
                                let node, childSkipped = scanNode childDir
                                skipped <- skipped + childSkipped
                                children.Add node
                            with _ ->
                                skipped <- skipped + 1
                with _ ->
                    skipped <- skipped + 1

                try
                    for childFile in Directory.EnumerateFiles path do
                        if not (shouldSkip childFile) then
                            try
                                children.Add(makeNode childFile false [])
                            with _ ->
                                skipped <- skipped + 1
                with _ ->
                    skipped <- skipped + 1

                makeNode path true (List.ofSeq children), skipped
        else
            makeNode path false [], 0

    /// Current terminal dimensions, clamped to a minimum of 1×1.
    let private consoleSize () =
        { Width = max 1 Console.WindowWidth
          Height = max 1 Console.WindowHeight }

    /// Resolve symlinks in every component of a path (realpath semantics),
    /// returning the canonical `/`-separated result. Language servers
    /// canonicalize the URIs they publish (sema realpaths macOS's
    /// `/tmp` -> `/private/tmp`; rust-analyzer does the same), so paths
    /// received from a server must be comparable against the editor's
    /// buffer paths through this resolution. Components that don't exist
    /// (or can't be probed) pass through unchanged. Impure — filesystem
    /// probing lives here, never in the pure layers.
    let canonicalizePath (path: string) : string =
        let resolveLink (candidate: string) : string option =
            try
                let info =
                    if Directory.Exists candidate then
                        Directory.ResolveLinkTarget(candidate, true)
                    elif File.Exists candidate then
                        File.ResolveLinkTarget(candidate, true)
                    else
                        null

                match info with
                | null -> None
                | resolved -> Some(Paths.norm resolved.FullName)
            with _ ->
                None

        // Walk from the root, resolving each accumulated prefix, so a
        // symlinked directory anywhere in the path is replaced by its
        // target before the deeper components are appended. A link's
        // target can itself pass through symlinked directories (a link
        // into `/var/...` on macOS), so each substitution re-walks the
        // target; the depth guard bounds symlink cycles.
        let rec walk (depth: int) (path: string) : string =
            let mutable current = ""
            let mutable first = true

            for segment in path.Split '/' do
                if first then
                    // "" for absolute Unix paths, "C:" for Windows drives.
                    current <- segment
                    first <- false
                else
                    let candidate = current + "/" + segment

                    current <-
                        match resolveLink candidate with
                        | Some target when depth < 16 -> walk (depth + 1) target
                        | Some target -> target
                        | None -> candidate

            current

        walk 0 (Paths.norm path)

    let run rootPath initialFile (logPath: string option) =
        // Canonicalize the workspace root + initial file to `/` at this OS
        // boundary so every downstream path comparison is platform-independent.
        let rootPath = Paths.norm rootPath
        let initialFile = initialFile |> Option.map Paths.norm
        Console.OutputEncoding <- Encoding.UTF8
        Console.TreatControlCAsInput <- true

        let logWriter: StreamWriter option =
            logPath
            |> Option.map (fun path ->
                Path.GetDirectoryName path
                |> Text.optStr
                |> Option.iter (fun d -> Directory.CreateDirectory d |> ignore)

                new StreamWriter(path, append = true, encoding = utf8WithoutBom))

        // LSP client callbacks log from their reader threads, so writes are
        // serialized — StreamWriter is not thread-safe.
        let logLock = obj ()

        let log (s: string) =
            match logWriter with
            | Some w ->
                lock logLock (fun () ->
                    w.WriteLine($"{DateTime.UtcNow:o} {s}")
                    w.Flush())
            | None -> ()

        // Async effect machinery.
        // Effect tasks run on the thread pool. Each posts a result Msg back through
        // the queue, which the main loop drains every tick. ScanWorkspace and
        // LoadFile each carry a single in-flight CancellationTokenSource: a
        // second instance cancels the first by dropping its result Msg.
        let queue = ConcurrentQueue<Msg>()
        let mutable scanCts: CancellationTokenSource option = None
        let mutable loadCts: CancellationTokenSource option = None
        // Serialize config writes so two quick saves can't land
        // out of order on disk. Each new SaveConfig chains onto the previous
        // task, preserving dispatch order by construction.
        let configSaveLock = obj ()
        let mutable configSaveChain: Task = Task.CompletedTask
        // Serialize buffer writes per canonical path so repeated saves cannot
        // land out of dispatch order on disk.
        let bufferSaveLock = obj ()
        let bufferSaveChains = System.Collections.Generic.Dictionary<string, Task>()
        // Cancel previous incremental search before starting the next.
        let mutable searchCts: CancellationTokenSource option = None
        // Latest-wins highlight parse per buffer: a keystroke during a parse
        // cancels the stale one; `update` also drops stale results by tick.
        let highlightCts =
            System.Collections.Generic.Dictionary<int, CancellationTokenSource>()
        // The interpreter owns all native tree-sitter objects; the Model only
        // ever sees span arrays posted back as `HighlightParsed`.
        let highlightRegistry = HighlightRegistry.tryCreate ()

        // Plugins load in a separate JIT process so the editor can ship as
        // NativeAOT. Scans and invocations go through this client; the Model
        // only ever sees the registry (stub Run closures) and PluginActions.
        let pluginHost = new PluginHostClient(PluginHostClient.defaultHostPath ())

        // Language servers: one out-of-process client per server name +
        // resolved workspace root, spawned lazily by the LspSyncDocuments
        // interpreter. All document notifications (and restarts) chain onto
        // one task — the configSaveChain pattern — so they reach each server
        // in dispatch order: a didChange can never outrun its didOpen, and a
        // restart cannot race an in-flight notification. Client callbacks
        // enqueue Msgs exactly like the FileSystemWatcher below.
        let lspLock = obj ()
        let lspClients = System.Collections.Generic.Dictionary<string, LspClient>()
        let mutable lspSyncChain: Task = Task.CompletedTask

        let lspMarkerExists (path: string) =
            File.Exists path || Directory.Exists path

        // Canonical (symlink-resolved) path aliases: canonical -> the path
        // the editor knows the document by. Servers may publish URIs for
        // the resolved path (sema realpaths `/tmp` -> `/private/tmp`), so
        // every path received from a server translates back through this
        // table — otherwise diagnostics would never match the open buffer
        // and goto-definition would open a duplicate of it. Documents
        // register identity entries too, so an explicitly-canonical open
        // wins over a workspace-root prefix rewrite. Entries are tiny and
        // bounded by the session's file set; they are never removed.
        let lspPathAliases = System.Collections.Generic.Dictionary<string, string>()
        let lspCanonicalCache = System.Collections.Generic.Dictionary<string, string>()

        // Resolution is cached per path so the reader-thread diagnostics
        // callback stays cheap after the first sighting of a path.
        let lspCanonicalFor (path: string) : string =
            let cached =
                lock lspLock (fun () ->
                    match lspCanonicalCache.TryGetValue path with
                    | true, canonical -> Some canonical
                    | _ -> None)

            match cached with
            | Some canonical -> canonical
            | None ->
                let canonical = canonicalizePath path
                lock lspLock (fun () -> lspCanonicalCache[path] <- canonical)
                canonical

        let lspRegisterPathAlias (editorPath: string) : unit =
            let canonical = lspCanonicalFor editorPath
            lock lspLock (fun () -> lspPathAliases[canonical] <- editorPath)

        /// A path received from a server, mapped back to the editor's form:
        /// exact document alias first, then a workspace-root prefix rewrite
        /// (covers never-opened files inside a symlinked root), else the
        /// canonical form as-is.
        let lspTranslateServerPath (serverPath: string) : string =
            let canonical = lspCanonicalFor serverPath

            lock lspLock (fun () ->
                match lspPathAliases.TryGetValue canonical with
                | true, editorPath -> editorPath
                | _ ->
                    lspPathAliases
                    |> Seq.tryPick (fun (KeyValue(aliasCanonical, aliasEditorPath)) ->
                        if
                            aliasEditorPath <> aliasCanonical
                            && canonical.StartsWith(aliasCanonical + "/", StringComparison.Ordinal)
                        then
                            Some(aliasEditorPath + canonical.Substring aliasCanonical.Length)
                        else
                            None)
                    |> Option.defaultValue canonical)

        // A document's workspace root resolves once, on first sync, and
        // stays pinned for its whole open/change/close lifecycle:
        // re-resolving against the live filesystem could route a later
        // didChange to a different client — one that never saw the didOpen —
        // when a root marker appears or disappears mid-session. Entries
        // drop on Closed and on LspRestart (documents re-pin on the reopen
        // sync that follows a restart).
        let lspDocumentRoots = System.Collections.Generic.Dictionary<string, string>()

        let lspRootFor (server: LanguageServerConfig) (path: string) (workspaceFallbackRoot: string) : string =
            let pinned =
                lock lspLock (fun () ->
                    match lspDocumentRoots.TryGetValue path with
                    | true, root -> Some root
                    | _ -> None)

            match pinned with
            | Some root -> root
            | None ->
                let resolved =
                    LanguageServers.findWorkspaceRoot lspMarkerExists server.RootMarkers path workspaceFallbackRoot

                lock lspLock (fun () -> lspDocumentRoots[path] <- resolved)
                resolved

        let lspContinueWith (work: unit -> unit) =
            lock lspLock (fun () ->
                lspSyncChain <- lspSyncChain.ContinueWith((fun (_: Task) -> work ()), TaskContinuationOptions.None))

        let lspClientFor (server: LanguageServerConfig) (rootPath: string) : LspClient =
            // The workspace root registers as an alias so server paths
            // under a symlinked root rewrite back to the editor's form.
            lspRegisterPathAlias rootPath

            lock lspLock (fun () ->
                let key = LspClient.key server rootPath

                match lspClients.TryGetValue key with
                | true, client -> client
                | false, _ ->
                    let callbacks =
                        { OnDiagnostics =
                            fun (path, diagnostics) ->
                                queue.Enqueue(LspDiagnosticsPublished(lspTranslateServerPath path, diagnostics))
                          OnStatusChanged = fun status -> queue.Enqueue(LspServerStatusChanged(key, status))
                          OnLog = fun line -> log $"lsp[{server.Name}]: {line}" }

                    let client = LspClient.create server rootPath callbacks
                    lspClients[key] <- client
                    client)

        /// Resolve the client owning a position request (get-or-spawn, the
        /// document's pinned root — same resolution as document sync).
        let lspClientForRequest (request: LspPositionRequest) : LspClient =
            lspRegisterPathAlias request.Path
            lspClientFor request.Server (lspRootFor request.Server request.Path request.WorkspaceRoot)

        /// One preview line off disk for the location picker. The update
        /// layer swaps in the open buffer's line where the document is open;
        /// this covers everything else (unopened files, indexed workspace).
        let lspPreviewLine (path: string) (lineIndex: int) : string =
            try
                use reader = new StreamReader(path)
                let mutable current = reader.ReadLine()
                let mutable index = 0

                while index < lineIndex && current <> null do
                    current <- reader.ReadLine()
                    index <- index + 1

                match current with
                | null -> ""
                | line -> line.Trim()
            with _ ->
                ""

        /// URI -> canonical path + preview line, dropping non-file URIs.
        /// The path translates back through the symlink alias table so a
        /// location lands on the buffer the editor already has open, never
        /// a duplicate under the server's resolved spelling. Involves disk
        /// reads, so callers run it off the reader thread.
        let lspResolveLocations (locations: LspLocation list) : LspResolvedLocation list =
            locations
            |> List.choose (fun location ->
                LspUri.toPath location.Uri
                |> Option.map (fun serverPath ->
                    let path = lspTranslateServerPath serverPath
                    let position = LspPosition.toPosition location.Range.Start

                    { Path = path
                      Position = position
                      Preview = lspPreviewLine path position.Line }))

        let cancelAndReplace (existing: CancellationTokenSource option) =
            existing
            |> Option.iter (fun cts ->
                try
                    cts.Cancel()
                with _ ->
                    ()

                cts.Dispose())

            new CancellationTokenSource()

        let enqueueUnlessCancelled (token: CancellationToken) (msg: Msg) =
            if not token.IsCancellationRequested then
                queue.Enqueue msg

        let startEffect effect =
            match effect with
            | ScanWorkspace rootPath ->
                let cts = cancelAndReplace scanCts
                scanCts <- Some cts
                let token = cts.Token

                Task.Run(fun () ->
                    let msg =
                        try
                            let tree, skipped = scanNode rootPath
                            let sorted, byPath, files = Workspace.preCompute rootPath tree
                            WorkspaceLoaded(Result.Ok(sorted, byPath, files, skipped))
                        with ex ->
                            WorkspaceLoaded(Result.Error ex.Message)

                    enqueueUnlessCancelled token msg)
                |> ignore
            | LoadFile(path, intent, target) ->
                let cts = cancelAndReplace loadCts
                loadCts <- Some cts
                let token = cts.Token

                Task.Run(fun () ->
                    let msg =
                        try
                            FileOpened(path, intent, target, Result.Ok(File.ReadAllText path))
                        with ex ->
                            FileOpened(path, intent, target, Result.Error ex.Message)

                    enqueueUnlessCancelled token msg)
                |> ignore
            | SaveBuffer(bufferId, path, revision, contents) ->
                let key =
                    try
                        Path.GetFullPath path
                    with _ ->
                        path

                lock bufferSaveLock (fun () ->
                    let previous =
                        match bufferSaveChains.TryGetValue key with
                        | true, task -> task
                        | false, _ -> Task.CompletedTask

                    let next =
                        previous.ContinueWith(
                            (fun (_: Task) ->
                                let msg =
                                    try
                                        File.writeAllTextAtomic path contents
                                        BufferSaved(bufferId, path, revision, Result.Ok())
                                    with ex ->
                                        BufferSaved(bufferId, path, revision, Result.Error ex.Message)

                                queue.Enqueue msg),
                            TaskContinuationOptions.None
                        )

                    bufferSaveChains[key] <- next)
            | SaveConfig config ->
                // Chain onto the previous config-save task so writes land in
                // dispatch order regardless of pool scheduling.
                lock configSaveLock (fun () ->
                    configSaveChain <-
                        configSaveChain.ContinueWith(
                            (fun (_: Task) ->
                                let msg =
                                    try
                                        ConfigIO.save config
                                        ConfigSaved(Result.Ok())
                                    with ex ->
                                        ConfigSaved(Result.Error ex.Message)

                                queue.Enqueue msg),
                            TaskContinuationOptions.None
                        ))
            | EnsureConfigFile config ->
                Task.Run(fun () ->
                    let msg =
                        try
                            let configPath = ConfigIO.path ()

                            if not (File.Exists configPath) then
                                ConfigIO.save config

                            ConfigFileReady(Result.Ok configPath)
                        with ex ->
                            ConfigFileReady(Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | ClipboardCopy text ->
                Task.Run(fun () ->
                    let msg =
                        try
                            clipboardCopy text
                            ClipboardCopied(Result.Ok())
                        with ex ->
                            ClipboardCopied(Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | RunSearch(bufferId, query, document) ->
                // Cancel any in-flight search; the latest query wins.
                let cts = cancelAndReplace searchCts
                searchCts <- Some cts
                let token = cts.Token

                Task.Run(fun () ->
                    // Materialize the haystack here, off the UI thread; the
                    // effect carries only the shared piece table.
                    let haystack = PieceTable.toString document
                    // Plain IndexOf loop — same logic as the old in-update
                    // `Buffer.findAll`, just off the UI thread.
                    let mutable matches: int list = []

                    let mutable index =
                        if String.IsNullOrEmpty query then
                            -1
                        else
                            haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase)

                    while index >= 0 do
                        matches <- index :: matches
                        index <- haystack.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase)

                    enqueueUnlessCancelled token (SearchCompleted(bufferId, query, List.rev matches)))
                |> ignore
            | ClipboardPaste ->
                Task.Run(fun () ->
                    let msg =
                        try
                            ClipboardPasted(Result.Ok(clipboardPaste ()))
                        with ex ->
                            ClipboardPasted(Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | ParseHighlight(bufferId, language, document, editTick) ->
                // Cancel any in-flight parse (or its debounce nap) first so
                // a stale-language result can never outrun this request.
                let existing =
                    match highlightCts.TryGetValue bufferId with
                    | true, cts -> Some cts
                    | false, _ -> None

                let cts = cancelAndReplace existing
                highlightCts[bufferId] <- cts
                let token = cts.Token

                // Grammar lookup is a pair of cheap dictionary probes — check
                // before materializing the document so a missing registry or
                // unloaded grammar never pays `PieceTable.toString`. The empty
                // result still posts so previously-stored spans stop painting
                // at stale offsets.
                let grammar =
                    highlightRegistry
                    |> Option.filter (fun registry ->
                        (registry.TryGetLanguage language).IsSome
                        && (registry.TryGetQuery language).IsSome)

                match grammar with
                | None -> queue.Enqueue(HighlightParsed(bufferId, editTick, [||]))
                | Some registry ->
                    // No debounce: parse immediately so syntax colors are as
                    // instant as the machine allows. A keystroke mid-parse
                    // cancels this token and the result is dropped at enqueue;
                    // the superseded parse still runs to completion, but off the
                    // UI thread, so it never blocks input or rendering. The
                    // size cap (Highlight.maxParseChars) bounds the one case
                    // that would actually hurt — a multi-megabyte buffer.
                    Task.Run(fun () ->
                        if not token.IsCancellationRequested then
                            try
                                let source = PieceTable.toString document

                                match Highlight.parseSpans registry language source with
                                | Some spans ->
                                    enqueueUnlessCancelled token (HighlightParsed(bufferId, editTick, spans))
                                | None ->
                                    // Post an empty result so previously-stored
                                    // spans stop painting at stale offsets.
                                    enqueueUnlessCancelled token (HighlightParsed(bufferId, editTick, [||]))
                            with ex ->
                                log $"highlight: parse failed for buffer {bufferId} ({language}): {ex.Message}")
                    |> ignore
            | ScanPlugins disabledPlugins ->
                Task.Run(fun () ->
                    let pluginsRoot = Path.Combine(ConfigIO.directory (), "plugins")
                    queue.Enqueue(PluginsScanned(pluginHost.Scan(pluginsRoot, disabledPlugins))))
                |> ignore
            | RunPluginCommand(source, command, context) ->
                Task.Run(fun () -> queue.Enqueue(PluginActionsReady(source, pluginHost.Invoke(command, context))))
                |> ignore
            | InstallPluginFromSource source ->
                Task.Run(fun () ->
                    let pluginsRoot = Path.Combine(ConfigIO.directory (), "plugins")

                    let msg =
                        try
                            let name = Plugins.install pluginsRoot source
                            PluginInstalled(name, Result.Ok())
                        with ex ->
                            PluginInstalled("?", Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | RemovePluginDir name ->
                Task.Run(fun () ->
                    let pluginsRoot = Path.Combine(ConfigIO.directory (), "plugins")

                    let msg =
                        try
                            Plugins.uninstall pluginsRoot name
                            PluginRemoved(name, Result.Ok())
                        with ex ->
                            PluginRemoved(name, Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | BuildPlugin pluginPath ->
                Task.Run(fun () ->
                    let apiDll = Path.Combine(AppContext.BaseDirectory, "Fedit.PluginApi.dll")
                    let name = Path.GetFileName pluginPath

                    let msg =
                        try
                            let manifestPath = Path.Combine(pluginPath, "plugin.json")

                            match Plugins.tryParseManifest manifestPath with
                            | Result.Error e -> PluginBuildFinished(name, Result.Error e)
                            | Result.Ok manifest ->
                                let loaded =
                                    { Manifest = manifest
                                      Path = pluginPath
                                      Status = Disabled
                                      Commands = []
                                      Keybindings = []
                                      Conflicts = [] }

                                match Plugins.build apiDll loaded with
                                | Result.Ok _ -> PluginBuildFinished(name, Result.Ok())
                                | Result.Error e -> PluginBuildFinished(name, Result.Error e)
                        with ex ->
                            PluginBuildFinished(name, Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | ValidatePlugin path ->
                Task.Run(fun () ->
                    let msg =
                        try
                            let manifestPath = Path.Combine(path, "plugin.json")

                            if not (File.Exists manifestPath) then
                                PluginValidated(Result.Error $"No plugin.json found in {path}.")
                            else
                                match Plugins.tryParseManifest manifestPath with
                                | Result.Ok manifest ->
                                    PluginValidated(
                                        Result.Ok
                                            $"OK: {manifest.Name} {manifest.Version} (apiVersion {manifest.ApiVersion}); entryType={manifest.EntryType}"
                                    )
                                | Result.Error reason -> PluginValidated(Result.Error reason)
                        with ex ->
                            PluginValidated(Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | LoadKeybinds ->
                Task.Run(fun () ->
                    let keymap, errors = KeymapIO.load ()
                    queue.Enqueue(KeybindsLoaded(keymap, errors)))
                |> ignore
            | ReplayKeys(chords, count) ->
                // Pure in-memory queue manipulation — runs synchronously on the
                // dispatch thread (unlike the I/O effects). Bracket the injected
                // keys with markers so the record-append hook suppresses
                // self-recording; the main loop drains them on later ticks.
                queue.Enqueue MacroReplayStart

                for _ in 1..count do
                    for chord in chords do
                        queue.Enqueue(KeyPressed chord)

                queue.Enqueue MacroReplayEnd
            | LspSyncDocuments(workspaceRoot, documents) ->
                // Serialized on the LSP chain (dispatch order preserved by
                // construction). Text is materialized here, off the update
                // thread — the effect carries only the shared piece table.
                lspContinueWith (fun () ->
                    for document in documents do
                        try
                            lspRegisterPathAlias document.Path
                            let rootPath = lspRootFor document.Server document.Path workspaceRoot
                            let client = lspClientFor document.Server rootPath

                            match document.Kind with
                            | LspDocumentSyncKind.Opened text ->
                                client.NotifyOpened(
                                    document.Path,
                                    document.LanguageId,
                                    document.Version,
                                    PieceTable.toString text
                                )
                            | LspDocumentSyncKind.Changed text ->
                                client.NotifyChanged(document.Path, document.Version, PieceTable.toString text)
                            | LspDocumentSyncKind.Closed ->
                                client.NotifyClosed document.Path
                                lock lspLock (fun () -> lspDocumentRoots.Remove document.Path |> ignore)
                        with ex ->
                            log $"lsp: sync failed for {document.Path}: {ex.Message}")
            | LspRestart name ->
                // Also on the chain so a restart cannot race an in-flight
                // notification. Removed clients respawn lazily on the next
                // LspSyncDocuments that needs them (documents re-open on the
                // next edit; the `:lsp` verbs landing later force a resync).
                lspContinueWith (fun () ->
                    let removed =
                        lock lspLock (fun () ->
                            let matching =
                                [ for KeyValue(key, client) in lspClients do
                                      let selected =
                                          match name with
                                          | None -> true
                                          | Some serverName -> client.Config.Name = serverName

                                      if selected then
                                          key, client ]

                            for key, _ in matching do
                                lspClients.Remove key |> ignore

                            // Unpin every document root so the reopen sync
                            // that follows the restart re-resolves against
                            // the current filesystem.
                            lspDocumentRoots.Clear()

                            matching |> List.map snd)

                    for client in removed do
                        try
                            client.Shutdown()
                        with ex ->
                            log $"lsp: shutdown failed for {client.Config.Name}: {ex.Message}")
            | LspRequestDefinition request ->
                // Chained after any pending document sync so the server sees
                // the request-time text. The reply callback runs on the
                // client's reader thread; the URI->path + preview-line
                // enrichment does disk reads, so it hops to the pool first.
                lspContinueWith (fun () ->
                    let client = lspClientForRequest request

                    client.SendDefinition(
                        request.Path,
                        request.Position,
                        fun outcome ->
                            Task.Run(fun () ->
                                queue.Enqueue(
                                    LspDefinitionResolved(
                                        Result.map lspResolveLocations outcome,
                                        request.EditTick,
                                        request.BufferId
                                    )
                                ))
                            |> ignore
                    ))
            | LspRequestReferences request ->
                lspContinueWith (fun () ->
                    let client = lspClientForRequest request

                    client.SendReferences(
                        request.Path,
                        request.Position,
                        fun outcome ->
                            Task.Run(fun () ->
                                queue.Enqueue(
                                    LspReferencesResolved(
                                        Result.map lspResolveLocations outcome,
                                        request.EditTick,
                                        request.BufferId
                                    )
                                ))
                            |> ignore
                    ))
            | LspRequestHover request ->
                lspContinueWith (fun () ->
                    let client = lspClientForRequest request

                    client.SendHover(
                        request.Path,
                        request.Position,
                        fun outcome -> queue.Enqueue(LspHoverResolved(outcome, request.EditTick, request.BufferId))
                    ))
            | LspFetchLog name ->
                Task.Run(fun () ->
                    let clients =
                        lock lspLock (fun () ->
                            [ for KeyValue(_, client) in lspClients do
                                  match name with
                                  | None -> yield client
                                  | Some serverName when client.Config.Name = serverName -> yield client
                                  | Some _ -> () ])

                    let title =
                        match name with
                        | Some serverName -> $"LSP log — {serverName}"
                        | None -> "LSP log"

                    let lines =
                        match clients with
                        | [] -> [ "No running language-server client." ]
                        | [ client ] -> client.RecentLog()
                        | many ->
                            many
                            |> List.collect (fun client ->
                                client.RecentLog() |> List.map (fun line -> $"[{client.Config.Name}] {line}"))

                    queue.Enqueue(LspLogFetched(title, lines)))
                |> ignore

        // The pure update layer records only the pending chords; the
        // wall-clock deadline lives here so `update` stays deterministic.
        // Reset whenever a dispatch produces a new pending prefix.
        let mutable prefixDeadline: DateTime voption = ValueNone

        let dispatch model msg =
            // renderMsg/renderEffect are AOT-safe (no reflective DU printing), so
            // the trace runs fine under NativeAOT with --log. Still gate on
            // logWriter: the interpolation argument is evaluated eagerly, so this
            // avoids building the trace string every tick when --log is off.
            match logWriter with
            | Some _ -> log $"msg: {renderMsg msg}"
            | None -> ()

            let nextModel, effects = Editor.update msg model

            match logWriter with
            | Some _ -> effects |> List.iter (fun e -> log $"effect: {renderEffect e}")
            | None -> ()

            effects |> List.iter startEffect

            prefixDeadline <-
                match nextModel.PendingPrefix with
                | Some _ when nextModel.PendingPrefix <> model.PendingPrefix ->
                    ValueSome(DateTime.UtcNow.AddSeconds 1.0)
                | Some _ -> prefixDeadline
                | None -> ValueNone

            nextModel

        let userThemes, themeErrors = ConfigIO.loadUserThemes ()
        let config, configError = ConfigIO.load userThemes

        match highlightRegistry with
        | None -> log "highlight: failed to load tree-sitter — F# files will render plain"
        | Some _ -> log "highlight: loaded tree-sitter F# grammar"

        let initialModel, startupEffects =
            Editor.initWithInitialFile rootPath initialFile (consoleSize ()) config userThemes

        // Replace the default welcome notification with a warning if any
        // startup loaders failed. Otherwise leave the welcome in place.
        let initialModel =
            let allErrors = (Option.toList configError) @ themeErrors

            match allErrors with
            | [] -> initialModel
            | errs ->
                { initialModel with
                    Notification = Some(Notification.warning (String.concat "; " errs)) }

        startupEffects |> List.iter startEffect

        let mutable model = initialModel
        let mutable needsRender = true
        let terminal = Terminal.create ()
        Terminal.logCapabilities terminal log

        /// True if any path segment matches the workspace exclusion set
        /// (used by the FS watcher to filter noise).
        let isExcludedFsPath (path: string) =
            try
                let rel = Path.GetRelativePath(rootPath, path)

                rel.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
                |> Array.exists (fun part -> Workspace.excludedNames.Contains part)
            with _ ->
                false

        // FS events are debounced: onFsEvent stamps the time, and the main
        // loop dispatches WorkspaceChangedExternally once 300ms of quiet
        // elapses — avoids re-indexing on rapid save/rename sequences.
        let mutable lastFsChange: DateTime option = None

        let onFsEvent (e: FileSystemEventArgs) =
            if not (isExcludedFsPath e.FullPath) then
                lastFsChange <- Some DateTime.UtcNow

        // The FileSystemWatcher costs ~60 ms to spin up (FSEvents on macOS) and
        // only feeds live-reload, which never needs to be ready for the first
        // frame — so build it off the startup path. `lastFsChange` is already
        // written from watcher threads, so the cross-thread assignment is benign.
        let mutable watcher: FileSystemWatcher option = None

        let startWatcher () =
            try
                let w = new FileSystemWatcher(rootPath)
                w.IncludeSubdirectories <- true

                w.NotifyFilter <-
                    NotifyFilters.FileName
                    ||| NotifyFilters.DirectoryName
                    ||| NotifyFilters.LastWrite

                w.Changed.Add onFsEvent
                w.Created.Add onFsEvent
                w.Deleted.Add onFsEvent

                w.Renamed.Add(fun e ->
                    if not (isExcludedFsPath e.FullPath) then
                        lastFsChange <- Some DateTime.UtcNow)

                w.EnableRaisingEvents <- true
                watcher <- Some w
            with _ ->
                ()

        // Kicked off once, after the first frame is painted (see the render
        // block) so the ~60 ms FSEvents spin-up never competes with first paint.
        let mutable watcherStarted = false

        try
            Terminal.enter terminal
            let detectedCaps = Terminal.detectCapabilities terminal
            log $"capabilities (detected): {TerminalCapabilities.toLogString detectedCaps}"

            while not model.ShouldQuit do
                let size = consoleSize ()

                if size <> model.Terminal then
                    model <- dispatch model (Resize size)
                    needsRender <- true

                // Drain async effect results.
                let mutable next = Unchecked.defaultof<Msg>

                while queue.TryDequeue(&next) do
                    model <- dispatch model next
                    needsRender <- true

                match lastFsChange with
                | Some t when (DateTime.UtcNow - t).TotalMilliseconds > 300.0 ->
                    lastFsChange <- None
                    model <- dispatch model WorkspaceChangedExternally
                    needsRender <- true
                | _ -> ()

                match prefixDeadline with
                | ValueSome deadline when DateTime.UtcNow > deadline ->
                    model <- dispatch model SequenceTimedOut
                    needsRender <- true
                | _ -> ()

                if needsRender then
                    let frame = Layout.render model
                    Terminal.writeFrame terminal frame
                    needsRender <- false

                    // First frame is up — now spin up the file watcher in the
                    // background without having stolen cycles from first paint.
                    if not watcherStarted then
                        watcherStarted <- true
                        Task.Run startWatcher |> ignore

                match Terminal.tryReadEvent terminal with
                | Some(TerminalEvent.KeyEvent chord) ->
                    model <- dispatch model (KeyPressed chord)
                    needsRender <- true
                | Some(TerminalEvent.MouseEvent event) ->
                    match event.Action with
                    | Press ->
                        match MouseProtocol.toWheelTicks event with
                        | Some ticks -> model <- dispatch model (MouseScrolled(ticks, event.Position))
                        | None -> model <- dispatch model (MousePressed event)
                    | Release -> model <- dispatch model (MouseReleased event)
                    | Drag -> model <- dispatch model (MouseDragged event)

                    needsRender <- true
                | Some(TerminalEvent.FocusIn) ->
                    model <- dispatch model FocusGained
                    needsRender <- true
                | Some(TerminalEvent.FocusOut) ->
                    model <- dispatch model FocusLost
                    needsRender <- true
                | Some(TerminalEvent.Paste text) ->
                    model <- dispatch model (PastedText text)
                    needsRender <- true
                | None -> Thread.Sleep 16
        finally
            // Wait briefly for in-flight disk writes: ShouldQuit can flip
            // while a save chain is still running on the pool (Ctrl+S then
            // Ctrl+Q), and process exit would otherwise kill the write
            // mid-file. Bounded so a wedged disk can't hang quit forever.
            let pendingWrites =
                let bufferChains =
                    lock bufferSaveLock (fun () -> bufferSaveChains.Values |> Seq.toArray)

                let configChain = lock configSaveLock (fun () -> configSaveChain)
                Array.append bufferChains [| configChain |]

            try
                Task.WaitAll(pendingWrites, TimeSpan.FromSeconds 5.0) |> ignore
            with _ ->
                ()

            scanCts
            |> Option.iter (fun cts ->
                try
                    cts.Cancel()
                with _ ->
                    ()

                cts.Dispose())

            loadCts
            |> Option.iter (fun cts ->
                try
                    cts.Cancel()
                with _ ->
                    ()

                cts.Dispose())

            watcher |> Option.iter (fun w -> w.Dispose())

            // Cancel in-flight highlight parses, then dispose the registry
            // that owns the compiled queries. Languages themselves are not
            // disposed — they wrap loaded dylibs which the OS reclaims on
            // exit. Parsers and trees never outlive their parse task.
            for cts in highlightCts.Values do
                try
                    cts.Cancel()
                with _ ->
                    ()

                cts.Dispose()

            highlightRegistry
            |> Option.iter (fun r ->
                try
                    (r :> IDisposable).Dispose()
                with _ ->
                    ())

            try
                (pluginHost :> IDisposable).Dispose()
            with _ ->
                ()

            // Polite shutdown for every language server, chained as the
            // LAST item on the LSP task so any queued notification (the
            // user's final edits) drains first — and so no in-flight chain
            // task can lose a race with the teardown and respawn a client
            // into an abandoned table. Nothing enqueues chain work after
            // the dispatch loop exits, so the chain is complete once this
            // continuation has run; the bounded Wait keeps a wedged server
            // from stalling quit forever (at worst its child leaks once).
            lspContinueWith (fun () ->
                let clients =
                    lock lspLock (fun () ->
                        let clients = List.ofSeq lspClients.Values
                        lspClients.Clear()
                        clients)

                for client in clients do
                    try
                        (client :> IDisposable).Dispose()
                    with _ ->
                        ())

            let lspChain = lock lspLock (fun () -> lspSyncChain)

            try
                lspChain.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            with _ ->
                ()

            Terminal.leave terminal
            logWriter |> Option.iter (fun w -> w.Dispose())
