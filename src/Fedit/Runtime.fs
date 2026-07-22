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
    /// verbatim as `FileOpenFailed`.
    let readFileForOpen (path: string) : Result<string, FileOpenError> =
        try
            Result.Ok(File.ReadAllText path)
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

    let private renderFileOpenResult (result: Result<string, FileOpenError>) =
        match result with
        | Result.Ok text -> $"Ok(<len={text.Length}>)"
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
        | WorkspaceLoaded(Result.Ok _) -> "WorkspaceLoaded(Ok)"
        | WorkspaceLoaded(Result.Error error) -> $"WorkspaceLoaded(Error({error}))"
        | FileOpened(path, intent, target, result) ->
            $"FileOpened({path}, {renderIntent intent}, target={renderTarget target}, {renderFileOpenResult result})"
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
        | ReplayStepReady -> "ReplayStepReady"
        | ReplayFenceTimeout -> "ReplayFenceTimeout"
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
        | MacrosLoaded(registers, errors, announce) ->
            $"MacrosLoaded(registers={registers.Count}, errors={errors.Length}, announce={announce})"
        | MacrosSaved result -> $"MacrosSaved({renderUnitResult result})"
        | MacrosFileReady(Result.Ok path) -> $"MacrosFileReady(Ok({path}))"
        | MacrosFileReady(Result.Error error) -> $"MacrosFileReady(Error({error}))"

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
        | LoadMacros announce -> $"LoadMacros(announce={announce})"
        | SaveMacros registers -> $"SaveMacros(registers={registers.Count})"
        | EnsureMacrosFile registers -> $"EnsureMacrosFile(registers={registers.Count})"
        | ReplayPump -> "ReplayPump"

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

        let log (s: string) =
            match logWriter with
            | Some w ->
                w.WriteLine($"{DateTime.UtcNow:o} {s}")
                w.Flush()
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
        let highlightCts =
            System.Collections.Generic.Dictionary<int, CancellationTokenSource>()
        // The interpreter owns all native tree-sitter objects; the Model only
        // ever sees span arrays posted back as `HighlightParsed`.
        let highlightRegistry = HighlightRegistry.tryCreate ()

        // Plugins load in a separate JIT process so the editor can ship as
        // NativeAOT. Scans and invocations go through this client; the Model
        // only ever sees the registry (stub Run closures) and PluginActions.
        let pluginHost = new PluginHostClient(PluginHostClient.defaultHostPath ())

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
                    enqueueUnlessCancelled token (FileOpened(path, intent, target, readFileForOpen path)))
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
                    // effect carries only the shared piece table. The scan
                    // itself is `Buffer.findAllMatches` — the same core the
                    // search-next/search-previous repeat actions use, so the
                    // two paths can never disagree on match semantics.
                    let haystack = PieceTable.toString document
                    let matches = Buffer.findAllMatches query haystack
                    enqueueUnlessCancelled token (SearchCompleted(bufferId, query, matches)))
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
            | LoadMacros announce ->
                Task.Run(fun () ->
                    let registers, errors = MacroIO.load ()
                    queue.Enqueue(MacrosLoaded(registers, errors, announce)))
                |> ignore
            | SaveMacros registers ->
                // Chain onto the previous macros-file write so write-through
                // saves land in dispatch order (config-save pattern).
                lock macroSaveLock (fun () ->
                    macroSaveChain <-
                        macroSaveChain.ContinueWith(
                            (fun (_: Task) ->
                                let msg =
                                    try
                                        MacroIO.save registers
                                        MacrosSaved(Result.Ok())
                                    with ex ->
                                        MacrosSaved(Result.Error ex.Message)

                                queue.Enqueue msg),
                            TaskContinuationOptions.None
                        ))
            | EnsureMacrosFile registers ->
                // Joins the macros-file write chain: a create for the edit
                // flow must not interleave with an in-flight write-through
                // save of the same file.
                lock macroSaveLock (fun () ->
                    macroSaveChain <-
                        macroSaveChain.ContinueWith(
                            (fun (_: Task) ->
                                let msg =
                                    try
                                        MacroIO.ensureFile registers
                                        // Normalized here — the OS boundary —
                                        // so the buffer opened on it compares
                                        // canonically on every platform.
                                        MacrosFileReady(Result.Ok(Paths.norm (MacroIO.path ())))
                                    with ex ->
                                        MacrosFileReady(Result.Error ex.Message)

                                queue.Enqueue msg),
                            TaskContinuationOptions.None
                        ))
            | ReplayPump ->
                // Pure queue manipulation — runs synchronously on the
                // dispatch thread. Round-tripping the step trigger through
                // the queue lets pending input and effect completions
                // interleave with macro steps instead of the whole replay
                // running ahead of them.
                queue.Enqueue ReplayStepReady

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

            Terminal.leave terminal
            logWriter |> Option.iter (fun w -> w.Dispose())
