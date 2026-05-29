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

    let private optStr (s: string | null) =
        match s with
        | null -> None
        | value -> Some value

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
        info.UseShellExecute <- false
        use proc = startProcessOrFail info
        proc.StandardInput.Write text
        proc.StandardInput.Close()
        proc.WaitForExit()

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
        info.UseShellExecute <- false
        use proc = startProcessOrFail info
        let output = proc.StandardOutput.ReadToEnd()
        proc.WaitForExit()
        output

    let private ensureDirectoryFor (path: string) =
        Path.GetDirectoryName path
        |> optStr
        |> Option.iter (fun directory ->
            if not (String.IsNullOrWhiteSpace directory) then
                Directory.CreateDirectory directory |> ignore)

    let private makeNode (path: string) isDirectory children : FileNode =
        let rawName = Path.GetFileName path |> optStr |> Option.defaultValue path

        { Path = path
          Name = if String.IsNullOrWhiteSpace rawName then path else rawName
          IsDirectory = isDirectory
          Children = children }

    let private shouldSkip (path: string) =
        Path.GetFileName path
        |> optStr
        |> Option.map Workspace.excludedNames.Contains
        |> Option.defaultValue false

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

    let private consoleSize () =
        { Width = max 1 Console.WindowWidth
          Height = max 1 Console.WindowHeight }

    let run rootPath (logPath: string option) =
        Console.OutputEncoding <- Encoding.UTF8
        Console.TreatControlCAsInput <- true

        let logWriter: StreamWriter option =
            logPath
            |> Option.map (fun path ->
                ensureDirectoryFor path
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
        // Phase 12.2: serialize config writes so two quick saves can't land
        // out of order on disk. Each new SaveConfig chains onto the previous
        // task, preserving dispatch order by construction.
        let configSaveLock = obj ()
        let mutable configSaveChain: Task = Task.CompletedTask
        // Phase 12.3: cancel previous incremental search before starting the next.
        let mutable searchCts: CancellationTokenSource option = None

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
                            WorkspaceLoaded(Result.Ok(scanNode rootPath))
                        with ex ->
                            WorkspaceLoaded(Result.Error ex.Message)

                    enqueueUnlessCancelled token msg)
                |> ignore
            | LoadFile path ->
                let cts = cancelAndReplace loadCts
                loadCts <- Some cts
                let token = cts.Token

                Task.Run(fun () ->
                    let msg =
                        try
                            FileOpened(path, Result.Ok(File.ReadAllText path))
                        with ex ->
                            FileOpened(path, Result.Error ex.Message)

                    enqueueUnlessCancelled token msg)
                |> ignore
            | SaveBuffer(bufferId, path, revision, contents) ->
                Task.Run(fun () ->
                    let msg =
                        try
                            ensureDirectoryFor path
                            File.WriteAllText(path, contents, utf8WithoutBom)
                            BufferSaved(bufferId, path, revision, Result.Ok())
                        with ex ->
                            BufferSaved(bufferId, path, revision, Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
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
            | RunSearch(bufferId, query, haystack) ->
                // Cancel any in-flight search; the latest query wins.
                let cts = cancelAndReplace searchCts
                searchCts <- Some cts
                let token = cts.Token

                Task.Run(fun () ->
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
            | ScanPlugins ->
                Task.Run(fun () ->
                    let msg =
                        try
                            let pluginsRoot = Path.Combine(ConfigIO.directory (), "plugins")
                            let apiDll = Path.Combine(AppContext.BaseDirectory, "Fedit.PluginApi.dll")
                            // MVP: enable map is empty — discovered plugins
                            // default to enabled. Per-plugin disable lives in
                            // a future config field.
                            let registry = Plugins.scanAndLoad pluginsRoot apiDll Map.empty log
                            PluginsScanned(Result.Ok registry)
                        with ex ->
                            PluginsScanned(Result.Error ex.Message)

                    queue.Enqueue msg)
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
                                      Keybindings = [] }

                                match Plugins.build apiDll loaded with
                                | Result.Ok _ -> PluginBuildFinished(name, Result.Ok())
                                | Result.Error e -> PluginBuildFinished(name, Result.Error e)
                        with ex ->
                            PluginBuildFinished(name, Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore

        let dispatch model msg =
            log $"msg: {msg}"
            let nextModel, effects = Editor.update msg model
            effects |> List.iter (fun e -> log $"effect: {e}")
            effects |> List.iter startEffect
            nextModel

        let userThemes, themeErrors = ConfigIO.loadUserThemes ()
        let config, configError = ConfigIO.load userThemes

        let highlightRegistry = HighlightRegistry.tryCreate ()

        match highlightRegistry with
        | None -> log "highlight: failed to load tree-sitter — F# files will render plain"
        | Some _ -> log "highlight: loaded tree-sitter F# grammar"

        let initialModel, startupEffects =
            Editor.init rootPath (consoleSize ()) config userThemes highlightRegistry

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
        let mutable previousFrame: Screen voption = ValueNone
        let writer = Console.Out

        let isExcludedFsPath (path: string) =
            try
                let rel = Path.GetRelativePath(rootPath, path)

                rel.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
                |> Array.exists (fun part -> Workspace.excludedNames.Contains part)
            with _ ->
                false

        let mutable lastFsChange: DateTime option = None

        let onFsEvent (e: FileSystemEventArgs) =
            if not (isExcludedFsPath e.FullPath) then
                lastFsChange <- Some DateTime.UtcNow

        let watcher =
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
                Some w
            with _ ->
                None

        try
            Renderer.enter writer

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

                match model.PendingPrefix with
                | Some(_, deadline) when DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > deadline ->
                    model <- dispatch model SequenceTimedOut
                    needsRender <- true
                | _ -> ()

                if needsRender then
                    let frame = Layout.render model
                    Renderer.render writer previousFrame frame
                    previousFrame <- ValueSome frame
                    needsRender <- false

                if Console.KeyAvailable then
                    let keyInfo = Console.ReadKey true

                    // SGR mouse reports arrive as ESC [ < Cb ; Cx ; Cy (M|m).
                    // .NET's ReadKey decodes known key sequences (arrows, Fn)
                    // itself, so a bare ESC trailed by more bytes is almost
                    // always a mouse event — drain the CSI and parse it.
                    let mouseTicks =
                        if keyInfo.Key = ConsoleKey.Escape && Console.KeyAvailable then
                            let c1 = Console.ReadKey true

                            if c1.KeyChar = '[' && Console.KeyAvailable then
                                let c2 = Console.ReadKey true

                                if c2.KeyChar = '<' then
                                    let sb = System.Text.StringBuilder("[<")
                                    let mutable terminated = false
                                    let mutable guard = 0

                                    while not terminated && Console.KeyAvailable && guard < 32 do
                                        let c = Console.ReadKey true
                                        sb.Append c.KeyChar |> ignore

                                        if c.KeyChar = 'M' || c.KeyChar = 'm' then
                                            terminated <- true

                                        guard <- guard + 1

                                    if terminated then
                                        Input.parseSgrMouse (sb.ToString())
                                    else
                                        None
                                else
                                    None
                            else
                                None
                        else
                            None

                    match mouseTicks with
                    | Some ticks ->
                        model <- dispatch model (MouseScrolled ticks)
                        needsRender <- true
                    | None ->
                        match Input.tryMap keyInfo with
                        | Some chord ->
                            model <- dispatch model (KeyPressed chord)
                            needsRender <- true
                        | None -> ()
                else
                    Thread.Sleep 16
        finally
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

            // Dispose per-buffer highlight parsers/trees, then the
            // registry that owns the compiled queries. Languages
            // themselves are not disposed — they wrap loaded dylibs
            // which the OS reclaims on exit.
            model.HighlightStates |> Map.iter (fun _ s -> Highlight.dispose s)

            highlightRegistry
            |> Option.iter (fun r ->
                try
                    (r :> IDisposable).Dispose()
                with _ ->
                    ())

            Renderer.leave writer
            logWriter |> Option.iter (fun w -> w.Dispose())
