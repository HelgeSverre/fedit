namespace Fedit

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent

[<RequireQualifiedAccess>]
module Runtime =
    let private utf8WithoutBom = UTF8Encoding false

    let private configDirectory () =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "fedit")

    let private configPath () =
        Path.Combine(configDirectory (), "config.json")

    let private themesDirectory () =
        Path.Combine(configDirectory (), "themes")

    let private optStr (s: string | null) =
        match s with
        | null -> None
        | value -> Some value

    let private getStringProp (root: System.Text.Json.JsonElement) (name: string) =
        match root.TryGetProperty(name: string) with
        | true, elem when elem.ValueKind = System.Text.Json.JsonValueKind.String -> optStr (elem.GetString())
        | _ -> None

    let private getIntProp (root: System.Text.Json.JsonElement) (name: string) =
        match root.TryGetProperty(name: string) with
        | true, elem when elem.ValueKind = System.Text.Json.JsonValueKind.Number -> Some(elem.GetInt32())
        | _ -> None

    let private clampInt low high value = max low (min high value)

    let private loadConfig (userThemes: Theme list) =
        let defaults = Config.defaults Themes.defaultTheme

        try
            let path = configPath ()

            if File.Exists path then
                let json = File.ReadAllText path
                use doc = System.Text.Json.JsonDocument.Parse json
                let root = doc.RootElement

                let theme =
                    getStringProp root "theme"
                    |> Option.bind (fun name -> Themes.tryFindIn userThemes name)
                    |> Option.defaultValue defaults.Theme

                let recent =
                    match root.TryGetProperty "recent" with
                    | true, elem when elem.ValueKind = System.Text.Json.JsonValueKind.Array ->
                        elem.EnumerateArray()
                        |> Seq.choose (fun item ->
                            if item.ValueKind = System.Text.Json.JsonValueKind.String then
                                optStr (item.GetString())
                            else
                                None)
                        |> Seq.toList
                    | _ -> defaults.Recent

                let completionLimit =
                    getIntProp root "completionLimit"
                    |> Option.defaultValue defaults.CompletionLimit
                    |> clampInt 1 64

                let sidebarIndent =
                    getIntProp root "sidebarIndent"
                    |> Option.defaultValue defaults.SidebarIndent
                    |> clampInt 0 16

                let sidebarWidth =
                    getIntProp root "sidebarWidth"
                    |> Option.defaultValue defaults.SidebarWidth
                    |> clampInt 10 200

                let dockHeight =
                    getIntProp root "dockHeight"
                    |> Option.defaultValue defaults.DockHeight
                    |> clampInt 1 40

                let wordMotion =
                    match getStringProp root "wordMotion" with
                    | Some "nextWordStart" -> NextWordStart
                    | Some "wordEnd" -> WordEnd
                    | _ -> defaults.WordMotion

                let pageOverlap =
                    getIntProp root "pageOverlap"
                    |> Option.defaultValue defaults.PageOverlap
                    |> clampInt 0 32

                let treePageJump =
                    getIntProp root "treePageJump"
                    |> Option.defaultValue defaults.TreePageJump
                    |> clampInt 1 500

                let tabWidth =
                    getIntProp root "tabWidth"
                    |> Option.defaultValue defaults.TabWidth
                    |> clampInt 1 16

                { Theme = theme
                  Recent = recent
                  CompletionLimit = completionLimit
                  SidebarIndent = sidebarIndent
                  SidebarWidth = sidebarWidth
                  DockHeight = dockHeight
                  WordMotion = wordMotion
                  PageOverlap = pageOverlap
                  TreePageJump = treePageJump
                  TabWidth = tabWidth }
            else
                defaults
        with _ ->
            defaults

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

    let private loadUserThemes () =
        try
            let dir = themesDirectory ()

            if not (Directory.Exists dir) then
                []
            else
                Directory.EnumerateFiles(dir, "*.json")
                |> Seq.choose (fun file ->
                    try
                        let json = File.ReadAllText file
                        use doc = System.Text.Json.JsonDocument.Parse json
                        let root = doc.RootElement

                        let fallbackName =
                            Path.GetFileNameWithoutExtension file
                            |> optStr
                            |> Option.defaultValue "user-theme"

                        let name =
                            getStringProp root "name"
                            |> Option.defaultValue fallbackName
                            |> fun s -> s.ToLowerInvariant()

                        let description =
                            getStringProp root "description" |> Option.defaultValue $"User theme '{name}'"

                        match
                            getIntProp root "accent",
                            getIntProp root "statusFg",
                            getIntProp root "statusBg",
                            getIntProp root "selectedBg",
                            getIntProp root "currentLine"
                        with
                        | Some a, Some sf, Some sb, Some seb, Some cl ->
                            Some
                                { Name = name
                                  Description = description
                                  Accent = a
                                  StatusFg = sf
                                  StatusBg = sb
                                  SelectedBg = seb
                                  CurrentLine = cl }
                        | _ -> None
                    with _ ->
                        None)
                |> Seq.toList
        with _ ->
            []

    let private saveConfig (config: Config) =
        let directory = configDirectory ()
        Directory.CreateDirectory directory |> ignore
        let path = configPath ()

        let root =
            if File.Exists path then
                try
                    let existing = File.ReadAllText path

                    match System.Text.Json.Nodes.JsonNode.Parse existing with
                    | :? System.Text.Json.Nodes.JsonObject as obj -> obj
                    | _ -> System.Text.Json.Nodes.JsonObject()
                with _ ->
                    System.Text.Json.Nodes.JsonObject()
            else
                System.Text.Json.Nodes.JsonObject()

        let recentArray = System.Text.Json.Nodes.JsonArray()

        for item in config.Recent do
            recentArray.Add(System.Text.Json.Nodes.JsonValue.Create item)

        let wordMotionStr =
            match config.WordMotion with
            | WordEnd -> "wordEnd"
            | NextWordStart -> "nextWordStart"

        root["theme"] <- System.Text.Json.Nodes.JsonValue.Create config.Theme.Name
        root["recent"] <- recentArray
        root["completionLimit"] <- System.Text.Json.Nodes.JsonValue.Create config.CompletionLimit
        root["sidebarIndent"] <- System.Text.Json.Nodes.JsonValue.Create config.SidebarIndent
        root["sidebarWidth"] <- System.Text.Json.Nodes.JsonValue.Create config.SidebarWidth
        root["dockHeight"] <- System.Text.Json.Nodes.JsonValue.Create config.DockHeight
        root["wordMotion"] <- System.Text.Json.Nodes.JsonValue.Create wordMotionStr
        root["pageOverlap"] <- System.Text.Json.Nodes.JsonValue.Create config.PageOverlap
        root["treePageJump"] <- System.Text.Json.Nodes.JsonValue.Create config.TreePageJump
        root["tabWidth"] <- System.Text.Json.Nodes.JsonValue.Create config.TabWidth

        let options = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
        let json = root.ToJsonString options
        File.WriteAllText(path, json + "\n", utf8WithoutBom)

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
            | SaveBuffer(bufferId, path, contents) ->
                Task.Run(fun () ->
                    let msg =
                        try
                            ensureDirectoryFor path
                            File.WriteAllText(path, contents, utf8WithoutBom)
                            BufferSaved(bufferId, path, Result.Ok())
                        with ex ->
                            BufferSaved(bufferId, path, Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore
            | SaveConfig config ->
                Task.Run(fun () ->
                    let msg =
                        try
                            saveConfig config
                            ConfigSaved(Result.Ok())
                        with ex ->
                            ConfigSaved(Result.Error ex.Message)

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
            | ClipboardPaste ->
                Task.Run(fun () ->
                    let msg =
                        try
                            ClipboardPasted(Result.Ok(clipboardPaste ()))
                        with ex ->
                            ClipboardPasted(Result.Error ex.Message)

                    queue.Enqueue msg)
                |> ignore

        let dispatch model msg =
            log $"msg: {msg}"
            let nextModel, effects = Editor.update msg model
            effects |> List.iter (fun e -> log $"effect: {e}")
            effects |> List.iter startEffect
            nextModel

        let userThemes = loadUserThemes ()
        let config = loadConfig userThemes

        let initialModel, startupEffects =
            Editor.init rootPath (consoleSize ()) config userThemes

        startupEffects |> List.iter startEffect

        let mutable model = initialModel
        let mutable needsRender = true
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

                if needsRender then
                    model |> Layout.render |> Renderer.render writer
                    needsRender <- false

                if Console.KeyAvailable then
                    match Console.ReadKey true |> Input.tryMap with
                    | Some key ->
                        model <- dispatch model (KeyPressed key)
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
            Renderer.leave writer
            logWriter |> Option.iter (fun w -> w.Dispose())
