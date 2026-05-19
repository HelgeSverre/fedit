namespace Fedit

open System
open System.IO

[<RequireQualifiedAccess>]
module Editor =
    let private emptyCommandBar =
        { Active = false
          Text = ""
          Cursor = 0
          Parsed = Empty
          Completions = []
          SelectedCompletion = 0
          History = []
          HistoryIndex = None
          PreviewTheme = None }

    let private initialPanels =
        { SidebarVisible = true
          SidebarWidth = 30
          DockHeight = 5 }

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

    let private themeFromApplyText (userThemes: Theme list) (applyText: string) =
        if applyText.StartsWith("theme ", StringComparison.OrdinalIgnoreCase) then
            Themes.tryFindIn userThemes (applyText.Substring 6)
        else
            None

    let private updatePreview model =
        if not model.CommandBar.Active then
            { model with
                CommandBar =
                    { model.CommandBar with
                        PreviewTheme = None } }
        else
            let fromCompletion =
                model.CommandBar.Completions
                |> List.tryItem model.CommandBar.SelectedCompletion
                |> Option.bind (fun item -> themeFromApplyText model.UserThemes item.ApplyText)

            let preview =
                match fromCompletion, model.CommandBar.Parsed with
                | Some _, _ -> fromCompletion
                | None, Ready(Theme name) -> Themes.tryFindIn model.UserThemes name
                | _ -> None

            { model with
                CommandBar =
                    { model.CommandBar with
                        PreviewTheme = preview } }

    let private refreshCommandBar model =
        let buffersForCompletion =
            model.Editors.Buffers
            |> Map.toList
            |> List.map (fun (id, buffer) -> id, buffer.Name, buffer.FilePath)

        let completions =
            Commands.completions
                { RootPath = model.Workspace.RootPath
                  Files = workspaceFiles model.Workspace
                  Recent = model.Recent
                  Buffers = buffersForCompletion
                  Themes = Themes.merge model.UserThemes }
                model.CommandBar.Text

        let selectedIndex =
            if completions.IsEmpty then
                0
            else
                min model.CommandBar.SelectedCompletion (completions.Length - 1)

        { model with
            CommandBar =
                { model.CommandBar with
                    Parsed = Commands.parse model.CommandBar.Text
                    Completions = completions
                    SelectedCompletion = selectedIndex } }
        |> updatePreview

    let private openCommandBar initialText model =
        { model with
            Focus = CommandBar
            CommandBar =
                { model.CommandBar with
                    Active = true
                    Text = initialText
                    Cursor = initialText.Length
                    HistoryIndex = None
                    SelectedCompletion = 0 } }
        |> refreshCommandBar

    let private closeCommandBar model =
        { model with
            Focus = Editor
            CommandBar =
                { model.CommandBar with
                    Active = false
                    Text = ""
                    Cursor = 0
                    Parsed = Empty
                    Completions = []
                    SelectedCompletion = 0
                    HistoryIndex = None
                    PreviewTheme = None } }

    let private resolvePath (rootPath: string) (path: string) =
        if Path.IsPathRooted path then
            path
        else
            Path.GetFullPath(Path.Combine(rootPath, path))

    let private insertCommandText value model =
        let cursor = max 0 (min model.CommandBar.Cursor model.CommandBar.Text.Length)
        let nextText = model.CommandBar.Text.Insert(cursor, value)

        { model with
            CommandBar =
                { model.CommandBar with
                    Text = nextText
                    Cursor = cursor + value.Length
                    SelectedCompletion = 0 } }
        |> refreshCommandBar

    let private replaceCommandText value model =
        { model with
            CommandBar =
                { model.CommandBar with
                    Text = value
                    Cursor = value.Length
                    SelectedCompletion = 0 } }
        |> refreshCommandBar

    let private deleteCommandBackward model =
        if model.CommandBar.Cursor = 0 then
            model
        else
            { model with
                CommandBar =
                    { model.CommandBar with
                        Text = model.CommandBar.Text.Remove(model.CommandBar.Cursor - 1, 1)
                        Cursor = model.CommandBar.Cursor - 1
                        SelectedCompletion = 0 } }
            |> refreshCommandBar

    let private deleteCommandForward model =
        if model.CommandBar.Cursor >= model.CommandBar.Text.Length then
            model
        else
            { model with
                CommandBar =
                    { model.CommandBar with
                        Text = model.CommandBar.Text.Remove(model.CommandBar.Cursor, 1)
                        SelectedCompletion = 0 } }
            |> refreshCommandBar

    let private saveActiveBuffer customPath model =
        let buffer = activeBufferState model

        let targetPath =
            match customPath, buffer.FilePath with
            | Some path, _ -> Some(resolvePath model.Workspace.RootPath path)
            | None, Some existing -> Some existing
            | None, None -> None

        match targetPath with
        | Some path -> model, [ SaveBuffer(buffer.Id, path, Buffer.serialize buffer) ]
        | None ->
            openCommandBar "writeas " model
            |> notify (Some(Notification.warning "Scratch buffers need a path.")),
            []

    let private pushHistory (text: string) model =
        let trimmed = text.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            model
        else
            { model with
                CommandBar =
                    { model.CommandBar with
                        History =
                            trimmed :: (model.CommandBar.History |> List.filter ((<>) trimmed))
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
        | Quit -> { model with ShouldQuit = true }, []
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
        | Help ->
            model
            |> notify (Some(Notification.info "Dock panel lists the current shortcuts and commands.")),
            []
        | Theme name ->
            match Themes.tryFindIn model.UserThemes name with
            | Some theme ->
                { model with Theme = theme }
                |> notify (Some(Notification.info $"Theme: {theme.Name}")),
                [ SaveConfig(theme.Name, model.Recent) ]
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
        | SwitchBuffer ident ->
            let buffers = model.Editors.Buffers

            let target =
                match System.Int32.TryParse ident with
                | true, id when buffers.ContainsKey id -> Some id
                | _ ->
                    buffers
                    |> Map.toList
                    |> List.tryFind (fun (_, b) -> b.Name = ident || b.FilePath = Some ident)
                    |> Option.map fst

            match target with
            | Some id ->
                { model with
                    Editors =
                        { model.Editors with
                            ActiveBufferId = id }
                    Focus = Editor }
                |> notify (Some(Notification.info $"Buffer {id}")),
                []
            | None -> model |> notify (Some(Notification.error $"Unknown buffer '{ident}'.")), []

    let private openSearch model =
        { model with
            Focus = Search
            Search =
                Some
                    { Query = ""
                      Matches = []
                      Current = 0 } }

    let private closeSearch model =
        { model with
            Focus = Editor
            Search = None }

    let private updateSearchQuery query model =
        let buffer = activeBufferState model
        let matches = Buffer.findAll query buffer

        let modelWithSearch =
            { model with
                Search =
                    Some
                        { Query = query
                          Matches = matches
                          Current = 0 } }

        match matches with
        | [] -> modelWithSearch
        | first :: _ -> updateActiveBuffer (Buffer.moveToOffset first) modelWithSearch

    let private moveSearchMatch delta model =
        match model.Search with
        | Some search when not search.Matches.IsEmpty ->
            let count = search.Matches.Length
            let nextIdx = ((search.Current + delta) % count + count) % count
            let target = search.Matches[nextIdx]
            let modelUpdated = updateActiveBuffer (Buffer.moveToOffset target) model

            { modelUpdated with
                Search = Some { search with Current = nextIdx } }
        | _ -> model

    let private runSearch key model =
        let current =
            model.Search
            |> Option.defaultValue
                { Query = ""
                  Matches = []
                  Current = 0 }

        match key with
        | Escape -> closeSearch model, []
        | Enter -> moveSearchMatch 1 model, []
        | Up -> moveSearchMatch -1 model, []
        | Down -> moveSearchMatch 1 model, []
        | Backspace when current.Query.Length > 0 ->
            let newQuery = current.Query.Substring(0, current.Query.Length - 1)
            updateSearchQuery newQuery model, []
        | Backspace -> closeSearch model, []
        | Character c ->
            let newQuery = current.Query + string c
            updateSearchQuery newQuery model, []
        | _ -> model, []

    let private runSidebar key model =
        match key with
        | Up ->
            { model with
                Workspace = Workspace.moveSelection -1 model.Workspace },
            []
        | Down ->
            { model with
                Workspace = Workspace.moveSelection 1 model.Workspace },
            []
        | PageUp ->
            { model with
                Workspace = Workspace.moveSelection -10 model.Workspace },
            []
        | PageDown ->
            { model with
                Workspace = Workspace.moveSelection 10 model.Workspace },
            []
        | Home ->
            { model with
                Workspace = Workspace.moveHome model.Workspace },
            []
        | End ->
            { model with
                Workspace = Workspace.moveEnd model.Workspace },
            []
        | Left ->
            let nextWorkspace =
                match Workspace.tryCollapseSelected model.Workspace with
                | Some collapsed -> collapsed
                | None -> Workspace.selectParent model.Workspace

            { model with Workspace = nextWorkspace }, []
        | Right ->
            { model with
                Workspace = Workspace.expandSelected model.Workspace },
            []
        | Enter ->
            let workspace, action = Workspace.activateSelected model.Workspace

            match action with
            | SidebarOpenFile path -> { model with Workspace = workspace }, [ LoadFile path ]
            | SidebarNoOp -> { model with Workspace = workspace }, []
        | Escape -> { model with Focus = Editor }, []
        | _ -> model, []

    let private runEditor key model =
        let hasSelection = (activeBufferState model).Selection.IsSome

        let editTransform editFn =
            if hasSelection then
                Buffer.deleteSelection >> editFn
            else
                editFn

        match key with
        | Character value ->
            updateActiveBuffer (editTransform (Buffer.insertText (string value)) >> Buffer.clearSelection) model, []
        | Enter -> updateActiveBuffer (editTransform Buffer.insertNewline >> Buffer.clearSelection) model, []
        | Backspace when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | Backspace -> updateActiveBuffer Buffer.backspace model, []
        | Delete when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | Delete -> updateActiveBuffer Buffer.deleteForward model, []
        | Left -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveLeft) model, []
        | Right -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveRight) model, []
        | Up -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveUp) model, []
        | Down -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveDown) model, []
        | Home -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveHome) model, []
        | End -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveEnd) model, []
        | ShiftLeft -> updateActiveBuffer (Buffer.extendSelectionToCursor >> Buffer.moveLeft) model, []
        | ShiftRight -> updateActiveBuffer (Buffer.extendSelectionToCursor >> Buffer.moveRight) model, []
        | ShiftUp -> updateActiveBuffer (Buffer.extendSelectionToCursor >> Buffer.moveUp) model, []
        | ShiftDown -> updateActiveBuffer (Buffer.extendSelectionToCursor >> Buffer.moveDown) model, []
        | ShiftHome -> updateActiveBuffer (Buffer.extendSelectionToCursor >> Buffer.moveHome) model, []
        | ShiftEnd -> updateActiveBuffer (Buffer.extendSelectionToCursor >> Buffer.moveEnd) model, []
        | PageUp ->
            updateActiveBuffer
                (Buffer.clearSelection
                 >> Buffer.movePageUp (max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)))
                model,
            []
        | PageDown ->
            updateActiveBuffer
                (Buffer.clearSelection
                 >> Buffer.movePageDown (max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)))
                model,
            []
        | Tab -> updateActiveBuffer (Buffer.clearSelection >> Buffer.indent) model, []
        | ShiftTab -> updateActiveBuffer (Buffer.clearSelection >> Buffer.unindent) model, []
        | AltLeft -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveLeftWord) model, []
        | AltRight -> updateActiveBuffer (Buffer.clearSelection >> Buffer.moveRightWord) model, []
        | CtrlBackspace when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | CtrlBackspace -> updateActiveBuffer Buffer.backspaceWord model, []
        | CtrlDelete when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
        | CtrlDelete -> updateActiveBuffer Buffer.deleteForwardWord model, []
        | _ -> model, []

    let private runCommandBar key model =
        match key with
        | Escape -> closeCommandBar model, []
        | Left ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        Cursor = max 0 (model.CommandBar.Cursor - 1) } },
            []
        | Right ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        Cursor = min model.CommandBar.Text.Length (model.CommandBar.Cursor + 1) } },
            []
        | Home ->
            { model with
                CommandBar = { model.CommandBar with Cursor = 0 } },
            []
        | End ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        Cursor = model.CommandBar.Text.Length } },
            []
        | Backspace -> deleteCommandBackward model, []
        | Delete -> deleteCommandForward model, []
        | Character value -> insertCommandText (string value) model, []
        | Tab when not model.CommandBar.Completions.IsEmpty ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        SelectedCompletion =
                            (model.CommandBar.SelectedCompletion + 1) % model.CommandBar.Completions.Length } }
            |> updatePreview,
            []
        | ShiftTab when not model.CommandBar.Completions.IsEmpty ->
            let count = model.CommandBar.Completions.Length

            { model with
                CommandBar =
                    { model.CommandBar with
                        SelectedCompletion = (model.CommandBar.SelectedCompletion + count - 1) % count } }
            |> updatePreview,
            []
        | Up when not model.CommandBar.Completions.IsEmpty ->
            let count = model.CommandBar.Completions.Length

            { model with
                CommandBar =
                    { model.CommandBar with
                        SelectedCompletion = (model.CommandBar.SelectedCompletion + count - 1) % count } }
            |> updatePreview,
            []
        | Down when not model.CommandBar.Completions.IsEmpty ->
            let count = model.CommandBar.Completions.Length

            { model with
                CommandBar =
                    { model.CommandBar with
                        SelectedCompletion = (model.CommandBar.SelectedCompletion + 1) % count } }
            |> updatePreview,
            []
        | AltUp when not model.CommandBar.History.IsEmpty ->
            let index =
                match model.CommandBar.HistoryIndex with
                | Some value -> max 0 (value - 1)
                | None -> model.CommandBar.History.Length - 1

            replaceCommandText
                model.CommandBar.History[index]
                { model with
                    CommandBar =
                        { model.CommandBar with
                            HistoryIndex = Some index } },
            []
        | AltDown when not model.CommandBar.History.IsEmpty ->
            let index =
                match model.CommandBar.HistoryIndex with
                | Some value -> min (model.CommandBar.History.Length - 1) (value + 1)
                | None -> 0

            replaceCommandText
                model.CommandBar.History[index]
                { model with
                    CommandBar =
                        { model.CommandBar with
                            HistoryIndex = Some index } },
            []
        | Enter ->
            match model.CommandBar.Parsed with
            | Ready command ->
                let remembered = pushHistory model.CommandBar.Text model
                let closed = closeCommandBar remembered
                executeCommand command closed
            | Pending _ when not model.CommandBar.Completions.IsEmpty ->
                match model.CommandBar.Completions |> List.tryItem model.CommandBar.SelectedCompletion with
                | Some item -> replaceCommandText item.ApplyText model, []
                | None -> model, []
            | Pending message -> notify (Some(Notification.warning message)) model, []
            | Invalid message -> notify (Some(Notification.error message)) model, []
            | Empty -> closeCommandBar model, []
        | _ -> model, []

    let init rootPath size theme userThemes recent =
        { Workspace = Workspace.create rootPath
          Editors = initialEditors
          CommandBar = emptyCommandBar
          Panels = initialPanels
          Focus = Editor
          Terminal = size
          Notification = Some(Notification.info "Ctrl+P commands  Ctrl+B tree  Ctrl+S save  Ctrl+Q quit")
          Theme = theme
          UserThemes = userThemes
          Recent = recent
          Search = None
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

                let recent = path :: (model.Recent |> List.filter ((<>) path)) |> List.truncate 20

                { model with
                    Editors =
                        { model.Editors with
                            Buffers = model.Editors.Buffers |> Map.add buffer.Id buffer
                            ActiveBufferId = buffer.Id
                            NextBufferId = buffer.Id + 1 }
                    Workspace = Workspace.selectPath path model.Workspace
                    Focus = Editor
                    Recent = recent
                    Notification = Some(Notification.info $"Opened {buffer.Name}") },
                [ SaveConfig(model.Theme.Name, recent) ]
            | Result.Error message -> notify (Some(Notification.error $"Failed to open {path}: {message}")) model, []
        | BufferSaved(bufferId, path, result) ->
            match result with
            | Result.Ok() ->
                { model with
                    Editors =
                        { model.Editors with
                            Buffers =
                                model.Editors.Buffers
                                |> Map.add bufferId (Buffer.markSaved path model.Editors.Buffers[bufferId]) }
                    Notification = Some(Notification.info $"Saved {Path.GetFileName path}") },
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
                    []
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
            | Ctrl 'p' -> openCommandBar "" { model with Notification = None }, []
            | Ctrl 'f' -> openSearch { model with Notification = None }, []
            | Ctrl 'b' ->
                { model with
                    Focus = Sidebar
                    Notification = None },
                []
            | Ctrl 'e' ->
                { model with
                    Focus = Editor
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
                | CommandBar -> runCommandBar key { model with Notification = None }
                | Search -> runSearch key { model with Notification = None }
