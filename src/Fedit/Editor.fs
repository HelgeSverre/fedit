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

    let activeBufferState model =
        model.Editors.Buffers[model.Editors.ActiveBufferId]

    /// Recompute the highlight state for `buffer` whenever its text
    /// might have changed. Idempotent on read-only transforms — a no-op
    /// reparse is inexpensive and avoids us having to thread an "is mutating"
    /// flag through every cursor / viewport helper. Returns the
    /// possibly-updated `HighlightStates` map.
    let private reparseHighlight (model: Model) (buffer: BufferState) : Map<int, HighlightState> =
        if not model.Config.SyntaxHighlightingEnabled then
            model.HighlightStates
        else
            match model.HighlightRegistry, Map.tryFind buffer.Id model.HighlightStates with
            | Some registry, Some existing ->
                match Highlight.parse registry existing.Language (Buffer.text buffer) (Some existing) with
                | Some next -> Map.add buffer.Id next model.HighlightStates
                | None -> Map.remove buffer.Id model.HighlightStates
            | _ -> model.HighlightStates

    let private updateActiveBuffer transform model =
        let original = activeBufferState model
        let transformed = original |> transform

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

        // EditTick bumps only on text-mutating transforms (Buffer.fs
        // owns this). Cursor / viewport / selection moves leave it
        // alone, so we skip the reparse — keeps non-edit interactions
        // free of tree-sitter cost.
        let nextStates =
            if updated.EditTick <> original.EditTick then
                reparseHighlight model updated
            else
                model.HighlightStates

        { model with
            Editors =
                { model.Editors with
                    Buffers = model.Editors.Buffers |> Map.add updated.Id updated }
            HighlightStates = nextStates }

    /// Cursor motion that drops any existing selection. Mirrors the
    /// `move` closure that used to live inside runEditor.
    let private moveCursor transform model =
        updateActiveBuffer (Buffer.clearSelection >> transform) model, []

    /// Shifted motion that extends the selection through the new cursor.
    let private extendCursor transform model =
        updateActiveBuffer (Buffer.extendSelectionToCursor >> transform) model, []

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
          Workspace = { RootPath = model.Workspace.RootPath } }

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
                    if buffer.Selection.IsSome then
                        Buffer.deleteSelection >> Buffer.insertText text
                    else
                        Buffer.insertText text

                current <- updateActiveBuffer transform current
            | Fedit.PluginApi.MoveCursor pos ->
                // Plugin API is 1-based to mirror the UI's `Ln N` indicator.
                let target =
                    { Line = max 0 (pos.Line - 1)
                      Column = max 0 (pos.Column - 1) }

                current <-
                    updateActiveBuffer
                        (fun buffer ->
                            let idx = Buffer.positionToIndex target buffer
                            Buffer.moveToOffset idx buffer)
                        current
            | Fedit.PluginApi.OpenFile path ->
                let absolutePath = resolvePath current.Workspace.RootPath path
                effects.Add(LoadFile absolutePath)
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

        current, List.ofSeq effects

    and private executeCommand command model =
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
        | Write -> runAction Save model
        | WriteAs path -> runAction (SaveAs path) model
        | Command.OpenConfig ->
            let configPath = ConfigIO.path ()

            // Materialize the config on first use so LoadFile has something
            // to read instead of returning a "failed to open" notification.
            // Synchronous write keeps the file ready before the async
            // LoadFile fires.
            if not (File.Exists configPath) then
                try
                    ConfigIO.save model.Config
                with _ ->
                    ()

            executeCommand (Open configPath) model
        | Command.Quit -> runAction Action.Quit model
        | Command.ToggleSidebar -> runAction Action.ToggleSidebar model
        | FocusTree -> runAction FocusSidebar model
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

            // Turning on: seed states for any open buffer whose
            // extension matches a supported grammar. Turning off:
            // dispose existing parsers/trees and drop the map so the
            // renderer stops overlaying.
            let nextStates =
                if newValue then
                    match model.HighlightRegistry with
                    | None -> Map.empty
                    | Some registry ->
                        // Dispose any stale entries first so we don't leak
                        // parsers across the off→on transition.
                        model.HighlightStates |> Map.iter (fun _ s -> Highlight.dispose s)

                        model.Editors.Buffers
                        |> Map.fold
                            (fun acc id buffer ->
                                match Highlight.detectLanguage buffer.FilePath with
                                | None -> acc
                                | Some lang ->
                                    match Highlight.parse registry lang (Buffer.text buffer) None with
                                    | Some s -> Map.add id s acc
                                    | None -> acc)
                            Map.empty
                else
                    model.HighlightStates |> Map.iter (fun _ s -> Highlight.dispose s)
                    Map.empty

            let updated =
                { model with
                    Config = nextConfig
                    HighlightStates = nextStates }

            let note =
                if newValue then
                    "Syntax highlighting on."
                else
                    "Syntax highlighting off."

            updated |> notify (Some(Notification.info note)), [ SaveConfig nextConfig ]

        | Plugin("list", _) ->
            // Render plugin status into the dock via notification (multiline
            // joined with newlines — View renders \n correctly in the dock).
            let lines =
                model.Plugins.Loaded
                |> Map.toList
                |> List.map (fun (_, plugin) ->
                    let status =
                        match plugin.Status with
                        | Loaded -> $"ok ({plugin.Commands.Length} cmd, {plugin.Keybindings.Length} key)"
                        | Disabled -> "disabled"
                        | Failed reason -> $"FAIL: {reason}"

                    sprintf "%-24s %s" plugin.Manifest.Name status)

            let body =
                if lines.IsEmpty then
                    "(no plugins installed — ~/.config/fedit/plugins/)"
                else
                    String.concat "\n" lines

            notify (Some(Notification.info body)) model, []

        | Plugin("install", arg) ->
            let source = Plugins.detectSource arg
            notify (Some(Notification.info $"Installing {arg}…")) model, [ InstallPluginFromSource source ]

        | Plugin("remove", name) -> notify (Some(Notification.info $"Removing {name}…")) model, [ RemovePluginDir name ]

        | Plugin("enable", _name) ->
            // MVP: enable/disable persistence deferred. ScanPlugins reloads
            // the directory; if the plugin is on disk it comes back enabled.
            notify (Some(Notification.info "Enable/disable persistence is deferred to v2; scanning.")) model,
            [ ScanPlugins ]

        | Plugin("disable", _name) ->
            notify (Some(Notification.info "Enable/disable persistence is deferred to v2; scanning.")) model,
            [ ScanPlugins ]

        | Plugin("reload", _) -> notify (Some(Notification.info "Reloading plugins…")) model, [ ScanPlugins ]

        | Plugin("validate", path) ->
            let manifestPath = Path.Combine(path, "plugin.json")

            let report =
                if not (File.Exists manifestPath) then
                    Notification.error $"No plugin.json found in {path}."
                else
                    match Plugins.tryParseManifest manifestPath with
                    | Result.Ok manifest ->
                        Notification.info
                            $"OK: {manifest.Name} {manifest.Version} (apiVersion {manifest.ApiVersion}); entryType={manifest.EntryType}"
                    | Result.Error reason -> Notification.error reason

            notify (Some report) model, []

        | Plugin(verb, _) -> notify (Some(Notification.error $"Unhandled plugin verb '{verb}'.")) model, []

        | Keybind argument ->
            let ctxName =
                function
                | Context.Global -> "global"
                | Context.Editor -> "editor"
                | Context.Sidebar -> "sidebar"
                | Context.Prompt -> "prompt"

            match argument.Trim() with
            | "" ->
                // List every effective binding (context, stroke, action).
                let lines =
                    model.Keymap
                    |> List.choose (fun b ->
                        b.Action
                        |> Option.map (fun a ->
                            sprintf "%-8s %-18s %A" (ctxName b.Context) (Chord.renderStroke b.Stroke) a))

                let body =
                    if lines.IsEmpty then
                        "(no keybindings)"
                    else
                        String.concat "\n" lines

                notify (Some(Notification.info body)) model, []
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

                    let lines =
                        [ Context.Global; Context.Editor; Context.Sidebar; Context.Prompt ]
                        |> List.map (fun ctx ->
                            let outcome =
                                match Keymap.resolve ctx stroke model.Keymap with
                                | Bound a -> sprintf "%A" a
                                | Unbound -> "(unbound)"
                                | NotBound -> "—"

                            sprintf "%-8s %s" (ctxName ctx) outcome)

                    let body = Chord.renderStroke stroke + "\n" + String.concat "\n" lines
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
        | HasSelection -> (activeBufferState model).Selection.IsSome
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

            if buffer.Selection.IsSome then
                updateActiveBuffer Buffer.deleteSelection model, []
            else
                updateActiveBuffer Buffer.backspaceWord model, []
        | DeleteWordForward ->
            let buffer = activeBufferState model

            if buffer.Selection.IsSome then
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
            | SidebarOpenFile path -> { model with Workspace = workspace }, [ LoadFile path ]
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
        // Spacebar maps to `Named Space`, not `Char ' '`; treat it as a literal
        // filter character so multi-word filters work.
        | { Mods = m; Key = Named Space } when m.IsEmpty ->
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
        let hasSelection = (activeBufferState model).Selection.IsSome

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

    let private runPrompt (chord: Chord) model =
        let prompt = model.Prompt

        match chord with
        | c when c = nk Escape -> closePrompt model, []
        | c when c = nk Left ->
            { model with
                Prompt =
                    { prompt with
                        Cursor = max 0 (prompt.Cursor - 1) } },
            []
        | c when c = nk Right ->
            { model with
                Prompt =
                    { prompt with
                        Cursor = min prompt.Text.Length (prompt.Cursor + 1) } },
            []
        | c when c = nk Home ->
            { model with
                Prompt = { prompt with Cursor = 0 } },
            []
        | c when c = nk End ->
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
        | c when c = nk Tab ->
            // Tab fills the prompt with the highlighted completion so users
            // can type `:o<Tab>` → `:open` and continue with arguments.
            // Up/Down/ShiftTab still cycle the selection.
            match prompt.Completions with
            | [] -> model, []
            | items ->
                let idx = max 0 (min prompt.SelectedCompletion (items.Length - 1))
                applyCompletion items[idx] model
        | c when c = snk Tab -> cycleCompletion -1 model, []
        | c when c = nk Up ->
            match prompt.Mode with
            | Search -> moveSearchMatch -1 model, []
            | _ -> cycleCompletion -1 model, []
        | c when c = nk Down ->
            match prompt.Mode with
            | Search -> moveSearchMatch 1 model, []
            | _ -> cycleCompletion 1 model, []
        | c when c = ank Up -> applyHistory -1 model
        | c when c = ank Down -> applyHistory 1 model
        | c when c = nk Enter ->
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

    let init rootPath size config userThemes (highlightRegistry: HighlightRegistry option) =
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
          HighlightRegistry = highlightRegistry
          HighlightStates = Map.empty
          QuitArmed = false
          ShouldQuit = false
          Keymap = Keymap.defaults
          PendingPrefix = None
          Registers = Map.empty
          Recording = None
          Replaying = false
          LastMacro = None },
        [ ScanWorkspace rootPath; ScanPlugins; LoadKeybinds ]

    let update msg model =
        match msg with
        | Resize size -> { model with Terminal = size } |> updateActiveBuffer id, []
        | MouseScrolled ticks ->
            // Ambient event, sibling of Resize — never enters the keybinding
            // dispatch. `ScrollViewport` moves the view and drags the cursor
            // only to honour scrolloff; `ScrollLine` keeps the legacy
            // wheel-moves-cursor behaviour.
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

                // Seed highlight state for the newly opened buffer if its
                // extension matches a supported grammar and the registry
                // loaded successfully. Failures stay silent — the renderer
                // simply skips the overlay when no state exists.
                let nextHighlight =
                    if not model.Config.SyntaxHighlightingEnabled then
                        model.HighlightStates
                    else
                        match Highlight.detectLanguage (Some path), model.HighlightRegistry with
                        | Some lang, Some registry ->
                            match Highlight.parse registry lang normalized None with
                            | Some state -> Map.add buffer.Id state model.HighlightStates
                            | None -> model.HighlightStates
                        | _ -> model.HighlightStates

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
                    HighlightStates = nextHighlight
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
                let pastedText = fst (normalizeNewlines pastedText)
                let buffer = activeBufferState model

                let transform =
                    if buffer.Selection.IsSome then
                        Buffer.deleteSelection >> Buffer.insertText pastedText
                    else
                        Buffer.insertText pastedText

                updateActiveBuffer (transform >> Buffer.clearSelection) model, []
            | Result.Ok _ -> model, []
            | Result.Error message -> notify (Some(Notification.warning $"Paste failed: {message}")) model, []
        | PluginsScanned(Result.Ok registry) ->
            // Conflict warnings surface as a notification; absent conflicts
            // leave any existing notification (startup hint) intact.
            let conflictNotice =
                match registry.Conflicts with
                | [] -> model.Notification
                | xs -> Some(Notification.warning (String.concat "; " xs))

            { model with
                Plugins = registry
                Notification = conflictNotice },
            []
        | PluginsScanned(Result.Error message) ->
            notify (Some(Notification.error $"Plugin scan failed: {message}")) model, []
        | PluginInstalled(name, Result.Ok()) ->
            notify (Some(Notification.info $"Installed plugin '{name}'")) model, [ ScanPlugins ]
        | PluginInstalled(_, Result.Error message) ->
            notify (Some(Notification.error $"Install failed: {message}")) model, []
        | PluginRemoved(name, Result.Ok()) ->
            notify (Some(Notification.info $"Removed plugin '{name}'")) model, [ ScanPlugins ]
        | PluginRemoved(name, Result.Error message) ->
            notify (Some(Notification.error $"Remove '{name}' failed: {message}")) model, []
        | PluginBuildFinished(name, Result.Ok()) -> notify (Some(Notification.info $"Built '{name}'")) model, []
        | PluginBuildFinished(name, Result.Error message) ->
            notify (Some(Notification.error $"Build '{name}' failed: {message}")) model, []
        | SequenceTimedOut -> { model with PendingPrefix = None }, []
        | MacroReplayStart -> { model with Replaying = true }, []
        | MacroReplayEnd -> { model with Replaying = false }, []
        | KeybindsLoaded(keymap, errors) ->
            let model = { model with Keymap = keymap }

            match errors with
            | [] -> model, []
            | _ -> notify (Some(Notification.warning (String.concat "; " errors))) model, []
        | KeyPressed chord ->
            let model =
                if chord = cc 'q' then
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

            let pending = model.PendingPrefix |> Option.map fst |> Option.defaultValue []

            let isPrefix (s: KeyStroke) =
                Keymap.isSequencePrefix ctx s model.Keymap

            // Escape always cancels an in-flight prefix (spec §6.3).
            if not (List.isEmpty pending) && chord = nk Escape then
                { model with PendingPrefix = None }, []
            else
                match Sequence.step isPrefix pending chord with
                | Sequence.Pending candidate ->
                    let deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000L

                    { model with
                        PendingPrefix = Some(candidate, deadline) },
                    []
                | stepResult ->
                    // Fire (pending was empty → single chord) or Failed (a
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
                    | [ c ] when c = cc 'q' ->
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
                            notify (Some(Notification.warning $"No binding for {Chord.renderStroke candidate}")) model,
                            []
                        | NotBound ->
                            // single-chord fallthrough by focus: plugins (editor
                            // only) → text/filter/prompt line-editing.
                            match model.Focus with
                            | Editor ->
                                match dispatchViaPlugins chord model with
                                | Some result -> result
                                | None -> runEditor chord model
                            | Sidebar -> runSidebar chord model
                            | Prompt -> runPrompt chord model
