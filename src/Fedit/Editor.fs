namespace Fedit

open System
open System.IO
open Fedit.PickerTypes
open Fedit.PromptTypes

[<RequireQualifiedAccess>]
module Editor =
    let private sessionForMode =
        function
        | FilePicker -> PromptSessionKind.FileOpenSession
        | PromptMode.Command -> PromptSessionKind.CommandSession
        | Search -> PromptSessionKind.SearchSession
        | Buffers -> PromptSessionKind.BufferSwitchSession

    let private isPromptListSession =
        function
        | PromptSessionKind.PluginsSession
        | PromptSessionKind.MacrosSession
        | PromptSessionKind.KeybindingsSession -> true
        | _ -> false

    let private emptyPrompt =
        { Active = false
          Session = PromptSessionKind.FileOpenSession
          Text = ""
          Cursor = 0
          Mode = FilePicker
          Parsed = Empty
          Completions = []
          SelectedCompletion = 0
          SelectedItemId = None
          History = []
          HistoryIndex = None
          PendingConfirmation = None
          SearchPreview = None }

    let private initialPanels (config: Config) =
        { SidebarVisible = true
          SidebarWidth = config.SidebarWidth
          DockHeight = config.DockHeight }

    let private initialEditors =
        let scratch = Buffer.createEmpty 1

        { Buffers = Map.ofList [ 1, scratch ]
          ActiveBufferId = 1
          NextBufferId = 2
          PreviewBufferId = None }

    /// Collapse CRLF and lone-CR sequences to LF and report the original
    /// dominant ending. The document invariant is LF-only; the returned
    /// newline feeds the buffer's save-time preference. Used by every path
    /// where external text enters a buffer (file open, paste, plugin text).
    let private normalizeNewlines (text: string) =
        let newline = if text.Contains "\r\n" then "\r\n" else "\n"
        let normalized = text.Replace("\r\n", "\n").Replace("\r", "\n")
        normalized, newline

    let private notify notification model =
        { model with
            Notification = notification }

    let private scanPluginsEffect model =
        ScanPlugins model.Config.DisabledPlugins

    let activeBufferState model =
        model.Editors.Buffers[model.Editors.ActiveBufferId]

    /// Pure layout geometry: returns (editorX, editorWidth, mainHeight) for
    /// the current model. Shared with `Layout.render` via `Dock.metrics`, so
    /// mouse hit-testing can never drift from the painted layout.
    let editorLayout (model: Model) =
        let metrics = Dock.metrics model
        metrics.EditorX, metrics.EditorWidth, metrics.MainHeight

    /// Map a mouse event's screen coordinates to a buffer position.
    /// Returns `None` if the click is outside the editor content area.
    let private mouseToBufferPosition (event: MouseEvent) (model: Model) : Position option =
        let editorX, editorWidth, mainHeight = editorLayout model
        let buffer = activeBufferState model
        let gutterWidth = Buffer.gutterWidth buffer
        let contentX = editorX + gutterWidth
        let contentWidth = max 1 (editorWidth - gutterWidth)

        let mouseX = event.Position.Column
        let mouseY = event.Position.Line

        if
            mouseY < 0
            || mouseY >= mainHeight
            || mouseX < contentX
            || mouseX >= contentX + contentWidth
        then
            None
        else
            let lineIndex = buffer.ViewportTop + mouseY
            let columnIndex = buffer.ViewportLeft + (mouseX - contentX)

            if lineIndex >= Buffer.lineCount buffer then
                None
            else
                let lineLength = (Buffer.line lineIndex buffer).Length
                let clampedCol = max 0 (min columnIndex lineLength)

                Some
                    { Line = lineIndex
                      Column = clampedCol }

    /// Map a mouse event to the sidebar entry under it. `None` when the
    /// sidebar is hidden, the event is outside it, or the row is past the
    /// last entry. Row→entry mapping shares Dock.sidebarRows with painting.
    let private sidebarEntryAt (event: MouseEvent) (model: Model) : WorkspaceEntry option =
        let metrics = Dock.metrics model

        if
            metrics.SidebarWidth = 0
            || event.Position.Column >= metrics.SidebarWidth
            || event.Position.Line < 0
            || event.Position.Line >= metrics.MainHeight
        then
            None
        else
            let entries, startIndex = Dock.sidebarRows model metrics.MainHeight
            entries |> List.tryItem (startIndex + event.Position.Line)

    /// Highlight language for a buffer. Pure — extension match on the path,
    /// shebang sniff on the first line. Drives `ParseHighlight` scheduling;
    /// the effect interpreter owns the actual tree-sitter work.
    let private languageFor (buffer: BufferState) =
        Highlight.detectLanguage buffer.FilePath (Buffer.line 0 buffer)

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

        let updated =
            transformed
            |> Buffer.ensureViewport model.Config.ScrollOff viewportHeight viewportWidth

        { model with
            Editors =
                { model.Editors with
                    Buffers = model.Editors.Buffers |> Map.add updated.Id updated } }

    /// Cursor motion that drops any existing selection. Mirrors the
    /// `move` closure that used to live inside runEditor.
    let private moveCursor transform model =
        updateActiveBuffer (Buffer.clearSelection >> transform) model, []

    /// Shifted motion that extends the selection through the new cursor.
    let private extendCursor transform model =
        updateActiveBuffer (Buffer.extendWith transform) model, []

    let private filePickerCompletions model query =
        let limit = model.Config.CompletionLimit
        let recent = model.Config.Recent
        let files = model.Workspace.Files
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
            |> List.map (fun (_, relative) ->
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

    /// Synthesize the plugin-command tuples for `Commands.allSpecs`. Each
    /// tuple is `(name, summary, source-plugin-name)`.
    let private pluginCmdTuples (model: Model) =
        model.Plugins.Commands
        |> Map.toList
        |> List.map (fun (name, binding) -> name, binding.Spec.Summary, binding.Source)

    let private commandCompletions model query =
        let buffersForCompletion =
            model.Editors.Buffers
            |> Map.toList
            |> List.map (fun (id, buffer) -> id, buffer.Name, buffer.FilePath)

        Commands.completionsWith
            (Commands.allSpecs (pluginCmdTuples model))
            { RootPath = model.Workspace.RootPath
              Files = model.Workspace.Files
              Recent = model.Config.Recent
              Buffers = buffersForCompletion
              Themes = Themes.merge model.UserThemes
              CompletionLimit = model.Config.CompletionLimit }
            query

    let private refreshPrompt model : Model * Effect list =
        let prompt = model.Prompt

        if isPromptListSession prompt.Session then
            model, []
        else
            let mode = Prompt.modeOf prompt.Text
            let argument = Prompt.argumentOf prompt.Text

            let isNumericGoto =
                let trimmed = argument.TrimStart()
                trimmed.Length > 0 && Char.IsDigit trimmed[0]

            let allCommandSpecs = Commands.allSpecs (pluginCmdTuples model)

            let completions, parsed =
                match mode with
                | FilePicker -> filePickerCompletions model prompt.Text, Empty
                | Command when isNumericGoto -> [], Commands.parseGoto argument
                | Command -> commandCompletions model argument, Commands.parseWith allCommandSpecs argument
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
                            Session = sessionForMode mode
                            Mode = mode
                            Parsed = parsed
                            Completions = completions
                            SelectedCompletion = selectedIndex
                            SelectedItemId = None
                            PendingConfirmation = None
                            SearchPreview = nextSearchPreview } }

            let effects =
                match mode with
                | Search ->
                    let query = Prompt.argumentOf nextModel.Prompt.Text

                    if query.Length = 0 then
                        []
                    else
                        let buffer = activeBufferState nextModel
                        [ RunSearch(buffer.Id, query, buffer.Document) ]
                | _ -> []

            nextModel, effects

    let private openPrompt (initialText: string) model =
        { model with
            Focus = Prompt
            Prompt =
                { model.Prompt with
                    Active = true
                    Session = sessionForMode (Prompt.modeOf initialText)
                    Text = initialText
                    Cursor = initialText.Length
                    HistoryIndex = None
                    SelectedCompletion = 0
                    SelectedItemId = None
                    PendingConfirmation = None
                    SearchPreview = None } }
        |> refreshPrompt

    let private openPromptSession session model =
        let selected =
            Dock.pickerKindOfPromptSession session
            |> Option.bind (Pickers.firstItemId model)

        { model with
            Focus = Prompt
            Notification = None
            Prompt =
                { model.Prompt with
                    Active = true
                    Session = session
                    Text = ""
                    Cursor = 0
                    Mode = FilePicker
                    Parsed = Empty
                    Completions = []
                    SelectedCompletion = 0
                    SelectedItemId = selected
                    HistoryIndex = None
                    PendingConfirmation = None
                    SearchPreview = None } },
        []

    let private closePrompt model =
        { model with
            Focus = Editor
            Prompt =
                { model.Prompt with
                    Active = false
                    Session = PromptSessionKind.FileOpenSession
                    Text = ""
                    Cursor = 0
                    Mode = FilePicker
                    Parsed = Empty
                    Completions = []
                    SelectedCompletion = 0
                    SelectedItemId = None
                    HistoryIndex = None
                    PendingConfirmation = None
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
            // Backspace at the start is a no-op; closing the prompt is
            // Esc's job alone so users can't accidentally dismiss their
            // session by holding backspace past the mode prefix.
            model, []
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

    /// Jump directly to the buffer at the given 1-based index (sorted by ID,
    /// matching `switchBuffer`'s ordering). Out-of-range presses are a
    /// silent no-op — pressing Ctrl+5 with only 3 buffers open shouldn't
    /// surprise the user with a notification.
    let private jumpToBuffer index model =
        let ids = model.Editors.Buffers |> Map.keys |> Seq.toList |> List.sort

        match List.tryItem (index - 1) ids with
        | Some id ->
            { model with
                Editors =
                    { model.Editors with
                        ActiveBufferId = id } }
            |> notify (Some(Notification.info $"Buffer {index}/{ids.Length}"))
        | None -> model

    /// Sidebar selection for a just-opened file: with `autoReveal` on,
    /// expand the ancestor chain so the selection is actually visible;
    /// off keeps today's select-only behaviour.
    let private selectOrReveal (path: string) (model: Model) workspace =
        if model.Config.AutoReveal then
            Workspace.revealPath path workspace
        else
            Workspace.selectPath path workspace

    /// Activate an already-open buffer for `path`, promoting it out of the
    /// preview slot (an explicit open of the previewed file makes it
    /// permanent). `None` when no buffer holds the path.
    let private tryActivateExisting (absolutePath: string) (model: Model) : Model option =
        model.Editors.Buffers
        |> Map.toList
        |> List.tryFind (fun (_, buffer) -> buffer.FilePath = Some absolutePath)
        |> Option.map (fun (bufferId, _) ->
            { model with
                Editors =
                    { model.Editors with
                        ActiveBufferId = bufferId
                        PreviewBufferId =
                            if model.Editors.PreviewBufferId = Some bufferId then
                                None
                            else
                                model.Editors.PreviewBufferId }
                Workspace = selectOrReveal absolutePath model model.Workspace
                Focus = Editor })

    /// Open `path` into the preview slot. Already-open normal buffers are
    /// activated instead (VSCode behavior); re-previewing the file already
    /// in the slot is a no-op activation. Focus is preserved — Space peeks
    /// from the sidebar.
    let private openPreview (path: string) (model: Model) : Model * Effect list =
        let previewSlot =
            model.Editors.PreviewBufferId
            |> Option.bind (fun id -> model.Editors.Buffers |> Map.tryFind id |> Option.map (fun buffer -> id, buffer))

        match previewSlot with
        | Some(previewId, buffer) when buffer.FilePath = Some path ->
            { model with
                Editors =
                    { model.Editors with
                        ActiveBufferId = previewId } },
            []
        | _ ->
            match tryActivateExisting path model with
            | Some activated -> { activated with Focus = model.Focus }, []
            | None -> model, [ LoadFile(path, OpenPreview) ]

    /// Build a read-only snapshot of the world for a plugin command. The
    /// plugin sees text, cursor, file path, all open buffers, and the
    /// workspace root — never any mutable handle into the host's model.
    let private toPluginContext (model: Model) : Fedit.PluginApi.PluginContext =
        let toView (id: int) (buffer: BufferState) : Fedit.PluginApi.BufferView =
            let toPluginPos (pos: Position) : Fedit.PluginApi.CursorPosition =
                { Line = pos.Line + 1 // surface 1-based
                  Column = pos.Column + 1 }

            let selection =
                Buffer.selectionRange buffer
                |> Option.map (fun (startIdx, endIdx) ->
                    toPluginPos (Buffer.indexToPosition startIdx buffer),
                    toPluginPos (Buffer.indexToPosition endIdx buffer))

            { Id = id
              Name = buffer.Name
              FilePath = buffer.FilePath
              Text = Buffer.text buffer
              Cursor = toPluginPos buffer.Cursor
              Selection = selection }

        let active = model.Editors.ActiveBufferId
        let activeBuffer = model.Editors.Buffers[active]

        { ActiveBuffer = toView active activeBuffer
          AllBuffers = model.Editors.Buffers |> Map.toList |> List.map (fun (id, b) -> toView id b)
          Workspace =
            { RootPath = model.Workspace.RootPath
              SelectedPath = model.Workspace.SelectedPath
              Files = model.Workspace.Files } }

    /// Translate the plugin API's 1-based CursorPosition to fedit's
    /// 0-based Position. Negative inputs clamp to 0 here;
    /// `Buffer.positionToIndex` clamps the rest to the document.
    let private toHostPosition (pos: Fedit.PluginApi.CursorPosition) : Position =
        { Line = max 0 (pos.Line - 1)
          Column = max 0 (pos.Column - 1) }

    /// Translate a plugin's `PluginAction list` return into core model
    /// updates + effects. Each action is applied in declaration order;
    /// `RunCommand` recursively dispatches via `executeCommand`.
    let rec private applyPluginActions
        (actions: Fedit.PluginApi.PluginAction list)
        (model: Model)
        : Model * Effect list =
        let mutable current = model
        let effects = ResizeArray<Effect>()

        for action in actions do
            match action with
            | Fedit.PluginApi.Notify(severity, message) ->
                let notif =
                    match severity with
                    | Fedit.PluginApi.Info -> Notification.info message
                    | Fedit.PluginApi.Warning -> Notification.warning message
                    | Fedit.PluginApi.Error -> Notification.error message

                current <- notify (Some notif) current
            | Fedit.PluginApi.InsertText text ->
                let text = fst (normalizeNewlines text)
                current <- updateActiveBuffer (Buffer.insertText text) current
            | Fedit.PluginApi.ReplaceSelection text ->
                // No bespoke `replaceSelection` in Buffer; compose the two
                // primitives so undo collapses them naturally.
                let text = fst (normalizeNewlines text)
                let buffer = activeBufferState current

                let transform =
                    if Buffer.hasSelection buffer then
                        Buffer.deleteSelection >> Buffer.insertText text
                    else
                        Buffer.insertText text

                current <- updateActiveBuffer transform current
            | Fedit.PluginApi.MoveCursor pos ->
                // Plugin API is 1-based to mirror the UI's `Ln N` indicator.
                current <-
                    updateActiveBuffer
                        (fun buffer ->
                            let idx = Buffer.positionToIndex (toHostPosition pos) buffer
                            Buffer.moveToOffset idx buffer)
                        current
            | Fedit.PluginApi.SelectRange(anchorPos, cursorPos) ->
                // Same 1-based coords as MoveCursor. The anchor pins one end;
                // the caret lands on `cursor` (the live end), so the result
                // matches a shift+motion selection. `positionToIndex` clamps,
                // so out-of-range plugin coords are safe.
                current <-
                    updateActiveBuffer
                        (fun buffer ->
                            let anchorIdx = Buffer.positionToIndex (toHostPosition anchorPos) buffer
                            let cursorIdx = Buffer.positionToIndex (toHostPosition cursorPos) buffer

                            Buffer.selectRange anchorIdx cursorIdx buffer)
                        current
            | Fedit.PluginApi.OpenFile path ->
                let absolutePath = resolvePath current.Workspace.RootPath path
                effects.Add(LoadFile(absolutePath, OpenPermanent))
            | Fedit.PluginApi.SaveActiveBuffer ->
                let nextModel, fx = saveActiveBuffer None current
                current <- nextModel
                effects.AddRange fx
            | Fedit.PluginApi.RunCommand name ->
                match Commands.parse name with
                | Ready cmd ->
                    let nextModel, fx = executeCommand cmd current
                    current <- nextModel
                    effects.AddRange fx
                | _ -> current <- notify (Some(Notification.error $"Plugin RunCommand: invalid '{name}'")) current
            | Fedit.PluginApi.SetClipboard text -> effects.Add(ClipboardCopy text)
            | Fedit.PluginApi.OpenFilePreview path ->
                let absolutePath = resolvePath current.Workspace.RootPath path
                let nextModel, fx = openPreview absolutePath current
                current <- nextModel
                effects.AddRange fx
            | Fedit.PluginApi.RevealPath path ->
                // Reveal without stealing focus: the plugin ran while the
                // user was editing, and the next keystroke should still go
                // to the buffer. Paths the index doesn't know are a full
                // no-op (the sidebar doesn't even pop open).
                let absolutePath = resolvePath current.Workspace.RootPath path

                if current.Workspace.ByPath.ContainsKey absolutePath then
                    current <-
                        { current with
                            Panels =
                                { current.Panels with
                                    SidebarVisible = true }
                            Workspace = Workspace.revealPath absolutePath (Workspace.clearSearch current.Workspace) }
            | Fedit.PluginApi.ReplaceRange(fromPos, toPos, text) ->
                // One undo entry: Buffer.replaceRange composes delete +
                // insert through a single finalizeEdit. Ends normalize so
                // from > to_ still works; positionToIndex clamps.
                let text = fst (normalizeNewlines text)

                current <-
                    updateActiveBuffer
                        (fun buffer ->
                            let fromIdx = Buffer.positionToIndex (toHostPosition fromPos) buffer
                            let toIdx = Buffer.positionToIndex (toHostPosition toPos) buffer
                            let startIdx = min fromIdx toIdx

                            Buffer.replaceRange startIdx (abs (toIdx - fromIdx)) text buffer
                            |> Buffer.clearSelection)
                        current
            | Fedit.PluginApi.ClearSelection -> current <- updateActiveBuffer Buffer.clearSelection current
            | Fedit.PluginApi.DeleteSelection -> current <- updateActiveBuffer Buffer.deleteSelection current
            | Fedit.PluginApi.SwitchBuffer id ->
                // Same path as `:buffer <id>`: validates the id, notifies
                // "Unknown buffer" on a miss, changes nothing else.
                let nextModel, fx = executeCommand (Command.SwitchBuffer(ById id)) current
                current <- nextModel
                effects.AddRange fx
            | Fedit.PluginApi.NewBuffer(name, text) ->
                let displayName =
                    if String.IsNullOrWhiteSpace name then
                        "plugin"
                    else
                        name.Trim()

                let normalized, newline = normalizeNewlines text

                let buffer =
                    Buffer.fromText current.Editors.NextBufferId None displayName normalized newline

                // Mirrors the FileOpened OpenPermanent construction minus
                // the path/recent bookkeeping. PreviewBufferId is untouched;
                // the host stays silent — the plugin owns messaging.
                current <-
                    { current with
                        Editors =
                            { current.Editors with
                                Buffers = current.Editors.Buffers |> Map.add buffer.Id buffer
                                ActiveBufferId = buffer.Id
                                NextBufferId = buffer.Id + 1 } }

        current, List.ofSeq effects

    and private trySelectedItem model pickerState =
        let pickerView = Pickers.buildView model pickerState
        pickerView.Items |> List.tryItem pickerView.SelectedIndex

    and private trySelectedRegister model pickerState =
        trySelectedItem model pickerState
        |> Option.bind (fun item -> if item.Id.Length = 1 then Some item.Id[0] else None)

    and private runPickerAction (actionChord: Chord) model pickerState =
        match trySelectedItem model pickerState with
        | None -> model, Some pickerState, []
        | Some selectedItem ->
            let matchingAction =
                selectedItem.Actions
                |> List.tryFind (fun a -> a.Key = actionChord && a.State = PickerActionState.Enabled)

            match matchingAction with
            | None -> model, Some pickerState, []
            | Some action ->
                let alreadyArmed =
                    match pickerState.PendingConfirmation with
                    | Some pending ->
                        pending.ItemId = Some selectedItem.Id
                        && pending.ActionId = action.Id
                        && pending.Key = action.Key
                    | None -> false

                match action.Confirmation with
                | Some confirmation when not alreadyArmed ->
                    let pending: PickerPendingConfirmation =
                        { ItemId = Some selectedItem.Id
                          ActionId = action.Id
                          Key = action.Key
                          Label = confirmation.Label }

                    { model with
                        Notification =
                            Some(Notification.warning $"Press {Chord.render action.Key} again to {confirmation.Label}.") },
                    Some
                        { pickerState with
                            PendingConfirmation = Some pending },
                    []
                | _ -> executePickerAction action.Id model pickerState

    and private executePickerAction actionId model pickerState =
        match pickerState.Kind, actionId with
        | PluginPicker, PickerActionId.PluginEnable ->
            match trySelectedItem model pickerState with
            | Some item -> togglePluginInPicker false item.Id model pickerState
            | None -> model, Some pickerState, []
        | PluginPicker, PickerActionId.PluginDisable ->
            match trySelectedItem model pickerState with
            | Some item -> togglePluginInPicker true item.Id model pickerState
            | None -> model, Some pickerState, []
        | PluginPicker, PickerActionId.PluginReloadAll ->
            { model with
                Notification = Some(Notification.info "Reloading plugins...") },
            Some
                { pickerState with
                    PendingConfirmation = None },
            [ scanPluginsEffect model ]
        | PluginPicker, PickerActionId.PluginUninstall ->
            match trySelectedItem model pickerState with
            | Some item ->
                { model with
                    Notification = Some(Notification.info $"Removing {item.Id}...") },
                Some
                    { pickerState with
                        PendingConfirmation = None },
                [ RemovePluginDir item.Id ]
            | None -> model, Some pickerState, []
        | MacroPicker, PickerActionId.MacroReplay ->
            match trySelectedRegister model pickerState with
            | Some register ->
                let next, effects = runAction (ReplayMacro(register, 1)) model
                next, None, effects
            | None -> model, Some pickerState, []
        | MacroPicker, PickerActionId.MacroRecord ->
            match trySelectedRegister model pickerState with
            | Some register ->
                let next, effects = runAction (RecordMacro register) model
                next, None, effects
            | None -> model, Some pickerState, []
        | MacroPicker, PickerActionId.MacroMarkLast ->
            match trySelectedRegister model pickerState with
            | Some register ->
                { model with
                    LastMacro = Some register
                    Notification = Some(Notification.info $"Last macro: @{register}") },
                Some
                    { pickerState with
                        PendingConfirmation = None },
                []
            | None -> model, Some pickerState, []
        | MacroPicker, PickerActionId.MacroClear ->
            match trySelectedRegister model pickerState with
            | Some register -> clearPickerMacro register model pickerState
            | None -> model, Some pickerState, []
        | _ -> model, Some pickerState, []

    /// Toggle a plugin's enabled state in config, persist, and rescan. Used by
    /// both the `:plugin enable/disable` commands and the picker action.
    and private setPluginDisabled disabled pluginName model =
        let nextDisabled =
            if disabled then
                model.Config.DisabledPlugins |> Set.add pluginName
            else
                model.Config.DisabledPlugins |> Set.remove pluginName

        let nextConfig =
            { model.Config with
                DisabledPlugins = nextDisabled }

        { model with
            Config = nextConfig
            Notification =
                Some(
                    Notification.info (
                        if disabled then
                            $"Disabled '{pluginName}'."
                        else
                            $"Enabled '{pluginName}'."
                    )
                ) },
        [ SaveConfig nextConfig; ScanPlugins nextConfig.DisabledPlugins ]

    and private togglePluginInPicker disabled pluginName model pickerState =
        let model, effects = setPluginDisabled disabled pluginName model

        let nextPicker =
            pickerState
            |> Pickers.clampSelection model
            |> fun s -> { s with PendingConfirmation = None }

        model, Some nextPicker, effects

    and private clearPickerMacro register model pickerState =
        let nextRegisters = model.Registers |> Map.remove register

        let nextRecording =
            match model.Recording with
            | Some active when active = register -> None
            | other -> other

        let nextLast =
            match model.LastMacro with
            | Some last when last = register -> None
            | other -> other

        let nextPicker =
            { pickerState with
                PendingConfirmation = None }
            |> Pickers.clampSelection
                { model with
                    Registers = nextRegisters
                    Recording = nextRecording
                    LastMacro = nextLast }

        { model with
            Registers = nextRegisters
            Recording = nextRecording
            LastMacro = nextLast
            Notification = Some(Notification.info $"Cleared macro @{register}") },
        Some nextPicker,
        []

    and private executeCommand command model =
        match command with
        | Open path ->
            let absolutePath = resolvePath model.Workspace.RootPath path

            match tryActivateExisting absolutePath model with
            | Some activated ->
                activated
                |> notify (Some(Notification.info $"Activated {Path.GetFileName absolutePath}")),
                []
            | None -> { model with Focus = Editor }, [ LoadFile(absolutePath, OpenPermanent) ]
        | Write -> runAction Save model
        | WriteAs path -> runAction (SaveAs path) model
        | Command.OpenConfig ->
            // Materialize the config on first use so LoadFile has something
            // to read instead of returning a "failed to open" notification.
            // The write is I/O, so it runs as an effect; `ConfigFileReady`
            // posts back and its Ok handler opens the file.
            model, [ EnsureConfigFile model.Config ]
        | Command.Quit -> runAction Action.Quit model
        | Command.ToggleSidebar -> runAction Action.ToggleSidebar model
        | FocusTree -> runAction FocusSidebar model
        | Command.Reveal -> runAction RevealInSidebar model
        // :editor focuses without clearing the sidebar search — a deliberate
        // divergence from the Ctrl+E chord (which clears it). Preserved.
        | Command.FocusEditor -> { model with Focus = Editor }, []
        | Command.ReloadWorkspace -> runAction Action.ReloadWorkspace model
        | Command.NextBuffer -> runAction Action.NextBuffer model
        | PreviousBuffer -> runAction PrevBuffer model
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

            match tryActivateExisting absolute model with
            | Some activated ->
                activated
                |> notify (Some(Notification.info $"Activated {Path.GetFileName absolute}")),
                []
            | None -> { model with Focus = Editor }, [ LoadFile(absolute, OpenPermanent) ]
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

        | Syntax verb ->
            let newValue =
                match verb with
                | "on" -> true
                | "off" -> false
                | "toggle" -> not model.Config.SyntaxHighlightingEnabled
                | _ -> model.Config.SyntaxHighlightingEnabled

            let nextConfig =
                { model.Config with
                    SyntaxHighlightingEnabled = newValue }

            // Turning on: request a parse for every open buffer with a
            // known language at or under `Highlight.maxParseChars`; spans
            // land asynchronously. Oversized buffers are skipped and
            // counted so the notification stays honest. Turning off: drop
            // the span overlays — the interpreter holds no per-buffer
            // native state, so there is nothing to dispose here.
            let parseEffects, skippedForSize =
                if newValue then
                    let parseable, oversized =
                        model.Editors.Buffers
                        |> Map.toList
                        |> List.choose (fun (id, buffer) ->
                            languageFor buffer |> Option.map (fun lang -> id, buffer, lang))
                        |> List.partition (fun (_, buffer, _) ->
                            PieceTable.length buffer.Document <= Highlight.maxParseChars)

                    parseable
                    |> List.map (fun (id, buffer, lang) -> ParseHighlight(id, lang, buffer.Document, buffer.EditTick)),
                    List.length oversized
                else
                    [], 0

            let updated =
                { model with
                    Config = nextConfig
                    HighlightStates = if newValue then model.HighlightStates else Map.empty }

            let note =
                if not newValue then
                    "Syntax highlighting off."
                elif skippedForSize = 0 then
                    "Syntax highlighting on."
                else
                    $"Syntax highlighting on ({skippedForSize} buffer(s) too large to parse)."

            updated |> notify (Some(Notification.info note)), SaveConfig nextConfig :: parseEffects

        | Plugin("list", _)
        | Command.Plugins -> openPromptSession PromptSessionKind.PluginsSession model
        | Command.Macros -> openPromptSession PromptSessionKind.MacrosSession model

        | Plugin("install", arg) ->
            let source = Plugins.detectSource arg
            notify (Some(Notification.info $"Installing {arg}…")) model, [ InstallPluginFromSource source ]

        | Plugin("remove", name) -> notify (Some(Notification.info $"Removing {name}…")) model, [ RemovePluginDir name ]

        | Plugin("enable", name) -> setPluginDisabled false name model

        | Plugin("disable", name) -> setPluginDisabled true name model

        | Plugin("reload", _) ->
            notify (Some(Notification.info "Reloading plugins...")) model, [ scanPluginsEffect model ]

        | Plugin("validate", path) ->
            // Manifest existence + parse are I/O — run as an effect; the
            // report comes back as `PluginValidated`.
            model, [ ValidatePlugin path ]

        | Plugin(verb, _) -> notify (Some(Notification.error $"Unhandled plugin verb '{verb}'.")) model, []

        | Keybind argument ->
            let ctxName =
                function
                | Context.Global -> "global"
                | Context.Editor -> "editor"
                | Context.Sidebar -> "sidebar"
                | Context.Prompt -> "prompt"

            match argument.Trim() with
            | "" -> openPromptSession PromptSessionKind.KeybindingsSession model
            | "reload" -> notify (Some(Notification.info "Reloading keybinds…")) model, [ LoadKeybinds ]
            | strokeText ->
                let chords =
                    strokeText.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList
                    |> List.map Chord.parse

                if chords |> List.exists Option.isNone then
                    notify (Some(Notification.error $"Unparseable stroke '{strokeText}'.")) model, []
                else
                    let stroke = chords |> List.choose id

                    // One line per context, joined — the status bar is single-row.
                    let parts =
                        [ Context.Global; Context.Editor; Context.Sidebar; Context.Prompt ]
                        |> List.map (fun ctx ->
                            let outcome =
                                match Keymap.resolve ctx stroke model.Keymap with
                                | Bound a -> (sprintf "%A" a).Replace("\n", " ")
                                | Unbound -> "(unbound)"
                                | NotBound -> "—"

                            sprintf "%s=%s" (ctxName ctx) outcome)

                    let body = Chord.renderStroke stroke + "  " + String.concat "  " parts
                    notify (Some(Notification.info body)) model, []

        | PluginInvoke(source, name, _argument) ->
            match model.Plugins.Commands.TryFind name with
            | Some binding when binding.Source = source ->
                try
                    let ctx = toPluginContext model
                    let actions = binding.Spec.Run ctx
                    applyPluginActions actions model
                with ex ->
                    notify (Some(Notification.error $"Plugin '{source}' threw: {ex.Message}")) model, []
            | Some _ ->
                notify (Some(Notification.error $"Plugin '{name}' is not provided by '{source}' anymore.")) model, []
            | None -> notify (Some(Notification.error $"Plugin command '{name}' missing.")) model, []

    and evalCond (cond: Cond) (model: Model) : bool =
        match cond with
        | SidebarVisible -> model.Panels.SidebarVisible
        | SidebarFocused -> model.Focus = Sidebar
        | EditorFocused -> model.Focus = Editor
        | PromptActive -> model.Prompt.Active
        | HasSelection -> Buffer.hasSelection (activeBufferState model)
        | BufferDirty -> (activeBufferState model).Dirty
        | Not c -> not (evalCond c model)
        | AllOf cs -> cs |> List.forall (fun c -> evalCond c model)
        | AnyOf cs -> cs |> List.exists (fun c -> evalCond c model)

    /// The single dispatcher. Each arm is the transition lifted verbatim
    /// from the handler that used to inline it. Public so tests and (later
    /// phases) the keymap resolver can call it.
    and runAction (action: Fedit.Action) (model: Model) : Model * Effect list =
        match action with
        // composition & control flow
        | NoOp -> model, []
        | Chain actions ->
            actions
            |> List.fold
                (fun (m, fx) a ->
                    let m', fx' = runAction a m
                    m', fx @ fx')
                (model, [])
        | When(cond, thenDo, elseDo) -> runAction (if evalCond cond model then thenDo else elseDo) model

        // motion / selection (verbatim from runEditor)
        | MoveLeft -> moveCursor Buffer.moveLeft model
        | MoveRight -> moveCursor Buffer.moveRight model
        | MoveUp -> moveCursor Buffer.moveUp model
        | MoveDown -> moveCursor Buffer.moveDown model
        | MoveHome -> moveCursor Buffer.moveHome model
        | MoveEnd -> moveCursor Buffer.moveEnd model
        | MoveWordLeft -> moveCursor Buffer.moveLeftWord model
        | MoveWordRight -> moveCursor (Buffer.moveRightWord model.Config.WordMotion) model
        | MovePageUp ->
            let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
            let jump = max 1 (viewportHeight - model.Config.PageOverlap)
            moveCursor (Buffer.movePageUp jump) model
        | MovePageDown ->
            let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
            let jump = max 1 (viewportHeight - model.Config.PageOverlap)
            moveCursor (Buffer.movePageDown jump) model
        | ExtendLeft -> extendCursor Buffer.moveLeft model
        | ExtendRight -> extendCursor Buffer.moveRight model
        | ExtendUp -> extendCursor Buffer.moveUp model
        | ExtendDown -> extendCursor Buffer.moveDown model
        | ExtendHome -> extendCursor Buffer.moveHome model
        | ExtendEnd -> extendCursor Buffer.moveEnd model
        | SelectAll -> updateActiveBuffer Buffer.selectAll model, []

        // editing (verbatim from runEditor + global handler)
        | Indent -> moveCursor (Buffer.indent model.Config.TabWidth) model
        | Unindent -> moveCursor (Buffer.unindent model.Config.TabWidth) model
        | DeleteWordBack ->
            let buffer = activeBufferState model

            if Buffer.hasSelection buffer then
                updateActiveBuffer Buffer.deleteSelection model, []
            else
                updateActiveBuffer Buffer.backspaceWord model, []
        | DeleteWordForward ->
            let buffer = activeBufferState model

            if Buffer.hasSelection buffer then
                updateActiveBuffer Buffer.deleteSelection model, []
            else
                updateActiveBuffer (Buffer.deleteForwardWord model.Config.WordMotion) model, []
        | Undo -> updateActiveBuffer Buffer.undo model, []
        | Redo -> updateActiveBuffer Buffer.redo model, []
        | Copy ->
            let buffer = activeBufferState model
            let text = Buffer.selectionText buffer

            if String.IsNullOrEmpty text then
                model, []
            else
                { model with
                    Notification = Some(Notification.info $"Copied {text.Length} char(s)") },
                [ ClipboardCopy text ]
        | Cut ->
            let buffer = activeBufferState model
            let text = Buffer.selectionText buffer

            if String.IsNullOrEmpty text then
                model, []
            else
                updateActiveBuffer
                    Buffer.deleteSelection
                    { model with
                        Notification = Some(Notification.info $"Cut {text.Length} char(s)") },
                [ ClipboardCopy text ]
        | Paste -> model, [ ClipboardPaste ]

        // command-group bodies (verbatim from global handler / executeCommand)
        | Save -> saveActiveBuffer None model
        | SaveAs path -> saveActiveBuffer (Some path) model
        | Quit -> { model with ShouldQuit = true }, [ SaveConfig model.Config ]
        | OpenPalette ->
            openPrompt
                ":"
                { model with
                    Workspace = Workspace.clearSearch model.Workspace }
        | OpenFilePicker ->
            openPrompt
                ""
                { model with
                    Workspace = Workspace.clearSearch model.Workspace }
        | OpenSearch ->
            openPrompt
                "/"
                { model with
                    Workspace = Workspace.clearSearch model.Workspace }
        | NextBuffer -> switchBuffer 1 model, []
        | PrevBuffer -> switchBuffer -1 model, []
        | JumpToBuffer n -> jumpToBuffer n model, []
        | ReloadWorkspace -> model, [ ScanWorkspace model.Workspace.RootPath ]

        // panel / focus primitives
        | RevealSidebar ->
            { model with
                Panels =
                    { model.Panels with
                        SidebarVisible = true } },
            []
        | HideSidebar ->
            { model with
                Panels =
                    { model.Panels with
                        SidebarVisible = false }
                Workspace = Workspace.clearSearch model.Workspace },
            []
        | ToggleSidebar ->
            { model with
                Panels =
                    { model.Panels with
                        SidebarVisible = not model.Panels.SidebarVisible } },
            []
        | FocusSidebar -> { model with Focus = Sidebar }, []
        | FocusEditor ->
            { model with
                Focus = Editor
                Workspace = Workspace.clearSearch model.Workspace },
            []
        | RevealInSidebar ->
            match (activeBufferState model).FilePath with
            | None -> notify (Some(Notification.info "Scratch buffer has no file to reveal.")) model, []
            | Some path ->
                { model with
                    Panels =
                        { model.Panels with
                            SidebarVisible = true }
                    Workspace = Workspace.revealPath path (Workspace.clearSearch model.Workspace)
                    Focus = Sidebar },
                []

        // sidebar navigation (verbatim from runSidebar)
        | SidebarUp ->
            { model with
                Workspace = Workspace.moveSelection -1 (Workspace.clearSearch model.Workspace) },
            []
        | SidebarDown ->
            { model with
                Workspace = Workspace.moveSelection 1 (Workspace.clearSearch model.Workspace) },
            []
        | SidebarPageUp ->
            { model with
                Workspace = Workspace.moveSelection -model.Config.TreePageJump (Workspace.clearSearch model.Workspace) },
            []
        | SidebarPageDown ->
            { model with
                Workspace = Workspace.moveSelection model.Config.TreePageJump (Workspace.clearSearch model.Workspace) },
            []
        | SidebarTop ->
            { model with
                Workspace = Workspace.moveHome (Workspace.clearSearch model.Workspace) },
            []
        | SidebarBottom ->
            { model with
                Workspace = Workspace.moveEnd (Workspace.clearSearch model.Workspace) },
            []
        | SidebarCollapse ->
            let cleared = Workspace.clearSearch model.Workspace

            let nextWorkspace =
                match Workspace.tryCollapseSelected cleared with
                | Some collapsed -> collapsed
                | None -> Workspace.selectParent cleared

            { model with Workspace = nextWorkspace }, []
        | SidebarExpand ->
            { model with
                Workspace = Workspace.expandSelected (Workspace.clearSearch model.Workspace) },
            []
        | SidebarActivate ->
            let workspace, sidebarAction =
                Workspace.activateSelected (Workspace.clearSearch model.Workspace)

            match sidebarAction with
            | SidebarOpenFile path ->
                // Enter is an explicit open: an already-open buffer is
                // activated (promoting a previewed file out of the preview
                // slot) instead of silently loading a duplicate.
                let withWorkspace = { model with Workspace = workspace }

                match tryActivateExisting path withWorkspace with
                | Some activated -> activated, []
                | None -> withWorkspace, [ LoadFile(path, OpenPermanent) ]
            | SidebarNoOp -> { model with Workspace = workspace }, []

        // verbs whose canonical body stays in executeCommand (prompt-only).
        // RHS cases are qualified Command.* because Action/Command share names.
        | SetTheme name -> executeCommand (Command.Theme name) model
        | Goto(line, col) -> executeCommand (Command.Goto(line, col)) model
        | OpenConfig -> executeCommand Command.OpenConfig model
        | RunPlugin(source, name, arg) -> executeCommand (Command.PluginInvoke(source, name, arg)) model

        | ReloadKeybinds -> model, [ LoadKeybinds ]

        // ── macros (keybindings phase 4) ──
        | RecordMacro register ->
            match model.Recording with
            | Some active when active = register ->
                // Toggle off: the just-recorded register becomes the repeat target.
                { model with
                    Recording = None
                    LastMacro = Some register
                    Notification = Some(Notification.info $"Recorded macro @{register}") },
                []
            | _ ->
                // Start (or switch) recording: clear the register, begin capture.
                { model with
                    Recording = Some register
                    Registers = model.Registers |> Map.add register []
                    Notification = Some(Notification.info $"Recording @{register}…") },
                []
        | ReplayMacro(register, count) ->
            // A replayed chord must not spawn another replay (runaway guard); a
            // register cannot replay itself while it is being recorded.
            let selfRef =
                match model.Recording with
                | Some active -> active = register
                | None -> false

            if model.Replaying || selfRef then
                model, []
            else
                match model.Registers |> Map.tryFind register with
                | Some chords when not (List.isEmpty chords) && count > 0 ->
                    { model with LastMacro = Some register }, [ ReplayKeys(chords, count) ]
                | _ ->
                    { model with
                        Notification = Some(Notification.warning $"No macro in @{register}") },
                    []
        | RepeatLastMacro ->
            match model.LastMacro with
            | Some register -> runAction (ReplayMacro(register, 1)) model
            | None ->
                { model with
                    Notification = Some(Notification.info "No macro to repeat") },
                []

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

    // Chord literals for the hardcoded default bindings. (A later phase
    // replaces these with a data-driven keymap; until then the dispatch is
    // hardcoded and matched via `when c = …` guards.)
    let private cc c : Chord =
        { Mods = Set.ofList [ Ctrl ]
          Key = Key.Char c } // ctrl+<char>

    let private nk n : Chord = { Mods = Set.empty; Key = Named n } // bare named key

    let private snk n : Chord =
        { Mods = Set.ofList [ Shift ]
          Key = Named n } // shift+<named>

    let private ank n : Chord =
        { Mods = Set.ofList [ Alt ]
          Key = Named n } // alt+<named>

    // Hoisted chord literals for the prompt dispatchers — their guards run
    // on every keypress, and rebuilding a chord (with its modifier set) per
    // comparison is avoidable garbage.
    let private kEscape = nk Escape
    let private kLeft = nk Left
    let private kRight = nk Right
    let private kHome = nk Home
    let private kEnd = nk End
    let private kTab = nk Tab
    let private kShiftTab = snk Tab
    let private kUp = nk Up
    let private kDown = nk Down
    let private kAltUp = ank Up
    let private kAltDown = ank Down
    let private kEnter = nk Enter
    let private kPageUp = nk PageUp
    let private kPageDown = nk PageDown
    let private kCtrlQ = cc 'q'

    let private contextOf =
        function
        | FocusTarget.Editor -> Context.Editor
        | FocusTarget.Sidebar -> Context.Sidebar
        | FocusTarget.Prompt -> Context.Prompt

    /// Sidebar fallthrough core: the incremental filter. All navigation is
    /// keymap-driven (Context.Sidebar) and resolves before this is reached.
    let private runSidebar (chord: Chord) model =
        match chord with
        | { Mods = m; Key = Char c } when m.IsEmpty ->
            { model with
                Workspace = Workspace.appendSearch c model.Workspace },
            []
        // Space previews the selected file (a peek — focus stays in the
        // sidebar) when no type-ahead query is in flight. With a query
        // active it stays a literal filter character so filenames with
        // spaces ("my file.fs") remain reachable; a leading space never
        // matched any entry anyway, so gating on the empty buffer loses
        // nothing.
        | { Mods = m; Key = Named Space } when m.IsEmpty ->
            if model.Workspace.SearchBuffer.Length = 0 then
                let workspace, sidebarAction = Workspace.activateSelected model.Workspace

                match sidebarAction with
                | SidebarOpenFile path -> openPreview path { model with Workspace = workspace }
                | SidebarNoOp -> { model with Workspace = workspace }, []
            else
                { model with
                    Workspace = Workspace.appendSearch ' ' model.Workspace },
                []
        | { Mods = m; Key = Named Backspace } when m.IsEmpty && model.Workspace.SearchBuffer.Length > 0 ->
            { model with
                Workspace = Workspace.backspaceSearch model.Workspace },
            []
        | _ -> model, []

    /// Editor fallthrough core: literal text insertion. Motions/edits are
    /// keymap-driven (Context.Editor / Context.Global) and resolve before this;
    /// plugin chords are tried between the keymap and this core (spec §6.7.4).
    let private runEditor (chord: Chord) model =
        let hasSelection = Buffer.hasSelection (activeBufferState model)

        let editTransform editFn =
            if hasSelection then
                Buffer.deleteSelection >> editFn
            else
                editFn

        match chord with
        | { Mods = m; Key = Char value } when m.IsEmpty ->
            updateActiveBuffer (editTransform (Buffer.insertText (string value)) >> Buffer.clearSelection) model, []
        // Spacebar maps to `Named Space`, not `Char ' '`; insert it as literal text.
        | { Mods = m; Key = Named Space } when m.IsEmpty ->
            updateActiveBuffer (editTransform (Buffer.insertText " ") >> Buffer.clearSelection) model, []
        | { Mods = m; Key = Named Enter } when m.IsEmpty ->
            updateActiveBuffer (editTransform Buffer.insertNewline >> Buffer.clearSelection) model, []
        | { Mods = m; Key = Named Backspace } when m.IsEmpty && hasSelection ->
            updateActiveBuffer Buffer.deleteSelection model, []
        | { Mods = m; Key = Named Backspace } when m.IsEmpty -> updateActiveBuffer Buffer.backspace model, []
        | { Mods = m; Key = Named Delete } when m.IsEmpty && hasSelection ->
            updateActiveBuffer Buffer.deleteSelection model, []
        | { Mods = m; Key = Named Delete } when m.IsEmpty -> updateActiveBuffer Buffer.deleteForward model, []
        | _ -> model, []

    /// Plugin keybinding lookup — consulted only after the keymap returns
    /// NotBound, in editor focus (spec §6.7.4). Single chords only:
    /// `Chord.toKeyChord` is `None` for sequences/Super/Named, so a pending
    /// multi-chord prefix never reaches plugins. `None` here = no plugin bound.
    let private dispatchViaPlugins (chord: Chord) model =
        match Chord.toKeyChord chord with
        | None -> None
        | Some kc ->
            model.Plugins.Keybindings
            |> List.tryFind (fun (c, _) -> c = kc)
            |> Option.map snd
            |> Option.map (fun commandName ->
                match model.Plugins.Commands.TryFind commandName with
                | Some binding -> executeCommand (PluginInvoke(binding.Source, commandName, "")) model
                | None ->
                    match Commands.parse commandName with
                    | Ready cmd -> executeCommand cmd model
                    | _ ->
                        notify
                            (Some(Notification.error $"Plugin binding refers to unknown command '{commandName}'."))
                            model,
                        [])

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

    let private promptPendingOfPicker (pending: PickerPendingConfirmation option) : PromptPendingConfirmation option =
        pending
        |> Option.map (fun pending ->
            { ItemId = pending.ItemId
              ActionId = pending.ActionId
              Key = pending.Key
              Label = pending.Label })

    let private pickerStateOfPrompt kind (prompt: PromptState) : PickerState =
        { Kind = kind
          SelectedItemId = prompt.SelectedItemId
          Filter = prompt.Text
          PendingConfirmation = Dock.pickerPendingOfPrompt prompt.PendingConfirmation }

    let private promptFromPickerState session (prompt: PromptState) (pickerState: PickerState) =
        { prompt with
            Active = true
            Session = session
            Text = pickerState.Filter
            Cursor = pickerState.Filter.Length
            Mode = FilePicker
            Parsed = Empty
            Completions = []
            SelectedCompletion = 0
            SelectedItemId = pickerState.SelectedItemId
            HistoryIndex = None
            PendingConfirmation = promptPendingOfPicker pickerState.PendingConfirmation
            SearchPreview = None }

    let private applyPromptPickerState session pickerState model =
        { model with
            Focus = Prompt
            Prompt = promptFromPickerState session model.Prompt pickerState },
        []

    let private runPromptSessionAction actionChord model kind pickerState =
        let session = model.Prompt.Session
        let next, nextPicker, effects = runPickerAction actionChord model pickerState

        match nextPicker with
        | Some nextPicker ->
            { next with
                Focus = Prompt
                Prompt = promptFromPickerState session next.Prompt nextPicker },
            effects
        | None -> closePrompt next, effects

    let private runPromptListSession chord model =
        let prompt = model.Prompt

        match Dock.pickerKindOfPromptSession prompt.Session with
        | None -> model, []
        | Some kind ->
            let pickerState = pickerStateOfPrompt kind prompt

            let actionKeys =
                match kind with
                | PickerKind.PluginPicker -> Set.ofList [ 'e'; 'd'; 'r'; 'u' ]
                | PickerKind.MacroPicker -> Set.ofList [ 'r'; 'm'; 'c' ]
                | PickerKind.KeyBindingPicker -> Set.empty

            let hasActions =
                match kind with
                | PickerKind.KeyBindingPicker -> false
                | _ -> true

            match chord with
            | c when c = kEscape -> closePrompt model, []
            | c when c = kUp ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.moveSelection -1 model) model
            | c when c = kDown ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.moveSelection 1 model) model
            | c when c = kPageUp ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.moveSelection -10 model) model
            | c when c = kPageDown ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.moveSelection 10 model) model
            | c when c = kHome ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.setSelection 0 model) model
            | c when c = kEnd ->
                let pickerView = Pickers.buildView model pickerState
                let last = pickerView.Items.Length - 1
                applyPromptPickerState prompt.Session (pickerState |> Pickers.setSelection last model) model
            | c when c = kEnter ->
                if hasActions then
                    runPromptSessionAction kEnter model kind pickerState
                else
                    closePrompt model, []
            | { Mods = m; Key = Named Backspace } when m.IsEmpty ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.backspaceFilter model) model
            | { Mods = m; Key = Named Space } when m.IsEmpty ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.appendFilter " " model) model
            | { Mods = m; Key = Char value } when m.IsEmpty && actionKeys.Contains(Char.ToLowerInvariant value) ->
                let actionKey = Char.ToLowerInvariant value

                if hasActions then
                    runPromptSessionAction (Chord.ofChar actionKey) model kind pickerState
                else
                    model, []
            | { Mods = m; Key = Char value } when m.IsEmpty ->
                applyPromptPickerState prompt.Session (pickerState |> Pickers.appendFilter (string value) model) model
            | _ -> model, []

    let private runPrompt (chord: Chord) model =
        let prompt = model.Prompt

        if isPromptListSession prompt.Session then
            runPromptListSession chord model
        else
            match chord with
            | c when c = kEscape -> closePrompt model, []
            | c when c = kLeft ->
                { model with
                    Prompt =
                        { prompt with
                            Cursor = max 0 (prompt.Cursor - 1) } },
                []
            | c when c = kRight ->
                { model with
                    Prompt =
                        { prompt with
                            Cursor = min prompt.Text.Length (prompt.Cursor + 1) } },
                []
            | c when c = kHome ->
                { model with
                    Prompt = { prompt with Cursor = 0 } },
                []
            | c when c = kEnd ->
                { model with
                    Prompt =
                        { prompt with
                            Cursor = prompt.Text.Length } },
                []
            | { Mods = m; Key = Named Backspace } when m.IsEmpty -> deletePromptBackward model
            | { Mods = m; Key = Named Delete } when m.IsEmpty -> deletePromptForward model
            | { Mods = m; Key = Char value } when m.IsEmpty -> insertPromptText (string value) model
            // Spacebar maps to `Named Space`, not `Char ' '`; insert it as text so
            // command arguments (`:theme green`) can be typed.
            | { Mods = m; Key = Named Space } when m.IsEmpty -> insertPromptText " " model
            | c when c = kTab ->
                // Tab fills the prompt with the highlighted completion so users
                // can type `:o<Tab>` -> `:open` and continue with arguments.
                // Up/Down/ShiftTab still cycle the selection.
                match prompt.Completions with
                | [] -> model, []
                | items ->
                    let idx = max 0 (min prompt.SelectedCompletion (items.Length - 1))
                    applyCompletion items[idx] model
            | c when c = kShiftTab -> cycleCompletion -1 model, []
            | c when c = kUp ->
                match prompt.Mode with
                | Search -> moveSearchMatch -1 model, []
                | _ -> cycleCompletion -1 model, []
            | c when c = kDown ->
                match prompt.Mode with
                | Search -> moveSearchMatch 1 model, []
                | _ -> cycleCompletion 1 model, []
            | c when c = kAltUp -> applyHistory -1 model
            | c when c = kAltDown -> applyHistory 1 model
            | c when c = kEnter ->
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
                    | Pending message ->
                        match prompt.Completions |> List.tryItem prompt.SelectedCompletion with
                        | Some item when (prefixOf prompt.Mode + item.ApplyText) <> prompt.Text ->
                            applyCompletion item model
                        | _ -> notify (Some(Notification.warning message)) model, []
                    | Invalid message -> notify (Some(Notification.error message)) model, []
                    | Empty -> closePrompt model, []
            | _ -> model, []

    let initWithInitialFile rootPath initialFile size config userThemes =
        let startupEffects =
            [ ScanWorkspace rootPath; ScanPlugins config.DisabledPlugins; LoadKeybinds ]
            @ (initialFile
               |> Option.map (fun path -> LoadFile(path, OpenPermanent))
               |> Option.toList)

        { Workspace = Workspace.create rootPath
          Editors = initialEditors
          Prompt = emptyPrompt
          Panels = initialPanels config
          Focus = Editor
          Terminal = size
          Notification = Some(Notification.info "Ctrl+P prompt  Ctrl+B tree  Ctrl+S save  Ctrl+Q quit")
          Config = config
          UserThemes = userThemes
          Plugins = PluginRegistry.empty
          HighlightStates = Map.empty
          QuitArmed = false
          ShouldQuit = false
          Keymap = Keymap.defaults
          PendingPrefix = None
          Registers = Map.empty
          Recording = None
          Replaying = false
          LastMacro = None
          MouseDrag = None },
        startupEffects

    let init rootPath size config userThemes =
        initWithInitialFile rootPath None size config userThemes

    /// Insert externally-sourced text into the active buffer as ONE
    /// `insertText` call — one undo entry — replacing any selection and
    /// normalizing newlines to the LF-only document invariant. Shared by
    /// `ClipboardPasted` (Ctrl+V effect result) and `PastedText`
    /// (terminal bracketed paste).
    let private insertPastedText (text: string) model =
        let text = fst (normalizeNewlines text)
        let buffer = activeBufferState model

        let transform =
            if Buffer.hasSelection buffer then
                Buffer.deleteSelection >> Buffer.insertText text
            else
                Buffer.insertText text

        updateActiveBuffer (transform >> Buffer.clearSelection) model, []

    let private updateCore msg model =
        match msg with
        | Resize size -> { model with Terminal = size } |> updateActiveBuffer id, []
        | MouseScrolled(ticks, position) ->
            // Ambient event, sibling of Resize — never enters the keybinding
            // dispatch. The position routes the scroll to the surface under
            // the pointer: over the sidebar the wheel moves the tree
            // selection (no focus change); over the editor `ScrollViewport`
            // moves the view and drags the cursor only to honour scrolloff,
            // while `ScrollLine` keeps the legacy wheel-moves-cursor
            // behaviour.
            let metrics = Dock.metrics model

            let overSidebar =
                metrics.SidebarWidth > 0
                && position.Column < metrics.SidebarWidth
                && position.Line < metrics.MainHeight

            if overSidebar then
                { model with
                    Workspace =
                        Workspace.moveSelection
                            (ticks * model.Config.MouseScrollLines)
                            (Workspace.clearSearch model.Workspace) },
                []
            else
                let delta = ticks * model.Config.MouseScrollLines

                match model.Config.ScrollMode with
                | ScrollViewport ->
                    let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)

                    updateActiveBuffer (Buffer.scrollViewport model.Config.ScrollOff viewportHeight delta) model, []
                | ScrollLine ->
                    let moveFn =
                        if ticks < 0 then
                            Buffer.movePageUp (abs delta)
                        else
                            Buffer.movePageDown (abs delta)

                    updateActiveBuffer (Buffer.clearSelection >> moveFn) model, []
        | WorkspaceLoaded result ->
            match result with
            | Result.Ok(tree, skipped) ->
                let suffix = if skipped > 0 then $" ({skipped} skipped)" else ""

                { model with
                    Workspace = Workspace.setTree tree model.Workspace
                    Notification = Some(Notification.info $"Indexed {tree.Name}{suffix}") },
                []
            | Result.Error message -> notify (Some(Notification.error message)) model, []
        | FileOpened(path, intent, result) ->
            match result with
            | Result.Ok contents ->
                let normalized, newline = normalizeNewlines contents
                let displayName = Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

                let recent =
                    path :: (model.Config.Recent |> List.filter ((<>) path)) |> List.truncate 20

                let nextConfig = { model.Config with Recent = recent }

                // A buffer already holding the path is activated, never
                // duplicated — the load may have raced another open route.
                // A preview intent keeps the caller's focus (Space peeks
                // from the sidebar); a permanent open focuses the editor.
                match tryActivateExisting path model with
                | Some activated ->
                    let activated =
                        if intent = OpenPreview then
                            { activated with Focus = model.Focus }
                        else
                            activated

                    { activated with Config = nextConfig }, []
                | None ->
                    match intent with
                    | OpenPermanent ->
                        let buffer =
                            Buffer.fromText model.Editors.NextBufferId (Some path) displayName normalized newline

                        // Highlight seeding happens in the `update` wrapper: the new
                        // buffer's EditTick diff schedules a ParseHighlight effect.
                        // Recent is persisted at quit, not per file-open: avoids save
                        // churn under the FS watcher and rapid open sequences. Phase
                        // 14.2.
                        { model with
                            Editors =
                                { model.Editors with
                                    Buffers = model.Editors.Buffers |> Map.add buffer.Id buffer
                                    ActiveBufferId = buffer.Id
                                    NextBufferId = buffer.Id + 1 }
                            Workspace = selectOrReveal path model model.Workspace
                            Focus = Editor
                            Config = nextConfig
                            Notification = Some(Notification.info $"Opened {buffer.Name}") },
                        []
                    | OpenPreview ->
                        let reusableSlot =
                            model.Editors.PreviewBufferId
                            |> Option.bind (fun id -> model.Editors.Buffers |> Map.tryFind id)
                            |> Option.filter (fun old -> not old.Dirty)

                        match reusableSlot with
                        | Some old ->
                            // Reuse the slot: same id, fresh content. The
                            // EditTick bump makes the highlight chokepoint
                            // reschedule a parse for the new text; SavedTick
                            // follows so Dirty stays derivable as
                            // `EditTick <> SavedTick`.
                            let buffer =
                                { Buffer.fromText old.Id (Some path) displayName normalized newline with
                                    EditTick = old.EditTick + 1
                                    SavedTick = old.EditTick + 1 }

                            { model with
                                Editors =
                                    { model.Editors with
                                        Buffers = model.Editors.Buffers |> Map.add buffer.Id buffer
                                        ActiveBufferId = buffer.Id }
                                HighlightStates = Map.remove old.Id model.HighlightStates
                                Workspace = selectOrReveal path model model.Workspace
                                Config = nextConfig
                                Notification = Some(Notification.info $"Previewing {displayName}") },
                            []
                        | None ->
                            let buffer =
                                Buffer.fromText model.Editors.NextBufferId (Some path) displayName normalized newline

                            { model with
                                Editors =
                                    { model.Editors with
                                        Buffers = model.Editors.Buffers |> Map.add buffer.Id buffer
                                        ActiveBufferId = buffer.Id
                                        NextBufferId = buffer.Id + 1
                                        PreviewBufferId = Some buffer.Id }
                                Workspace = selectOrReveal path model model.Workspace
                                Config = nextConfig
                                Notification = Some(Notification.info $"Previewing {displayName}") },
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

                    // Saving the keybinds file through fedit reloads it (the
                    // implicit counterpart to `:keybind reload`).
                    let reloadFx =
                        try
                            if
                                String.Equals(
                                    Path.GetFullPath path,
                                    Path.GetFullPath(KeymapIO.path ()),
                                    StringComparison.Ordinal
                                )
                            then
                                [ LoadKeybinds ]
                            else
                                []
                        with _ ->
                            []

                    { model with
                        Editors =
                            { model.Editors with
                                Buffers = model.Editors.Buffers |> Map.add bufferId updated }
                        Notification = Some(Notification.info note) },
                    reloadFx
            | Result.Error message -> notify (Some(Notification.error $"Failed to save {path}: {message}")) model, []
        | ConfigSaved result ->
            match result with
            | Result.Ok() -> model, []
            | Result.Error message ->
                notify (Some(Notification.warning $"Theme set, but config save failed: {message}")) model, []
        | ConfigFileReady result ->
            match result with
            | Result.Ok path -> executeCommand (Open path) model
            | Result.Error message ->
                notify (Some(Notification.warning $"Could not create config file: {message}")) model, []
        | ClipboardCopied result ->
            match result with
            | Result.Ok() -> model, []
            | Result.Error message -> notify (Some(Notification.warning $"Clipboard copy failed: {message}")) model, []
        | WorkspaceChangedExternally -> model, [ ScanWorkspace model.Workspace.RootPath ]
        | HighlightParsed(bufferId, editTick, spans) ->
            // Accept only results for the buffer's current revision; a stale
            // tick means a newer ParseHighlight is already in flight. Also
            // dropped when highlighting was toggled off mid-parse.
            match Map.tryFind bufferId model.Editors.Buffers with
            | Some buffer when buffer.EditTick = editTick && model.Config.SyntaxHighlightingEnabled ->
                { model with
                    HighlightStates = Map.add bufferId spans model.HighlightStates },
                []
            | _ -> model, []
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
            | Result.Ok pastedText when pastedText.Length > 0 -> insertPastedText pastedText model
            | Result.Ok _ -> model, []
            | Result.Error message -> notify (Some(Notification.warning $"Paste failed: {message}")) model, []
        | PastedText text when text.Length > 0 ->
            match model.Focus with
            | Editor -> insertPastedText text model
            | Prompt when isPromptListSession model.Prompt.Session ->
                // Picker/list sessions filter by single keys; pasting into
                // them has no sensible meaning. Ignore.
                model, []
            | Prompt ->
                // The prompt is a single-line input: insert only the first
                // line so a multi-line paste can never act as Enter and
                // execute commands statement by statement.
                let normalized = fst (normalizeNewlines text)

                let firstLine =
                    match normalized.IndexOf '\n' with
                    | -1 -> normalized
                    | newlineIdx -> normalized.Substring(0, newlineIdx)

                if firstLine.Length = 0 then
                    model, []
                else
                    insertPromptText firstLine model
            | Sidebar -> model, []
        | PastedText _ -> model, []
        | PluginsScanned(Result.Ok registry) ->
            // Conflict warnings surface as a notification; absent conflicts
            // leave any existing notification (startup hint) intact.
            let conflictNotice =
                match registry.Conflicts with
                | [] -> model.Notification
                | xs -> Some(Notification.warning (String.concat "; " xs))

            let nextModel =
                { model with
                    Plugins = registry
                    Notification = conflictNotice }

            let nextModel =
                if
                    nextModel.Prompt.Active
                    && nextModel.Prompt.Session = PromptSessionKind.PluginsSession
                then
                    let clamped =
                        nextModel.Prompt
                        |> pickerStateOfPrompt PickerKind.PluginPicker
                        |> Pickers.clampSelection nextModel

                    { nextModel with
                        Prompt = promptFromPickerState PromptSessionKind.PluginsSession nextModel.Prompt clamped }
                else
                    nextModel

            nextModel, []
        | PluginsScanned(Result.Error message) ->
            notify (Some(Notification.error $"Plugin scan failed: {message}")) model, []
        | PluginInstalled(name, Result.Ok()) ->
            notify (Some(Notification.info $"Installed plugin '{name}'")) model, [ scanPluginsEffect model ]
        | PluginInstalled(_, Result.Error message) ->
            notify (Some(Notification.error $"Install failed: {message}")) model, []
        | PluginRemoved(name, Result.Ok()) ->
            let nextConfig =
                { model.Config with
                    DisabledPlugins = model.Config.DisabledPlugins |> Set.remove name }

            let nextModel =
                { model with Config = nextConfig }
                |> notify (Some(Notification.info $"Removed plugin '{name}'"))

            nextModel, [ SaveConfig nextConfig; ScanPlugins nextConfig.DisabledPlugins ]
        | PluginRemoved(name, Result.Error message) ->
            notify (Some(Notification.error $"Remove '{name}' failed: {message}")) model, []
        | PluginBuildFinished(name, Result.Ok()) -> notify (Some(Notification.info $"Built '{name}'")) model, []
        | PluginBuildFinished(name, Result.Error message) ->
            notify (Some(Notification.error $"Build '{name}' failed: {message}")) model, []
        | PluginValidated(Result.Ok report) -> notify (Some(Notification.info report)) model, []
        | PluginValidated(Result.Error report) -> notify (Some(Notification.error report)) model, []
        | SequenceTimedOut -> { model with PendingPrefix = None }, []
        | MacroReplayStart -> { model with Replaying = true }, []
        | MacroReplayEnd -> { model with Replaying = false }, []
        | KeybindsLoaded(keymap, errors) ->
            let model = { model with Keymap = keymap }

            match errors with
            | [] -> model, []
            | _ -> notify (Some(Notification.warning (String.concat "; " errors))) model, []
        | MousePressed event ->
            match event.Button with
            | LeftButton ->
                // Sidebar first: a click on a row selects it; a click on the
                // already-selected row activates it (dir → toggle expand,
                // file → preview peek — focus stays in the sidebar, matching
                // the Space behavior).
                match sidebarEntryAt event model with
                | Some entry when Some entry.Path <> model.Workspace.SelectedPath ->
                    { model with
                        Workspace = Workspace.selectPath entry.Path (Workspace.clearSearch model.Workspace)
                        Focus = Sidebar },
                    []
                | Some _ ->
                    let workspace, action =
                        Workspace.activateSelected (Workspace.clearSearch model.Workspace)

                    match action with
                    | SidebarOpenFile path ->
                        openPreview
                            path
                            { model with
                                Workspace = workspace
                                Focus = Sidebar }
                    | SidebarNoOp ->
                        { model with
                            Workspace = workspace
                            Focus = Sidebar },
                        []
                | None ->
                    match mouseToBufferPosition event model with
                    | None -> model, []
                    | Some pos ->
                        let buffer = activeBufferState model
                        let idx = Buffer.positionToIndex pos buffer

                        let nextModel =
                            { model with Focus = Editor } |> updateActiveBuffer (Buffer.selectRange idx idx)

                        { nextModel with
                            MouseDrag =
                                Some
                                    { AnchorBufferId = buffer.Id
                                      AnchorPosition = pos } },
                        []
            | _ -> model, []
        | MouseDragged event ->
            match model.MouseDrag with
            | Some drag when drag.AnchorBufferId = model.Editors.ActiveBufferId ->
                match mouseToBufferPosition event model with
                | None -> model, []
                | Some pos ->
                    let buffer = activeBufferState model
                    let idx = Buffer.positionToIndex pos buffer
                    let anchorIdx = Buffer.positionToIndex drag.AnchorPosition buffer

                    let nextModel = updateActiveBuffer (Buffer.selectRange anchorIdx idx) model

                    nextModel, []
            | _ -> model, []
        | MouseReleased _event -> { model with MouseDrag = None }, []
        | FocusGained -> model, []
        | FocusLost -> model, []
        | KeyPressed chord ->
            if model.Focus = Prompt && isPromptListSession model.Prompt.Session then
                runPrompt chord model
            else
                let model =
                    if chord = kCtrlQ then
                        model
                    else
                        { model with QuitArmed = false }

                let ctx = contextOf model.Focus

                // Record-append hook: while recording (and not replaying), capture
                // each incoming chord into the active register, except the chord
                // that toggles recording off (which `runAction RecordMacro`
                // consumes). Recording captures chords, not Actions, so replay
                // re-runs live keymap resolution and reassembles any sequences.
                let model =
                    match model.Recording with
                    | Some r when not model.Replaying ->
                        let isRecordToggle =
                            match Keymap.resolve ctx [ chord ] model.Keymap with
                            | Bound(RecordMacro _) -> true
                            | _ -> false

                        if isRecordToggle then
                            model
                        else
                            let appended =
                                (model.Registers |> Map.tryFind r |> Option.defaultValue []) @ [ chord ]

                            { model with
                                Registers = model.Registers |> Map.add r appended }
                    | _ -> model

                let pending = model.PendingPrefix |> Option.defaultValue []

                let isPrefix (s: KeyStroke) =
                    Keymap.isSequencePrefix ctx s model.Keymap

                // Escape always cancels an in-flight prefix (spec §6.3).
                if not (List.isEmpty pending) && chord = kEscape then
                    { model with PendingPrefix = None }, []
                else
                    match Sequence.step isPrefix pending chord with
                    | Sequence.Pending candidate ->
                        { model with
                            PendingPrefix = Some candidate },
                        []
                    | stepResult ->
                        // Fire (pending was empty -> single chord) or Failed (a
                        // completed or dead multi-chord candidate). Both resolve the
                        // full candidate against the keymap; they differ only in the
                        // NotBound fallthrough — a mid-sequence candidate must NOT
                        // fall through to text insert.
                        let candidate, wasSequence =
                            match stepResult with
                            | Sequence.Failed c -> c, true
                            | _ -> [ chord ], false

                        let model = { model with PendingPrefix = None }

                        match candidate with
                        // Ctrl+Q two-stage quit stays bespoke — it owns QuitArmed
                        // and is deliberately absent from the keymap defaults.
                        | [ c ] when c = kCtrlQ ->
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
                        | _ ->
                            let model = { model with Notification = None }

                            match Keymap.resolve ctx candidate model.Keymap with
                            | Bound action -> runAction action model
                            | Unbound -> model, [] // explicitly freed: consume, do nothing
                            | NotBound when wasSequence ->
                                notify
                                    (Some(Notification.warning $"No binding for {Chord.renderStroke candidate}"))
                                    model,
                                []
                            | NotBound ->
                                // single-chord fallthrough by focus: plugins (editor
                                // only) -> text/filter/prompt line-editing.
                                match model.Focus with
                                | Editor ->
                                    match dispatchViaPlugins chord model with
                                    | Some result -> result
                                    | None -> runEditor chord model
                                | Sidebar -> runSidebar chord model
                                | Prompt -> runPrompt chord model

    /// Schedule highlight reparses for every buffer that changed during
    /// this dispatch. A buffer counts as changed when its EditTick moved,
    /// when it didn't exist before (file open), or when its FilePath
    /// changed — save-as/writeas can flip the detected language without an
    /// edit, so a scratch buffer saved as `x.fs` must reschedule. One
    /// chokepoint after `updateCore`, so no individual handler can forget
    /// to request a reparse. Changed buffers with no detected language or
    /// above `Highlight.maxParseChars` get their stored spans pruned
    /// instead of an effect: stale spans must never paint, and oversized
    /// documents aren't worth a multi-second parse.
    let private highlightEffects (before: Model) (after: Model) : Model * Effect list =
        if not after.Config.SyntaxHighlightingEnabled then
            after, []
        else
            let pruned, effects =
                after.Editors.Buffers
                |> Map.toList
                |> List.fold
                    (fun (model: Model, effects) (id, buffer) ->
                        let changed =
                            match Map.tryFind id before.Editors.Buffers with
                            | Some old -> old.EditTick <> buffer.EditTick || old.FilePath <> buffer.FilePath
                            | None -> true

                        if not changed then
                            model, effects
                        else
                            let language =
                                if PieceTable.length buffer.Document <= Highlight.maxParseChars then
                                    languageFor buffer
                                else
                                    None

                            match language with
                            | Some lang -> model, ParseHighlight(id, lang, buffer.Document, buffer.EditTick) :: effects
                            | None ->
                                { model with
                                    HighlightStates = Map.remove id model.HighlightStates },
                                effects)
                    (after, [])

            pruned, List.rev effects

    /// Preview invariant: the preview buffer is never Dirty. Any edit (typing,
    /// paste, plugin action, macro replay) promotes it to a normal buffer.
    /// One chokepoint after updateCore so no handler can forget.
    let private promoteDirtyPreview (model: Model) =
        let previewIsDirty =
            model.Editors.PreviewBufferId
            |> Option.bind (fun id -> model.Editors.Buffers |> Map.tryFind id)
            |> Option.exists (fun buffer -> buffer.Dirty)

        if previewIsDirty then
            { model with
                Editors =
                    { model.Editors with
                        PreviewBufferId = None } }
        else
            model

    let update msg model =
        let next, effects = updateCore msg model
        let next = promoteDirtyPreview next
        let next, highlightFx = highlightEffects model next
        next, effects @ highlightFx
