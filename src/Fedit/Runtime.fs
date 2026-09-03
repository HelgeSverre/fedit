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

    /// Read a file for `LoadFile`, classifying "not there" (missing file or
    /// missing parent directory) apart from real I/O errors so the editor
    /// can treat a permanent open of a nonexistent path as creating a new
    /// file. Other failures (permissions, the path is a directory) surface
    /// verbatim as `FileOpenFailed`. Reads raw bytes so the view decision
    /// is byte-accurate: `Hex.looksBinary` (a NUL in the first 8000 bytes)
    /// picks the latin1 hex projection, everything else gets the
    /// historical BOM-aware UTF-8 decode. The in-editor `:hex` flip
    /// re-projects the open buffer instead of reloading, so this is the
    /// only view decision point.
    let readFileForOpen (path: string) : Result<LoadedFile, FileOpenError> =
        try
            let bytes = File.ReadAllBytes path

            if Hex.looksBinary bytes then
                Result.Ok(LoadedBinary(Hex.bytesToText bytes))
            else
                Result.Ok(LoadedText(Hex.decodeText bytes))
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Result.Error FileNotFound
        | ex -> Result.Error(FileOpenFailed ex.Message)

    let private renderTextResult (result: Result<string, string>) =
        match result with
        | Result.Ok text -> $"Ok(<len={text.Length}>)"
        | Result.Error error -> $"Error({error})"

    let private renderUnitResult (result: Result<unit, string>) =
        match result with
        | Result.Ok() -> "Ok"
        | Result.Error error -> $"Error({error})"

    let private renderFileOpenResult (result: Result<LoadedFile, FileOpenError>) =
        match result with
        | Result.Ok(LoadedText text) -> $"Ok(text, <len={text.Length}>)"
        | Result.Ok(LoadedBinary latin1) -> $"Ok(binary, <len={latin1.Length}>)"
        | Result.Error FileNotFound -> "Error(FileNotFound)"
        | Result.Error(FileOpenFailed error) -> $"Error({error})"

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
        | MousePressed(e, clickCount) -> $"MousePressed({e.Position.Line}:{e.Position.Column}, clicks={clickCount})"
        | MouseReleased e -> $"MouseReleased({e.Position.Line}:{e.Position.Column})"
        | MouseDragged e -> $"MouseDragged({e.Position.Line}:{e.Position.Column})"
        | FocusGained -> "FocusGained"
        | FocusLost -> "FocusLost"
        | WorkspaceLoaded(complete, Result.Ok _) -> $"WorkspaceLoaded(Ok, complete={complete})"
        | WorkspaceLoaded(_, Result.Error error) -> $"WorkspaceLoaded(Error({error}))"
        | FileOpened(path, intent, target, result) ->
            $"FileOpened({path}, {renderIntent intent}, target={renderTarget target}, {renderFileOpenResult result})"
        | BufferSaved(bufferId, path, revision, result) ->
            let rendered =
                match result with
                | Result.Ok backupStatus -> $"Ok(backup={backupStatus})"
                | Result.Error error -> $"Error({error})"

            $"BufferSaved(buffer={bufferId}, path={path}, revision={revision}, {rendered})"
        | ConfigSaved result -> $"ConfigSaved({renderUnitResult result})"
        | ConfigFileReady(Result.Ok path) -> $"ConfigFileReady(Ok({path}))"
        | ConfigFileReady(Result.Error error) -> $"ConfigFileReady(Error({error}))"
        | ClipboardCopied result -> $"ClipboardCopied({renderUnitResult result})"
        | ClipboardPasted result -> $"ClipboardPasted({renderTextResult result})"
        | PastedText text -> $"PastedText(<len={text.Length}>)"
        | SearchCompleted(bufferId, query, matches) ->
            $"SearchCompleted(buffer={bufferId}, queryLen={query.Length}, matches={matches.Length})"
        | WorkspaceChangedExternally -> "WorkspaceChangedExternally"
        | ReplayStepReady -> "ReplayStepReady"
        | ReplayFenceTimeout -> "ReplayFenceTimeout"
        | HighlightParsed(bufferId, editTick, spans) ->
            $"HighlightParsed(buffer={bufferId}, tick={editTick}, spans={spans.Length})"
        | SelectionLadderReady(bufferId, editTick, selStart, selEnd, ranges) ->
            $"SelectionLadderReady(buffer={bufferId}, tick={editTick}, sel={selStart}..{selEnd}, steps={ranges.Length})"
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
        | MacrosLoaded(registers, errors, announce) ->
            $"MacrosLoaded(registers={registers.Count}, errors={errors.Length}, announce={announce})"
        | MacrosSaved result -> $"MacrosSaved({renderUnitResult result})"
        | MacrosFileReady(Result.Ok path) -> $"MacrosFileReady(Ok({path}))"
        | MacrosFileReady(Result.Error error) -> $"MacrosFileReady(Error({error}))"
        | LspServerStatusChanged(name, status) -> $"LspServerStatusChanged({name}, {renderLspServerStatus status})"
        | LspDocumentSyncSkipped(path, chars, limit) -> $"LspDocumentSyncSkipped({path}, chars={chars}, limit={limit})"
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
        | ScanWorkspace(path, _) -> $"ScanWorkspace({path})"
        | LoadFile(path, intent, target) -> $"LoadFile({path}, {renderIntent intent}, target={renderTarget target})"
        | SaveBuffer(bufferId, path, revision, contents, binary) ->
            $"SaveBuffer(buffer={bufferId}, path={path}, revision={revision}, contentsLen={contents.Length}, binary={binary})"
        | SaveConfig _ -> "SaveConfig(<config>)"
        | EnsureConfigFile _ -> "EnsureConfigFile(<config>)"
        | ClipboardCopy text -> $"ClipboardCopy(<len={text.Length}>)"
        | ClipboardPaste -> "ClipboardPaste"
        | RunSearch(bufferId, query, document, hex) ->
            $"RunSearch(buffer={bufferId}, queryLen={query.Length}, haystackLen={PieceTable.length document}, hex={hex})"
        | ParseHighlight(bufferId, language, document, editTick) ->
            $"ParseHighlight(buffer={bufferId}, lang={language}, tick={editTick}, docLen={PieceTable.length document})"
        | ComputeSelectionLadder(bufferId, language, _, editTick, selStart, selEnd) ->
            $"ComputeSelectionLadder(buffer={bufferId}, lang={language}, tick={editTick}, sel={selStart}..{selEnd})"
        | ScanPlugins disabled -> $"ScanPlugins(disabled={disabled.Count})"
        | RunPluginCommand(source, command, _) -> $"RunPluginCommand(source={source}, command={command})"
        | InstallPluginFromSource _ -> "InstallPluginFromSource(<source>)"
        | RemovePluginDir name -> $"RemovePluginDir({name})"
        | BuildPlugin pluginPath -> $"BuildPlugin({pluginPath})"
        | ValidatePlugin path -> $"ValidatePlugin({path})"
        | RegisterLanguages specs -> $"RegisterLanguages({specs.Length})"
        | CancelPluginRuns(bufferId, editTick) -> $"CancelPluginRuns(buffer={bufferId}, tick={editTick})"
        | LoadKeybinds -> "LoadKeybinds"
        | LoadMacros announce -> $"LoadMacros(announce={announce})"
        | SaveMacros registers -> $"SaveMacros(registers={registers.Count})"
        | EnsureMacrosFile registers -> $"EnsureMacrosFile(registers={registers.Count})"
        | ReplayPump -> "ReplayPump"
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

    /// The `.gitignore` in `directory`, if the rules say to honour it and
    /// one exists. Unreadable files are treated as absent.
    let private gitignoreIn (rules: IgnoreRules) (directory: string) : Ignore.Gitignore option =
        if not rules.UseGitignore then
            None
        else
            let file = Path.Combine(directory, ".gitignore")

            try
                if File.Exists file then
                    Some(Ignore.parseGitignore (Paths.norm directory) (File.ReadAllText file))
                else
                    None
            with _ ->
                None

    /// Recursively build a FileNode tree, counting skipped/unreadable entries.
    /// `depth` bounds the walk: directories at depth 0 come back childless.
    /// `gitignores` is the ancestor chain, outermost first.
    let rec private scanNode
        (rules: IgnoreRules)
        (gitignores: Ignore.Gitignore list)
        (depth: int)
        (path: string)
        : FileNode * int =
        let attributes = File.GetAttributes path
        let isDirectory = attributes.HasFlag FileAttributes.Directory

        if isDirectory then
            if attributes.HasFlag FileAttributes.ReparsePoint || depth = 0 then
                makeNode path true [], 0
            else
                let mutable skipped = 0
                let children = ResizeArray<FileNode>()

                let gitignores =
                    match gitignoreIn rules path with
                    | Some own -> gitignores @ [ own ]
                    | None -> gitignores

                let keep child isDirectory =
                    not (Ignore.isIgnored rules gitignores (Paths.norm child) isDirectory)

                try
                    for childDir in Directory.EnumerateDirectories path do
                        if keep childDir true then
                            try
                                let node, childSkipped = scanNode rules gitignores (depth - 1) childDir
                                skipped <- skipped + childSkipped
                                children.Add node
                            with _ ->
                                skipped <- skipped + 1
                with _ ->
                    skipped <- skipped + 1

                try
                    for childFile in Directory.EnumerateFiles path do
                        if keep childFile false then
                            try
                                children.Add(makeNode childFile false [])
                            with _ ->
                                skipped <- skipped + 1
                with _ ->
                    skipped <- skipped + 1

                makeNode path true (List.ofSeq children), skipped
        else
            makeNode path false [], 0

    /// Full workspace walk under `rules` (what `ScanWorkspace` runs). Public
    /// so the ignore pipeline is testable end to end against a real tree.
    let scanWorkspace (rules: IgnoreRules) (rootPath: string) : FileNode * int =
        scanNode rules [] Int32.MaxValue rootPath

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

        /// Run `work` on the pool and post its Msg to the queue.
        let post (work: unit -> Msg) =
            Task.Run(fun () -> queue.Enqueue(work ())) |> ignore

        /// Exceptions become `Error message` so effect results stay data.
        let attempt (work: unit -> 'a) : Result<'a, string> =
            try
                Result.Ok(work ())
            with ex ->
                Result.Error ex.Message

        /// Chain `work` after `previous` so writes land in dispatch order
        /// regardless of pool scheduling; posts the Msg to the queue.
        let continueOn (previous: Task) (work: unit -> Msg) : Task =
            previous.ContinueWith((fun (_: Task) -> queue.Enqueue(work ())), TaskContinuationOptions.None)

        let mutable scanCts: CancellationTokenSource option = None
        let mutable workspaceScanned = false
        // Latest scan's rules, reused by the FS watcher filter. The root
        // `.gitignore` alone is enough there: a miss only costs a rescan.
        let mutable ignoreRules = Ignore.defaults
        let mutable rootGitignore: Ignore.Gitignore list = []
        let mutable loadCts: CancellationTokenSource option = None
        // Serialize config writes so two quick saves can't land
        // out of order on disk. Each new SaveConfig chains onto the previous
        // task, preserving dispatch order by construction.
        let configSaveLock = obj ()
        let mutable configSaveChain: Task = Task.CompletedTask
        // Serialize macros-file writes the same way (write-through saves
        // fire on every recording commit / register clear); `ensureFile`
        // joins the chain so an edit-flow create can't race a save.
        let macroSaveLock = obj ()
        let mutable macroSaveChain: Task = Task.CompletedTask
        // Serialize buffer writes per canonical path so repeated saves cannot
        // land out of dispatch order on disk.
        let bufferSaveLock = obj ()
        let bufferSaveChains = System.Collections.Generic.Dictionary<string, Task>()
        // Cancel previous incremental search before starting the next.
        let mutable searchCts: CancellationTokenSource option = None
        // Latest-wins highlight parse per buffer: a keystroke during a parse
        // cancels the stale one; `update` also drops stale results by tick.
        // In-flight plugin runs by the buffer they snapshotted, with the
        // snapshot's edit tick: `CancelPluginRuns` fires the older ones.
        let pluginRunLock = obj ()

        let pluginRuns =
            System.Collections.Generic.Dictionary<int, ResizeArray<int * CancellationTokenSource>>()

        let highlightCts =
            System.Collections.Generic.Dictionary<int, CancellationTokenSource>()
        // Config first: user grammars/query overrides feed the registry.
        let userThemes, themeErrors = ConfigIO.loadUserThemes ()
        let config, configError = ConfigIO.load userThemes

        // The interpreter owns all native tree-sitter objects; the Model only
        // ever sees span arrays posted back as `HighlightParsed`.
        let highlightRegistry = HighlightRegistry.tryCreateWith config.Languages

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

        let lspPendingDocuments =
            System.Collections.Generic.Dictionary<string, string * LspDocumentSync>()

        let lspSyncedDocuments = System.Collections.Generic.HashSet<string>()
        let lspSkippedDocuments = System.Collections.Generic.HashSet<string>()
        let mutable lspDocumentDrainScheduled = false
        let mutable resourceLimits = config.ResourceLimits

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

                    let client =
                        LspClient.create server rootPath callbacks resourceLimits.LspIncomingMessageBytes

                    lspClients[key] <- client
                    client)

        /// Resolve the client owning a position request (get-or-spawn, the
        /// document's pinned root — same resolution as document sync).
        let lspClientForRequest (request: LspPositionRequest) : LspClient =
            lspRegisterPathAlias request.Path
            lspClientFor request.Server (lspRootFor request.Server request.Path request.WorkspaceRoot)

        /// Position-keyed LSP request, chained after any pending document
        /// sync so the server sees the request-time text. The reply callback
        /// runs on the client's reader thread; `toMsg` may do disk reads
        /// (URI->path + preview-line enrichment), so it hops to the pool.
        let lspPositionRequest
            (request: LspPositionRequest)
            (send: LspClient -> string * Position * ('a -> unit) -> unit)
            (toMsg: 'a -> Msg)
            =
            lspContinueWith (fun () ->
                let client = lspClientForRequest request
                send client (request.Path, request.Position, fun outcome -> post (fun () -> toMsg outcome)))

        /// One preview line off disk for the location picker. The update
        /// layer swaps in the open buffer's line where the document is open;
        /// this covers everything else (unopened files, indexed workspace).
        let lspPreviewLine (path: string) (lineIndex: int) : string =
            try
                let canonicalPath = canonicalizePath path
                let info = FileInfo canonicalPath

                // Devices and FIFOs conventionally report zero length. Empty
                // regular files have no preview either, so avoid opening both.
                if not info.Exists || info.Length = 0L || lineIndex < 0 then
                    ""
                else
                    use stream =
                        new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

                    use reader = new StreamReader(stream, detectEncodingFromByteOrderMarks = true)
                    let preview = StringBuilder(min resourceLimits.LspPreviewChars 1024)
                    let stopwatch = System.Diagnostics.Stopwatch.StartNew()
                    let mutable line = 0
                    let mutable doneReading = false

                    let withinBudget () =
                        let withinBytes =
                            match resourceLimits.LspPreviewScanBytes with
                            | Some limit -> stream.Position <= int64 limit
                            | None -> true

                        let withinTime =
                            match resourceLimits.LspPreviewTimeoutMs with
                            | Some limit -> stopwatch.ElapsedMilliseconds <= int64 limit
                            | None -> true

                        withinBytes && withinTime

                    while not doneReading && withinBudget () do
                        match reader.Read() with
                        | -1 -> doneReading <- true
                        | value when char value = '\n' ->
                            if line = lineIndex then
                                doneReading <- true
                            else
                                line <- line + 1
                        | value when line = lineIndex && preview.Length < resourceLimits.LspPreviewChars ->
                            preview.Append(char value) |> ignore
                        | _ -> ()

                    if line = lineIndex then preview.ToString().Trim() else ""
            with _ ->
                ""

        /// URI -> canonical path + preview line, dropping non-file URIs.
        /// The path translates back through the symlink alias table so a
        /// location lands on the buffer the editor already has open, never
        /// a duplicate under the server's resolved spelling. Involves disk
        /// reads, so callers run it off the reader thread.
        let lspResolveLocations (locations: LspLocation list) : LspResolvedLocation list =
            let boundedLocations =
                match resourceLimits.LspLocationCount with
                | Some limit -> locations |> List.truncate limit
                | None -> locations

            boundedLocations
            |> List.choose (fun location ->
                LspUri.toPath location.Uri
                |> Option.map (fun serverPath ->
                    let path = lspTranslateServerPath serverPath
                    let position = LspPosition.toPosition location.Range.Start
                    path, position))
            |> List.toArray
            |> fun targets ->
                let resolved = Array.zeroCreate<LspResolvedLocation> targets.Length

                let options =
                    ParallelOptions(MaxDegreeOfParallelism = resourceLimits.LspPreviewConcurrency)

                Parallel.For(
                    0,
                    targets.Length,
                    options,
                    fun index ->
                        let path, position = targets[index]

                        resolved[index] <-
                            { Path = path
                              Position = position
                              Preview = lspPreviewLine path position.Line }
                )
                |> ignore

                List.ofArray resolved

        let cancelDispose (cts: CancellationTokenSource) =
            try
                cts.Cancel()
            with _ ->
                ()

            cts.Dispose()

        let cancelAndReplace (existing: CancellationTokenSource option) =
            existing |> Option.iter cancelDispose
            new CancellationTokenSource()

        let enqueueUnlessCancelled (token: CancellationToken) (msg: Msg) =
            if not token.IsCancellationRequested then
                queue.Enqueue msg

        let startEffect effect =
            match effect with
            | ScanWorkspace(rootPath, rules) ->
                let cts = cancelAndReplace scanCts
                scanCts <- Some cts
                let token = cts.Token
                ignoreRules <- rules
                rootGitignore <- gitignoreIn rules rootPath |> Option.toList

                let scan depth =
                    try
                        let tree, skipped = scanNode rules [] depth rootPath
                        let sorted, byPath, files = Workspace.preCompute rootPath tree
                        Result.Ok(sorted, byPath, files, skipped)
                    with ex ->
                        Result.Error ex.Message

                // Shallow pass first so the sidebar paints at once; the full
                // walk (seconds on a cold, large tree) lands after. Only for
                // the first scan: a rescan already has a full tree to show.
                let shallowFirst = not workspaceScanned
                workspaceScanned <- true

                Task.Run(fun () ->
                    if shallowFirst then
                        enqueueUnlessCancelled token (WorkspaceLoaded(false, scan 1))

                    enqueueUnlessCancelled token (WorkspaceLoaded(true, scan Int32.MaxValue)))
                |> ignore
            | LoadFile(path, intent, target) ->
                let cts = cancelAndReplace loadCts
                loadCts <- Some cts
                let token = cts.Token

                Task.Run(fun () ->
                    enqueueUnlessCancelled token (FileOpened(path, intent, target, readFileForOpen path)))
                |> ignore
            | SaveBuffer(bufferId, path, revision, contents, binary) ->
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

                    bufferSaveChains[key] <-
                        continueOn previous (fun () ->
                            BufferSaved(
                                bufferId,
                                path,
                                revision,
                                attempt (fun () ->
                                    if binary then
                                        // Hex edits are easy to get wrong; keep the
                                        // original bytes recoverable before the first
                                        // overwrite. A failed copy fails the save.
                                        let sourceIsLink = (FileInfo(Path.GetFullPath path)).LinkTarget <> null
                                        let backedUp = File.backupOnce path
                                        File.writeAllBytesAtomic path (Hex.textToBytes contents)

                                        if backedUp then BackupCreated
                                        elif sourceIsLink then BackupSkippedSymlink
                                        else BackupNotNeeded
                                    else
                                        File.writeAllTextAtomic path contents
                                        BackupNotNeeded)
                            )))
            | SaveConfig config ->
                // Chain onto the previous config-save task so writes land in
                // dispatch order regardless of pool scheduling.
                lock configSaveLock (fun () ->
                    configSaveChain <-
                        continueOn configSaveChain (fun () -> ConfigSaved(attempt (fun () -> ConfigIO.save config))))
            | EnsureConfigFile config ->
                post (fun () ->
                    ConfigFileReady(
                        attempt (fun () ->
                            let configPath = ConfigIO.path ()

                            if not (File.Exists configPath) then
                                ConfigIO.save config

                            configPath)
                    ))
            | ClipboardCopy text -> post (fun () -> ClipboardCopied(attempt (fun () -> clipboardCopy text)))
            | RunSearch(bufferId, query, document, hex) ->
                // Cancel any in-flight search; the latest query wins.
                let cts = cancelAndReplace searchCts
                searchCts <- Some cts
                let token = cts.Token

                Task.Run(fun () ->
                    // Materialize the haystack here, off the UI thread; the
                    // effect carries only the shared piece table. The scan
                    // itself is `Buffer.findAllMatches` — the same core the
                    // search-next/search-previous repeat actions use, so the
                    // two paths can never disagree on match semantics. Hex
                    // buffers translate the query through `Hex.searchNeedle`
                    // and match byte-exactly — again the same core the hex
                    // repeat actions use.
                    let haystack = PieceTable.toString document

                    let matches =
                        if hex then
                            Hex.findAllExact (Hex.searchNeedle query) haystack
                        else
                            Buffer.findAllMatches query haystack

                    enqueueUnlessCancelled token (SearchCompleted(bufferId, query, matches)))
                |> ignore
            | ClipboardPaste -> post (fun () -> ClipboardPasted(attempt clipboardPaste))
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
            | ComputeSelectionLadder(bufferId, language, document, editTick, selStart, selEnd) ->
                // Discrete user action, not a hot path: no debounce or
                // cancellation. A stale result (buffer edited or caret moved
                // before it lands) is dropped by the `SelectionLadderReady`
                // edit-tick + requested-selection guards.
                let grammar =
                    highlightRegistry
                    |> Option.filter (fun registry -> (registry.TryGetLanguage language).IsSome)

                match grammar with
                | None -> ()
                | Some registry ->
                    Task.Run(fun () ->
                        try
                            let source = PieceTable.toString document

                            match Highlight.selectionLadder registry language source selStart selEnd with
                            | Some ranges when ranges.Length > 0 ->
                                queue.Enqueue(SelectionLadderReady(bufferId, editTick, selStart, selEnd, ranges))
                            | _ -> ()
                        with ex ->
                            log $"selection-ladder: parse failed for buffer {bufferId} ({language}): {ex.Message}")
                    |> ignore
            | ScanPlugins disabledPlugins ->
                Task.Run(fun () ->
                    let pluginsRoot = Path.Combine(ConfigIO.directory (), "plugins")
                    // `attempt`: a host reply the editor cannot parse (a newer
                    // host's action tag, a truncated frame) must surface as an
                    // error, not vanish inside an unobserved task.
                    queue.Enqueue(
                        PluginsScanned(
                            attempt (fun () -> pluginHost.Scan(pluginsRoot, disabledPlugins))
                            |> Result.bind id
                        )
                    ))
                |> ignore
            | CancelPluginRuns(bufferId, editTick) ->
                lock pluginRunLock (fun () ->
                    match pluginRuns.TryGetValue bufferId with
                    | true, runs ->
                        for tick, cts in runs do
                            if tick < editTick then
                                try
                                    cts.Cancel()
                                with _ ->
                                    ()
                    | _ -> ())
            | RunPluginCommand(source, command, context) ->
                let bufferId = context.ActiveBuffer.Id
                let cts = new CancellationTokenSource()
                let entry = context.ActiveBuffer.EditTick, cts

                lock pluginRunLock (fun () ->
                    match pluginRuns.TryGetValue bufferId with
                    | true, runs -> runs.Add entry
                    | _ -> pluginRuns[bufferId] <- ResizeArray [ entry ])

                post (fun () ->
                    let result =
                        try
                            attempt (fun () -> pluginHost.Invoke(command, context, cts.Token))
                            |> Result.bind id
                        finally
                            lock pluginRunLock (fun () ->
                                match pluginRuns.TryGetValue bufferId with
                                | true, runs ->
                                    runs.Remove entry |> ignore

                                    if runs.Count = 0 then
                                        pluginRuns.Remove bufferId |> ignore
                                | _ -> ())

                            cts.Dispose()

                    PluginActionsReady(source, result))
            | InstallPluginFromSource source ->
                post (fun () ->
                    let pluginsRoot = Path.Combine(ConfigIO.directory (), "plugins")

                    match attempt (fun () -> Plugins.install pluginsRoot source) with
                    | Result.Ok name -> PluginInstalled(name, Result.Ok())
                    | Result.Error message -> PluginInstalled("?", Result.Error message))
            | RemovePluginDir name ->
                post (fun () ->
                    let pluginsRoot = Path.Combine(ConfigIO.directory (), "plugins")
                    PluginRemoved(name, attempt (fun () -> Plugins.uninstall pluginsRoot name)))
            | BuildPlugin pluginPath ->
                post (fun () ->
                    let apiDll = Path.Combine(AppContext.BaseDirectory, "Fedit.PluginApi.dll")
                    let name = Path.GetFileName pluginPath

                    let outcome =
                        attempt (fun () ->
                            Plugins.tryParseManifest (Path.Combine(pluginPath, "plugin.json"))
                            |> Result.bind (fun manifest ->
                                Plugins.build
                                    apiDll
                                    { Manifest = manifest
                                      Path = pluginPath
                                      Status = Disabled
                                      Commands = []
                                      AsyncCommands = Map.empty
                                      Keybindings = []
                                      Hooks = []
                                      LanguageServers = []
                                      Languages = []
                                      Conflicts = [] })
                            |> Result.map ignore)

                    PluginBuildFinished(name, Result.bind id outcome))
            | RegisterLanguages specs -> highlightRegistry |> Option.iter (fun registry -> registry.AddLanguages specs)
            | ValidatePlugin path ->
                post (fun () ->
                    let manifestPath = Path.Combine(path, "plugin.json")

                    let outcome =
                        attempt (fun () ->
                            if not (File.Exists manifestPath) then
                                Result.Error $"No plugin.json found in {path}."
                            else
                                Plugins.tryParseManifest manifestPath
                                |> Result.map (fun manifest ->
                                    $"OK: {manifest.Name} {manifest.Version} (apiVersion {manifest.ApiVersion}); entryType={manifest.EntryType}"))

                    PluginValidated(Result.bind id outcome))
            | LoadKeybinds -> post (fun () -> KeybindsLoaded(KeymapIO.load ()))
            | LoadMacros announce ->
                post (fun () ->
                    let registers, errors = MacroIO.load ()
                    MacrosLoaded(registers, errors, announce))
            | SaveMacros registers ->
                // Chain onto the previous macros-file write so write-through
                // saves land in dispatch order (config-save pattern).
                lock macroSaveLock (fun () ->
                    macroSaveChain <-
                        continueOn macroSaveChain (fun () -> MacrosSaved(attempt (fun () -> MacroIO.save registers))))
            | EnsureMacrosFile registers ->
                // Joins the macros-file write chain: a create for the edit
                // flow must not interleave with an in-flight write-through
                // save of the same file.
                lock macroSaveLock (fun () ->
                    macroSaveChain <-
                        continueOn macroSaveChain (fun () ->
                            MacrosFileReady(
                                attempt (fun () ->
                                    MacroIO.ensureFile registers
                                    // Normalized here — the OS boundary —
                                    // so the buffer opened on it compares
                                    // canonically on every platform.
                                    Paths.norm (MacroIO.path ()))
                            )))
            | ReplayPump ->
                // Pure queue manipulation — runs synchronously on the
                // dispatch thread. Round-tripping the step trigger through
                // the queue lets pending input and effect completions
                // interleave with macro steps instead of the whole replay
                // running ahead of them.
                queue.Enqueue ReplayStepReady
            | LspSyncDocuments(workspaceRoot, documents) ->
                // Latest wins per path before any PieceTable is materialized.
                // This bounds rapid-edit amplification while preserving the
                // serialized protocol order for the final state.
                let scheduleDrain =
                    lock lspLock (fun () ->
                        for document in documents do
                            lspPendingDocuments[document.Path] <- workspaceRoot, document

                        if lspDocumentDrainScheduled then
                            false
                        else
                            lspDocumentDrainScheduled <- true
                            true)

                if scheduleDrain then
                    lspContinueWith (fun () ->
                        let mutable draining = true

                        while draining do
                            let pending =
                                lock lspLock (fun () ->
                                    if lspPendingDocuments.Count = 0 then
                                        lspDocumentDrainScheduled <- false
                                        draining <- false
                                        []
                                    else
                                        let batch = List.ofSeq lspPendingDocuments.Values
                                        lspPendingDocuments.Clear()
                                        batch)

                            for pendingRoot, document in pending do
                                try
                                    lspRegisterPathAlias document.Path
                                    let rootPath = lspRootFor document.Server document.Path pendingRoot

                                    let withinDocumentLimit text =
                                        match resourceLimits.LspDocumentChars with
                                        | Some limit -> PieceTable.length text <= limit
                                        | None -> true

                                    match document.Kind with
                                    | LspDocumentSyncKind.Opened text when withinDocumentLimit text ->
                                        lock lspLock (fun () -> lspSkippedDocuments.Remove document.Path |> ignore)
                                        let client = lspClientFor document.Server rootPath

                                        client.NotifyOpened(
                                            document.Path,
                                            document.LanguageId,
                                            document.Version,
                                            PieceTable.toString text
                                        )

                                        lock lspLock (fun () -> lspSyncedDocuments.Add document.Path |> ignore)
                                    | LspDocumentSyncKind.Changed text when withinDocumentLimit text ->
                                        lock lspLock (fun () -> lspSkippedDocuments.Remove document.Path |> ignore)
                                        let client = lspClientFor document.Server rootPath

                                        let wasSynced =
                                            lock lspLock (fun () -> lspSyncedDocuments.Contains document.Path)

                                        if wasSynced then
                                            client.NotifyChanged(
                                                document.Path,
                                                document.Version,
                                                PieceTable.toString text
                                            )
                                        else
                                            client.NotifyOpened(
                                                document.Path,
                                                document.LanguageId,
                                                document.Version,
                                                PieceTable.toString text
                                            )

                                            lock lspLock (fun () -> lspSyncedDocuments.Add document.Path |> ignore)
                                    | LspDocumentSyncKind.Opened text
                                    | LspDocumentSyncKind.Changed text ->
                                        let length = PieceTable.length text

                                        let wasSynced =
                                            lock lspLock (fun () -> lspSyncedDocuments.Remove document.Path)

                                        if wasSynced then
                                            let client = lspClientFor document.Server rootPath
                                            client.NotifyClosed document.Path

                                        if lock lspLock (fun () -> lspSkippedDocuments.Add document.Path) then
                                            queue.Enqueue(
                                                LspDocumentSyncSkipped(
                                                    document.Path,
                                                    length,
                                                    resourceLimits.LspDocumentChars.Value
                                                )
                                            )

                                        log
                                            $"lsp: skipped {document.Path}: document has {length} chars (limit {resourceLimits.LspDocumentChars.Value})"
                                    | LspDocumentSyncKind.Closed ->
                                        lock lspLock (fun () -> lspSkippedDocuments.Remove document.Path |> ignore)

                                        if lock lspLock (fun () -> lspSyncedDocuments.Remove document.Path) then
                                            let client = lspClientFor document.Server rootPath
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
                            lspSyncedDocuments.Clear()
                            lspSkippedDocuments.Clear()

                            matching |> List.map snd)

                    for client in removed do
                        try
                            client.Shutdown()
                        with ex ->
                            log $"lsp: shutdown failed for {client.Config.Name}: {ex.Message}")
            | LspRequestDefinition request ->
                lspPositionRequest request (fun client -> client.SendDefinition) (fun outcome ->
                    LspDefinitionResolved(Result.map lspResolveLocations outcome, request.EditTick, request.BufferId))
            | LspRequestReferences request ->
                lspPositionRequest request (fun client -> client.SendReferences) (fun outcome ->
                    LspReferencesResolved(Result.map lspResolveLocations outcome, request.EditTick, request.BufferId))
            | LspRequestHover request ->
                lspPositionRequest request (fun client -> client.SendHover) (fun outcome ->
                    LspHoverResolved(outcome, request.EditTick, request.BufferId))
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
        // Reset whenever a dispatch produces a new pending prefix. 3 s:
        // long enough to read the which-key panel the prefix opens.
        let mutable prefixDeadline: DateTime voption = ValueNone

        // Macro replay fence safety valve, also wall clock and also here:
        // while the model waits on a fenced step's async result, a 5 s
        // deadline is armed; if no completion pumps the queue in time,
        // ReplayFenceTimeout cancels the replay with an error naming the
        // step instead of leaving it parked forever.
        let mutable replayFenceDeadline: DateTime voption = ValueNone

        // Replay fairness/cancellability bound: ReplayPump enqueues
        // ReplayStepReady synchronously, so an unbounded queue drain would
        // run a whole replay (every step of every iteration) inside one
        // tick — no render, no terminal read, and no way to cancel a
        // runaway `replay-macro:<r>:<count>`. The drain below dispatches at
        // most this many replay steps per tick, then paints and reads input
        // (the Escape cancel path); the idle sleep is skipped while the
        // queue holds work, so a bounded replay still runs at full speed.
        let maxReplayStepsPerTick = 100

        // Multi-click synthesis also lives here, not in `update`: the
        // double-click window is a wall-clock decision, like prefixDeadline.
        // A left press on the same cell within the window bumps the count
        // (1 → 2 → 3 → …); `update` maps 2 to word- and 3 to line-selection.
        // Any other button press breaks the chain.
        let multiClickWindow = TimeSpan.FromMilliseconds 500.0
        let mutable lastLeftClick: (DateTime * Position * int) voption = ValueNone

        let clickCountFor (event: MouseEvent) =
            if event.Button = LeftButton then
                let now = DateTime.UtcNow

                let count =
                    match lastLeftClick with
                    | ValueSome(at, position, previous) when position = event.Position && now - at <= multiClickWindow ->
                        previous + 1
                    | _ -> 1

                lastLeftClick <- ValueSome(now, event.Position, count)
                count
            else
                lastLeftClick <- ValueNone
                1

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
                    ValueSome(DateTime.UtcNow.AddSeconds 3.0)
                | Some _ -> prefixDeadline
                | None -> ValueNone

            replayFenceDeadline <-
                match nextModel.Replay with
                | Some state when not (Map.isEmpty state.PendingFences) ->
                    // Re-arm whenever the pending fence set CHANGES — a
                    // completion that chains into a fresh fenced effect
                    // (ConfigFileReady → LoadFile) gets its own full
                    // window instead of inheriting the remainder of the
                    // previous fence's deadline.
                    let previousFences =
                        match model.Replay with
                        | Some previousState -> previousState.PendingFences
                        | None -> Map.empty

                    if state.PendingFences <> previousFences then
                        ValueSome(DateTime.UtcNow.AddSeconds 5.0)
                    else
                        replayFenceDeadline
                | _ -> ValueNone

            nextModel

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
                let startupWarning = Notification.warning (String.concat "; " errs)

                { initialModel with
                    Notification = Some startupWarning
                    // Keep the `:messages` log in step with what is shown —
                    // the warning replaces the seeded welcome hint.
                    NotificationLog = [ startupWarning ] }

        startupEffects |> List.iter startEffect

        let mutable model = initialModel
        let mutable needsRender = true
        let terminal = Terminal.create ()
        Terminal.logCapabilities terminal log

        /// True if the path or any ancestor segment is ignored by the latest
        /// scan's rules (used by the FS watcher to filter noise).
        let isExcludedFsPath (path: string) =
            try
                let normalized = Paths.norm path

                Path.GetRelativePath(rootPath, path).Split([| '/'; '\\' |])
                |> Array.exists (fun part -> List.contains part ignoreRules.Names)
                || (ignoreRules.UseGitignore
                    && Ignore.matchesGitignore rootGitignore normalized (Directory.Exists path))
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

                // Structure only: the tree never shows mtimes, and LastWrite
                // would trigger a full rescan on every save.
                w.NotifyFilter <- NotifyFilters.FileName ||| NotifyFilters.DirectoryName

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

                // Drain async effect results, budgeting replay steps (see
                // maxReplayStepsPerTick above) so a long replay stays
                // interruptible and visibly paints progress.
                let mutable next = Unchecked.defaultof<Msg>
                let mutable replayStepBudget = maxReplayStepsPerTick
                let mutable draining = true

                while draining && queue.TryDequeue(&next) do
                    model <- dispatch model next
                    needsRender <- true

                    match next with
                    | ReplayStepReady ->
                        replayStepBudget <- replayStepBudget - 1

                        if replayStepBudget <= 0 then
                            draining <- false
                    | _ -> ()

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

                match replayFenceDeadline with
                | ValueSome deadline when DateTime.UtcNow > deadline ->
                    replayFenceDeadline <- ValueNone
                    model <- dispatch model ReplayFenceTimeout
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
                        | None -> model <- dispatch model (MousePressed(event, clickCountFor event))
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
                | None ->
                    // Idle nap only when the queue is empty: a
                    // budget-paused replay (or an effect completion that
                    // landed mid-tick) must be drained on the next
                    // iteration, not 16 ms later.
                    if queue.IsEmpty then
                        Thread.Sleep 16
        finally
            // Wait briefly for in-flight disk writes: ShouldQuit can flip
            // while a save chain is still running on the pool (Ctrl+S then
            // Ctrl+Q), and process exit would otherwise kill the write
            // mid-file. Bounded so a wedged disk can't hang quit forever.
            let pendingWrites =
                let bufferChains =
                    lock bufferSaveLock (fun () -> bufferSaveChains.Values |> Seq.toArray)

                let configChain = lock configSaveLock (fun () -> configSaveChain)
                let macroChain = lock macroSaveLock (fun () -> macroSaveChain)
                Array.append bufferChains [| configChain; macroChain |]

            try
                Task.WaitAll(pendingWrites, TimeSpan.FromSeconds 5.0) |> ignore
            with _ ->
                ()

            scanCts |> Option.iter cancelDispose
            loadCts |> Option.iter cancelDispose

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

                // Dispose concurrently: each Shutdown can spend its full
                // notify-grace-kill budget, and paying it once per server in
                // sequence made quit scale with the number of live clients.
                let disposals =
                    clients
                    |> List.map (fun client ->
                        Task.Run(fun () ->
                            try
                                (client :> IDisposable).Dispose()
                            with _ ->
                                ()))
                    |> Array.ofList

                if disposals.Length > 0 then
                    try
                        Task.WaitAll(disposals, TimeSpan.FromSeconds 2.0) |> ignore
                    with _ ->
                        ())

            let lspChain = lock lspLock (fun () -> lspSyncChain)

            try
                lspChain.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            with _ ->
                ()

            Terminal.leave terminal
            logWriter |> Option.iter (fun w -> w.Dispose())
