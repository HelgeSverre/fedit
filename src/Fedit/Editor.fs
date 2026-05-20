namespace Fedit

open System
open System.IO

[<RequireQualifiedAccess>]
module Editor =
    let private emptyPrompt =
        { Active = false
          Text = ""
          Cursor = 0
          Mode = FilePicker
          Parsed = Empty
          Completions = []
          SelectedCompletion = 0
          History = []
          HistoryIndex = None
          SearchPreview = None }

    let private initialPanels (config: Config) =
        { SidebarVisible = true
          SidebarWidth = config.SidebarWidth
          DockHeight = config.DockHeight }

    let private initialEditors =
        let scratch = Buffer.createEmpty 1

        { Buffers = Map.ofList [ 1, scratch ]
          ActiveBufferId = 1
          NextBufferId = 2 }

    let private notify notification model =
        { model with
            Notification = notification }

    let activeBufferState model =
        model.Editors.Buffers[model.Editors.ActiveBufferId]

    let private updateActiveBuffer transform model =
        let transformed = activeBufferState model |> transform

        let sidebarOffset =
            if model.Panels.SidebarVisible then
                model.Panels.SidebarWidth + 1
            else
                0

        let viewportWidth =
            max 1 (model.Terminal.Width - sidebarOffset - Buffer.gutterWidth transformed)

        let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
        let updated = transformed |> Buffer.ensureViewport viewportHeight viewportWidth

        { model with
            Editors =
                { model.Editors with
                    Buffers = model.Editors.Buffers |> Map.add updated.Id updated } }

    let private workspaceFiles (workspace: WorkspaceState) =
        let rec collect (node: FileNode) =
            if node.IsDirectory then
                node.Children |> List.collect collect
            else
                [ Path.GetRelativePath(workspace.RootPath, node.Path) ]

        workspace.Tree |> Option.map collect |> Option.defaultValue []

    let private filePickerCompletions model query =
        let limit = model.Config.CompletionLimit
        let recent = model.Config.Recent
        let files = workspaceFiles model.Workspace
        let needle = query

        let matches (path: string) =
            if String.IsNullOrEmpty needle then
                true
            else
                let fileName = Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

                fileName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

        let recentRelative =
            recent
            |> List.map (fun path ->
                try
                    Path.GetRelativePath(model.Workspace.RootPath, path)
                with _ ->
                    path)

        let recentFiltered =
            List.zip recent recentRelative |> List.filter (fun (_, rel) -> matches rel)

        let workspaceFiltered =
            let recentSet = System.Collections.Generic.HashSet(recentRelative)

            files |> List.filter (fun rel -> not (recentSet.Contains rel) && matches rel)

        let recentItems =
            recentFiltered
            |> List.map (fun (absolute, relative) ->
                { Label = relative
                  ApplyText = relative
                  Detail = "recent"
                  Kind = PathItem })

        let workspaceItems =
            workspaceFiltered
            |> List.map (fun rel ->
                { Label = rel
                  ApplyText = rel
                  Detail = "workspace"
                  Kind = PathItem })

        recentItems @ workspaceItems |> List.truncate limit

    let private buffersCompletions model query =
        model.Editors.Buffers
        |> Map.toList
        |> List.filter (fun (id, buffer) ->
            String.IsNullOrEmpty query
            || buffer.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)
            || (string id).StartsWith query)
        |> List.truncate model.Config.CompletionLimit
        |> List.map (fun (id, buffer) ->
            let detail = buffer.FilePath |> Option.defaultValue "scratch"

            { Label = $"{id}  {buffer.Name}"
              ApplyText = $"{id}"
              Detail = detail
              Kind = PathItem })

    let private commandCompletions model query =
        let buffersForCompletion =
            model.Editors.Buffers
            |> Map.toList
            |> List.map (fun (id, buffer) -> id, buffer.Name, buffer.FilePath)

        Commands.completions
            { RootPath = model.Workspace.RootPath
              Files = workspaceFiles model.Workspace
              Recent = model.Config.Recent
              Buffers = buffersForCompletion
              Themes = Themes.merge model.UserThemes
              CompletionLimit = model.Config.CompletionLimit }
            query

    let private refreshPrompt model : Model * Effect list =
        let prompt = model.Prompt
        let mode = Prompt.modeOf prompt.Text
        let argument = Prompt.argumentOf prompt.Text

        let isNumericGoto =
            let trimmed = argument.TrimStart()
            trimmed.Length > 0 && Char.IsDigit trimmed[0]

        let completions, parsed =
            match mode with
            | FilePicker -> filePickerCompletions model prompt.Text, Empty
            | Command when isNumericGoto -> [], Commands.parseGoto argument
            | Command -> commandCompletions model argument, Commands.parse argument
            | Buffers -> buffersCompletions model argument, Empty
            | Search -> [], Empty

        let selectedIndex =
            if completions.IsEmpty then
                0
            else
                min prompt.SelectedCompletion (completions.Length - 1)

        // Keep stale search highlights visible while a new query is in
        // flight; the SearchCompleted handler overwrites once results land.
        let nextSearchPreview =
            match mode with
            | Search -> prompt.SearchPreview
            | _ -> None

        let nextModel =
            { model with
                Prompt =
                    { prompt with
                        Mode = mode
                        Parsed = parsed
                        Completions = completions
                        SelectedCompletion = selectedIndex
                        SearchPreview = nextSearchPreview } }

        let effects =
            match mode with
            | Search ->
                let query = Prompt.argumentOf nextModel.Prompt.Text

                if query.Length = 0 then
                    []
                else
                    let buffer = activeBufferState nextModel
                    let haystack = Buffer.text buffer
                    [ RunSearch(buffer.Id, query, haystack) ]
            | _ -> []

        nextModel, effects

    let private openPrompt (initialText: string) model =
        { model with
            Focus = Prompt
            Prompt =
                { model.Prompt with
                    Active = true
                    Text = initialText
                    Cursor = initialText.Length
                    HistoryIndex = None
                    SelectedCompletion = 0
                    SearchPreview = None } }
        |> refreshPrompt

    let private closePrompt model =
        { model with
            Focus = Editor
            Prompt =
                { model.Prompt with
                    Active = false
                    Text = ""
                    Cursor = 0
                    Mode = FilePicker
                    Parsed = Empty
                    Completions = []
                    SelectedCompletion = 0
                    HistoryIndex = None
                    SearchPreview = None } }

    let private resolvePath (rootPath: string) (path: string) =
        if Path.IsPathRooted path then
            path
        else
            Path.GetFullPath(Path.Combine(rootPath, path))

    let private insertPromptText value model =
        let prompt = model.Prompt
        let cursor = max 0 (min prompt.Cursor prompt.Text.Length)
        let nextText = prompt.Text.Insert(cursor, value)

        { model with
            Prompt =
                { prompt with
                    Text = nextText
                    Cursor = cursor + value.Length
                    SelectedCompletion = 0 } }
        |> refreshPrompt

    let private replacePromptText value model =
        { model with
            Prompt =
                { model.Prompt with
                    Text = value
                    Cursor = value.Length
                    SelectedCompletion = 0 } }
        |> refreshPrompt

    let private prefixOf mode =
        match mode with
        | FilePicker -> ""
        | Command -> ":"
        | Search -> "/"
        | Buffers -> "@"

    /// Apply a completion's ApplyText to the prompt, preserving the active
    /// mode's prefix.
    let private applyCompletion (item: CompletionItem) model =
        replacePromptText (prefixOf model.Prompt.Mode + item.ApplyText) model

    let private deletePromptBackward model =
        let prompt = model.Prompt

        if prompt.Cursor = 0 then
            // Backspace on empty (or at start of empty Text) closes the prompt.
            closePrompt model, []
        else
            { model with
                Prompt =
                    { prompt with
                        Text = prompt.Text.Remove(prompt.Cursor - 1, 1)
                        Cursor = prompt.Cursor - 1
                        SelectedCompletion = 0 } }
            |> refreshPrompt

    let private deletePromptForward model =
        let prompt = model.Prompt

        if prompt.Cursor >= prompt.Text.Length then
            model, []
        else
            { model with
                Prompt =
                    { prompt with
                        Text = prompt.Text.Remove(prompt.Cursor, 1)
                        SelectedCompletion = 0 } }
            |> refreshPrompt

    let private saveActiveBuffer customPath model =
        let buffer = activeBufferState model

        let targetPath =
            match customPath, buffer.FilePath with
            | Some path, _ -> Some(resolvePath model.Workspace.RootPath path)
            | None, Some existing -> Some existing
            | None, None -> None

        match targetPath with
        | Some path -> model, [ SaveBuffer(buffer.Id, path, buffer.EditTick, Buffer.serialize buffer) ]
        | None ->
            let opened, effects = openPrompt ":writeas " model
            opened |> notify (Some(Notification.warning "Scratch buffers need a path.")), effects

    let private pushHistory (text: string) model =
        let trimmed = text.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            model
        else
            { model with
                Prompt =
                    { model.Prompt with
                        History =
                            trimmed :: (model.Prompt.History |> List.filter ((<>) trimmed))
                            |> List.truncate 20 } }

    let private switchBuffer offset model =
        let ids = model.Editors.Buffers |> Map.keys |> Seq.toList

        if ids.IsEmpty then
            model
        else
            let currentIndex =
                ids
                |> List.tryFindIndex ((=) model.Editors.ActiveBufferId)
                |> Option.defaultValue 0

            let nextIndex =
                if offset > 0 then
                    (currentIndex + 1) % ids.Length
                else
                    (currentIndex - 1 + ids.Length) % ids.Length

            { model with
                Editors =
                    { model.Editors with
                        ActiveBufferId = ids[nextIndex] } }
            |> notify (Some(Notification.info $"Buffer {nextIndex + 1}/{ids.Length}"))

    let private executeCommand command model =
        match command with
        | Open path ->
            let absolutePath = resolvePath model.Workspace.RootPath path

            match
                model.Editors.Buffers
                |> Map.toList
                |> List.tryFind (fun (_, buffer) -> buffer.FilePath = Some absolutePath)
            with
            | Some(bufferId, _) ->
                { model with
                    Editors =
                        { model.Editors with
                            ActiveBufferId = bufferId }
                    Workspace = Workspace.selectPath absolutePath model.Workspace
                    Focus = Editor }
                |> notify (Some(Notification.info $"Activated {Path.GetFileName absolutePath}")),
                []
            | None -> { model with Focus = Editor }, [ LoadFile absolutePath ]
        | Write -> saveActiveBuffer None model
        | WriteAs path -> saveActiveBuffer (Some path) model
        | Quit -> { model with ShouldQuit = true }, [ SaveConfig model.Config ]
        | ToggleSidebar ->
            { model with
                Panels =
                    { model.Panels with
                        SidebarVisible = not model.Panels.SidebarVisible } },
            []
        | FocusTree -> { model with Focus = Sidebar }, []
        | FocusEditor -> { model with Focus = Editor }, []
        | ReloadWorkspace -> model, [ ScanWorkspace model.Workspace.RootPath ]
        | NextBuffer -> switchBuffer 1 model, []
        | PreviousBuffer -> switchBuffer -1 model, []
        | Theme name ->
            match Themes.tryFindIn model.UserThemes name with
            | Some theme ->
                let nextConfig = { model.Config with Theme = theme }

                { model with Config = nextConfig }
                |> notify (Some(Notification.info $"Theme: {theme.Name}")),
                [ SaveConfig nextConfig ]
            | None -> model |> notify (Some(Notification.error $"Unknown theme '{name}'.")), []
        | Recent path ->
            let absolute = resolvePath model.Workspace.RootPath path

            match
                model.Editors.Buffers
                |> Map.toList
                |> List.tryFind (fun (_, buffer) -> buffer.FilePath = Some absolute)
            with
            | Some(bufferId, _) ->
                { model with
                    Editors =
                        { model.Editors with
                            ActiveBufferId = bufferId }
                    Workspace = Workspace.selectPath absolute model.Workspace
                    Focus = Editor }
                |> notify (Some(Notification.info $"Activated {Path.GetFileName absolute}")),
                []
            | None -> { model with Focus = Editor }, [ LoadFile absolute ]
        | SwitchBuffer bufferRef ->
            let buffers = model.Editors.Buffers

            let target =
                match bufferRef with
                | ById id when buffers.ContainsKey id -> Some id
                | ById _ -> None
                | ByName name ->
                    buffers
                    |> Map.toList
                    |> List.tryFind (fun (_, b) -> b.Name = name || b.FilePath = Some name)
                    |> Option.map fst

            let label =
                match bufferRef with
                | ById id -> string id
                | ByName name -> name

            match target with
            | Some id ->
                { model with
                    Editors =
                        { model.Editors with
                            ActiveBufferId = id }
                    Focus = Editor }
                |> notify (Some(Notification.info $"Buffer {id}")),
                []
            | None -> model |> notify (Some(Notification.error $"Unknown buffer '{label}'.")), []
        | Command.Goto(line, column) ->
            let targetLine = max 0 (line - 1)
            let targetCol = max 0 ((Option.defaultValue 1 column) - 1)

            let target =
                { Line = targetLine
                  Column = targetCol }

            let moved =
                model
                |> updateActiveBuffer (fun buffer ->
                    let idx = Buffer.positionToIndex target buffer
                    Buffer.moveToOffset idx buffer)

            { moved with Focus = Editor }, []

    let private moveSearchMatch delta model =
        let prompt = model.Prompt

        match prompt.SearchPreview with
        | Some preview when not preview.Matches.IsEmpty ->
            let count = preview.Matches.Length
            let nextIdx = ((preview.Current + delta) % count + count) % count
            let target = preview.Matches[nextIdx]
            let modelUpdated = updateActiveBuffer (Buffer.moveToOffset target) model

            { modelUpdated with
                Prompt =
                    { modelUpdated.Prompt with
                        SearchPreview = Some { preview with Current = nextIdx } } }
        | _ -> model

    let private withClearedSearch workspace = Workspace.clearSearch workspace

    let private runSidebar key model =
        match key with
        | Character c ->
            { model with
                Workspace = Workspace.appendSearch c model.Workspace },
            []
        | Backspace when model.Workspace.SearchBuffer.Length > 0 ->
            { model with
                Workspace = Workspace.backspaceSearch model.Workspace },
            []
        | Up ->
            { model with
                Workspace = Workspace.moveSelection -1 (withClearedSearch model.Workspace) },
            []
        | Down ->
            { model with
                Workspace = Workspace.moveSelection 1 (withClearedSearch model.Workspace) },
            []
        | PageUp ->
            { model with
                Workspace = Workspace.moveSelection -model.Config.TreePageJump (withClearedSearch model.Workspace) },
            []
        | PageDown ->
            { model with
                Workspace = Workspace.moveSelection model.Config.TreePageJump (withClearedSearch model.Workspace) },
            []
        | Home ->
            { model with
                Workspace = Workspace.moveHome (withClearedSearch model.Workspace) },
            []
        | End ->
            { model with
                Workspace = Workspace.moveEnd (withClearedSearch model.Workspace) },
            []
        | Left ->
            let cleared = withClearedSearch model.Workspace

            let nextWorkspace =
                match Workspace.tryCollapseSelected cleared with
                | Some collapsed -> collapsed
                | None -> Workspace.selectParent cleared

            { model with Workspace = nextWorkspace }, []
        | Right ->
            { model with
                Workspace = Workspace.expandSelected (withClearedSearch model.Workspace) },
            []
        | Enter ->
            let workspace, action =
                Workspace.activateSelected (withClearedSearch model.Workspace)

            match action with
            | SidebarOpenFile path -> { model with Workspace = workspace }, [ LoadFile path ]
            | SidebarNoOp -> { model with Workspace = workspace }, []
        | Escape ->
            { model with
                Workspace = withClearedSearch model.Workspace
                Focus = Editor },
            []
        | _ -> model, []

    let private runEditor key model =
        let hasSelection = (activeBufferState model).Selection.IsSome

        let editTransform editFn =
            if hasSelection then
                Buffer.deleteSelection >> editFn
            else
                editFn

        // Cursor motion that drops any existing selection.
        let move transform =
            updateActiveBuffer (Buffer.clearSelection >> transform) model, []

        // Shifted motion that extends the selection through the new cursor.
        let extend transform =
            updateActiveBuffer (Buffer.extendSelectionToCursor >> transform) model, []

        // Page Up/Down both use the same viewport-aware jump distance.
        let pageJump direction =
            let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
            let jump = max 1 (viewportHeight - model.Config.PageOverlap)
            move (direction jump)

        match key with
        | Character value ->
            updateActiveBuffer (editTransform (Buffer.insertText (string value)) >> Buffer.clearSelection) model, []
        | Enter -> updateActiveBuffer (editTransform Buffer.insertNewline >> Buffer.clearSelection) model, []
        | Backspace when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | Backspace -> updateActiveBuffer Buffer.backspace model, []
        | Delete when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | Delete -> updateActiveBuffer Buffer.deleteForward model, []
        | Left -> move Buffer.moveLeft
        | Right -> move Buffer.moveRight
        | Up -> move Buffer.moveUp
        | Down -> move Buffer.moveDown
        | Home -> move Buffer.moveHome
        | End -> move Buffer.moveEnd
        | ShiftLeft -> extend Buffer.moveLeft
        | ShiftRight -> extend Buffer.moveRight
        | ShiftUp -> extend Buffer.moveUp
        | ShiftDown -> extend Buffer.moveDown
        | ShiftHome -> extend Buffer.moveHome
        | ShiftEnd -> extend Buffer.moveEnd
        | PageUp -> pageJump Buffer.movePageUp
        | PageDown -> pageJump Buffer.movePageDown
        | Tab -> move (Buffer.indent model.Config.TabWidth)
        | ShiftTab -> move (Buffer.unindent model.Config.TabWidth)
        | AltLeft -> move Buffer.moveLeftWord
        | AltRight -> move (Buffer.moveRightWord model.Config.WordMotion)
        | CtrlBackspace when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | CtrlBackspace -> updateActiveBuffer Buffer.backspaceWord model, []
        | CtrlDelete when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | CtrlDelete -> updateActiveBuffer (Buffer.deleteForwardWord model.Config.WordMotion) model, []
        | _ -> model, []

    let private cycleCompletion delta model =
        let prompt = model.Prompt

        if prompt.Completions.IsEmpty then
            model
        else
            let count = prompt.Completions.Length
            let nextIndex = ((prompt.SelectedCompletion + delta) % count + count) % count

            { model with
                Prompt =
                    { prompt with
                        SelectedCompletion = nextIndex } }

    let private applyHistory delta model =
        let prompt = model.Prompt

        if prompt.History.IsEmpty then
            model, []
        else
            let index =
                match prompt.HistoryIndex, delta with
                | Some value, d when d < 0 -> max 0 (value - 1)
                | Some value, _ -> min (prompt.History.Length - 1) (value + 1)
                | None, d when d < 0 -> prompt.History.Length - 1
                | None, _ -> 0

            replacePromptText
                prompt.History[index]
                { model with
                    Prompt =
                        { prompt with
                            HistoryIndex = Some index } }

    let private runPrompt key model =
        let prompt = model.Prompt

        match key with
        | Escape -> closePrompt model, []
        | Left ->
            { model with
                Prompt =
                    { prompt with
                        Cursor = max 0 (prompt.Cursor - 1) } },
            []
        | Right ->
            { model with
                Prompt =
                    { prompt with
                        Cursor = min prompt.Text.Length (prompt.Cursor + 1) } },
            []
        | Home ->
            { model with
                Prompt = { prompt with Cursor = 0 } },
            []
        | End ->
            { model with
                Prompt =
                    { prompt with
                        Cursor = prompt.Text.Length } },
            []
        | Backspace -> deletePromptBackward model
        | Delete -> deletePromptForward model
        | Character value -> insertPromptText (string value) model
        | Tab -> cycleCompletion 1 model, []
        | ShiftTab -> cycleCompletion -1 model, []
        | Up ->
            match prompt.Mode with
            | Search -> moveSearchMatch -1 model, []
            | _ -> cycleCompletion -1 model, []
        | Down ->
            match prompt.Mode with
            | Search -> moveSearchMatch 1 model, []
            | _ -> cycleCompletion 1 model, []
        | AltUp -> applyHistory -1 model
        | AltDown -> applyHistory 1 model
        | Enter ->
            match prompt.Mode with
            | Search -> moveSearchMatch 1 model, []
            | FilePicker ->
                match prompt.Completions |> List.tryItem prompt.SelectedCompletion with
                | Some item ->
                    let remembered = pushHistory prompt.Text model
                    let closed = closePrompt remembered
                    executeCommand (Open item.ApplyText) closed
                | None -> model, []
            | Buffers ->
                let bufferRefOf (text: string) =
                    match Int32.TryParse text with
                    | true, id -> ById id
                    | _ -> ByName text

                match prompt.Completions |> List.tryItem prompt.SelectedCompletion with
                | Some item ->
                    let remembered = pushHistory prompt.Text model
                    let closed = closePrompt remembered
                    executeCommand (SwitchBuffer(bufferRefOf item.ApplyText)) closed
                | None ->
                    let argument = Prompt.argumentOf prompt.Text

                    if String.IsNullOrWhiteSpace argument then
                        model, []
                    else
                        let remembered = pushHistory prompt.Text model
                        let closed = closePrompt remembered
                        executeCommand (SwitchBuffer(bufferRefOf (argument.Trim()))) closed
            | Command ->
                match prompt.Parsed with
                | Ready command ->
                    let remembered = pushHistory prompt.Text model
                    let closed = closePrompt remembered
                    executeCommand command closed
                | Pending _ when not prompt.Completions.IsEmpty ->
                    match prompt.Completions |> List.tryItem prompt.SelectedCompletion with
                    | Some item -> applyCompletion item model
                    | None -> model, []
                | Pending message -> notify (Some(Notification.warning message)) model, []
                | Invalid message -> notify (Some(Notification.error message)) model, []
                | Empty -> closePrompt model, []
        | _ -> model, []

    let init rootPath size config userThemes =
        { Workspace = Workspace.create rootPath
          Editors = initialEditors
          Prompt = emptyPrompt
          Panels = initialPanels config
          Focus = Editor
          Terminal = size
          Notification = Some(Notification.info "Ctrl+P prompt  Ctrl+B tree  Ctrl+S save  Ctrl+Q quit")
          Config = config
          UserThemes = userThemes
          QuitArmed = false
          ShouldQuit = false },
        [ ScanWorkspace rootPath ]

    let private normalizeNewlines (text: string) =
        if text.Contains "\r\n" then
            text.Replace("\r\n", "\n"), "\r\n"
        else
            text, "\n"

    let update msg model =
        match msg with
        | Resize size -> { model with Terminal = size } |> updateActiveBuffer id, []
        | WorkspaceLoaded result ->
            match result with
            | Result.Ok(tree, skipped) ->
                let suffix = if skipped > 0 then $" ({skipped} skipped)" else ""

                { model with
                    Workspace = Workspace.setTree tree model.Workspace
                    Notification = Some(Notification.info $"Indexed {tree.Name}{suffix}") },
                []
            | Result.Error message -> notify (Some(Notification.error message)) model, []
        | FileOpened(path, result) ->
            match result with
            | Result.Ok contents ->
                let normalized, newline = normalizeNewlines contents
                let displayName = Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

                let buffer =
                    Buffer.fromText model.Editors.NextBufferId (Some path) displayName normalized newline

                let recent =
                    path :: (model.Config.Recent |> List.filter ((<>) path)) |> List.truncate 20

                let nextConfig = { model.Config with Recent = recent }

                // Recent is persisted at quit, not per file-open: avoids save
                // churn under the FS watcher and rapid open sequences. Phase
                // 14.2.
                { model with
                    Editors =
                        { model.Editors with
                            Buffers = model.Editors.Buffers |> Map.add buffer.Id buffer
                            ActiveBufferId = buffer.Id
                            NextBufferId = buffer.Id + 1 }
                    Workspace = Workspace.selectPath path model.Workspace
                    Focus = Editor
                    Config = nextConfig
                    Notification = Some(Notification.info $"Opened {buffer.Name}") },
                []
            | Result.Error message -> notify (Some(Notification.error $"Failed to open {path}: {message}")) model, []
        | BufferSaved(bufferId, path, revision, result) ->
            match result with
            | Result.Ok() ->
                match Map.tryFind bufferId model.Editors.Buffers with
                | None -> model, []
                | Some buffer ->
                    let updated = Buffer.markSaved revision path buffer

                    let note =
                        if updated.Dirty then
                            $"Saved {Path.GetFileName path} (continued editing)"
                        else
                            $"Saved {Path.GetFileName path}"

                    { model with
                        Editors =
                            { model.Editors with
                                Buffers = model.Editors.Buffers |> Map.add bufferId updated }
                        Notification = Some(Notification.info note) },
                    []
            | Result.Error message -> notify (Some(Notification.error $"Failed to save {path}: {message}")) model, []
        | ConfigSaved result ->
            match result with
            | Result.Ok() -> model, []
            | Result.Error message ->
                notify (Some(Notification.warning $"Theme set, but config save failed: {message}")) model, []
        | ClipboardCopied result ->
            match result with
            | Result.Ok() -> model, []
            | Result.Error message -> notify (Some(Notification.warning $"Clipboard copy failed: {message}")) model, []
        | WorkspaceChangedExternally -> model, [ ScanWorkspace model.Workspace.RootPath ]
        | SearchCompleted(bufferId, query, matches) ->
            // Drop stale results: prompt may have closed, mode changed, query
            // moved on, or the active buffer switched while the effect ran.
            let prompt = model.Prompt

            let isStale =
                not prompt.Active
                || prompt.Mode <> Search
                || Prompt.argumentOf prompt.Text <> query
                || model.Editors.ActiveBufferId <> bufferId

            if isStale then
                model, []
            else
                let preview = { Matches = matches; Current = 0 }

                let withPreview =
                    { model with
                        Prompt =
                            { prompt with
                                SearchPreview = Some preview } }

                match matches with
                | [] -> withPreview, []
                | first :: _ -> updateActiveBuffer (Buffer.moveToOffset first) withPreview, []
        | ClipboardPasted result ->
            match result with
            | Result.Ok pastedText when pastedText.Length > 0 ->
                let buffer = activeBufferState model

                let transform =
                    if buffer.Selection.IsSome then
                        Buffer.deleteSelection >> Buffer.insertText pastedText
                    else
                        Buffer.insertText pastedText

                updateActiveBuffer (transform >> Buffer.clearSelection) model, []
            | Result.Ok _ -> model, []
            | Result.Error message -> notify (Some(Notification.warning $"Paste failed: {message}")) model, []
        | KeyPressed key ->
            let model =
                if key = Ctrl 'q' then
                    model
                else
                    { model with QuitArmed = false }

            match key with
            | Ctrl 'q' ->
                let hasDirty = model.Editors.Buffers |> Map.exists (fun _ buffer -> buffer.Dirty)

                if model.QuitArmed || not hasDirty then
                    { model with
                        ShouldQuit = true
                        QuitArmed = false
                        Notification = None },
                    [ SaveConfig model.Config ]
                else
                    let dirtyCount =
                        model.Editors.Buffers
                        |> Map.toList
                        |> List.filter (fun (_, b) -> b.Dirty)
                        |> List.length

                    { model with
                        QuitArmed = true
                        Notification =
                            Some(
                                Notification.warning
                                    $"Unsaved changes in {dirtyCount} buffer(s). Press Ctrl+Q again to discard."
                            ) },
                    []
            | Ctrl 'p' ->
                openPrompt
                    ":"
                    { model with
                        Workspace = Workspace.clearSearch model.Workspace
                        Notification = None }
            | Ctrl 'o' ->
                openPrompt
                    ""
                    { model with
                        Workspace = Workspace.clearSearch model.Workspace
                        Notification = None }
            | Ctrl 'f' ->
                openPrompt
                    "/"
                    { model with
                        Workspace = Workspace.clearSearch model.Workspace
                        Notification = None }
            | Ctrl 'b' ->
                // Zed-style three-state toggle: hidden → show+focus;
                // visible-elsewhere → focus; visible-focused → hide+editor.
                let panels = model.Panels
                let cleared = { model with Notification = None }

                if not panels.SidebarVisible then
                    { cleared with
                        Panels = { panels with SidebarVisible = true }
                        Focus = Sidebar },
                    []
                elif cleared.Focus <> Sidebar then
                    { cleared with Focus = Sidebar }, []
                else
                    { cleared with
                        Panels = { panels with SidebarVisible = false }
                        Focus = Editor
                        Workspace = Workspace.clearSearch cleared.Workspace },
                    []
            | Ctrl 'e' ->
                { model with
                    Focus = Editor
                    Workspace = Workspace.clearSearch model.Workspace
                    Notification = None },
                []
            | Ctrl 's' -> saveActiveBuffer None { model with Notification = None }
            | Ctrl 'r' -> { model with Notification = None }, [ ScanWorkspace model.Workspace.RootPath ]
            | Ctrl 'z' -> updateActiveBuffer Buffer.undo { model with Notification = None }, []
            | Ctrl 'y' -> updateActiveBuffer Buffer.redo { model with Notification = None }, []
            | Ctrl 'a' -> updateActiveBuffer Buffer.selectAll { model with Notification = None }, []
            | Ctrl 'c' ->
                let buffer = activeBufferState model
                let text = Buffer.selectionText buffer

                if String.IsNullOrEmpty text then
                    { model with Notification = None }, []
                else
                    { model with
                        Notification = Some(Notification.info $"Copied {text.Length} char(s)") },
                    [ ClipboardCopy text ]
            | Ctrl 'x' ->
                let buffer = activeBufferState model
                let text = Buffer.selectionText buffer

                if String.IsNullOrEmpty text then
                    { model with Notification = None }, []
                else
                    updateActiveBuffer
                        Buffer.deleteSelection
                        { model with
                            Notification = Some(Notification.info $"Cut {text.Length} char(s)") },
                    [ ClipboardCopy text ]
            | Ctrl 'v' -> { model with Notification = None }, [ ClipboardPaste ]
            | _ ->
                match model.Focus with
                | Sidebar -> runSidebar key { model with Notification = None }
                | Editor -> runEditor key { model with Notification = None }
                | Prompt -> runPrompt key { model with Notification = None }
