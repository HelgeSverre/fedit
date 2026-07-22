namespace Fedit

open System
open Fedit.PickerTypes
open Fedit.PromptTypes

/// Everything the bottom-of-screen layout decides in one pass: which dock
/// surface is showing, how tall it is, and the resulting editor geometry.
/// Computed by `Dock.metrics` — the single source of truth shared by
/// `Layout.render` (painting) and `Editor` (mouse hit-testing), so the two
/// can never drift apart.
type DockMetrics =
    { PickerView: PickerView option
      Panel: DockPanel
      DockHeight: int
      StatusY: int
      DockY: int
      CommandY: int
      MainHeight: int
      SidebarWidth: int
      EditorX: int
      EditorWidth: int }

[<RequireQualifiedAccess>]
module Dock =
    let pickerKindOfPromptSession =
        function
        | PromptSessionKind.PluginsSession -> Some PickerKind.PluginPicker
        | PromptSessionKind.MacrosSession -> Some PickerKind.MacroPicker
        | PromptSessionKind.KeybindingsSession -> Some PickerKind.KeyBindingPicker
        | PromptSessionKind.MessagesSession -> Some PickerKind.MessagePicker
        | _ -> None

    let pickerPendingOfPrompt (pending: PromptPendingConfirmation option) : PickerPendingConfirmation option =
        pending
        |> Option.map (fun pending ->
            { ItemId = pending.ItemId
              ActionId = pending.ActionId
              Key = pending.Key
              Label = pending.Label })

    let pickerViewOfPromptSession model =
        pickerKindOfPromptSession model.Prompt.Session
        |> Option.map (fun kind ->
            Pickers.buildView
                model
                { Kind = kind
                  SelectedItemId = model.Prompt.SelectedItemId
                  Filter = model.Prompt.Text
                  PendingConfirmation = pickerPendingOfPrompt model.Prompt.PendingConfirmation })

    /// Which-key: while a multi-key sequence prefix is pending, the dock
    /// lists every bound continuation (remaining stroke + action name) for
    /// the active context, as (title, aligned lines). `None` when no prefix
    /// is pending or nothing continues it, so the panel clears the instant
    /// the sequence fires, times out, or is cancelled with Escape.
    let whichKeyPanel (model: Model) : (string * string list) option =
        match model.PendingPrefix with
        | Some pending when not pending.IsEmpty ->
            match Keymap.continuations (Keymap.contextOf model.Focus) pending model.Keymap with
            | [] -> None
            | rows ->
                let width = rows |> List.map (fst >> String.length) |> List.max

                let lines =
                    rows
                    |> List.map (fun (remainder, actionName) -> remainder.PadRight width + "  " + actionName)

                Some($"Keys  {Chord.renderStroke pending} …", lines)
        | _ -> None

    let panel model =
        let prompt = model.Prompt

        if prompt.Active then
            match prompt.Mode with
            | FilePicker when not prompt.Completions.IsEmpty ->
                DockCompletions("Files", prompt.Completions, prompt.SelectedCompletion)
            | FilePicker -> DockInfo("Files", [ "Type to filter recent + workspace files." ])
            | Command when not prompt.Completions.IsEmpty ->
                DockCompletions("Commands", prompt.Completions, prompt.SelectedCompletion)
            | Command ->
                let lines =
                    match prompt.Parsed with
                    | Ready(Command.Goto(line, None)) -> [ $"Press Enter to jump to line {line}." ]
                    | Ready(Command.Goto(line, Some col)) -> [ $"Press Enter to jump to line {line}, column {col}." ]
                    | Ready _ -> [ "Press Enter to run the command." ]
                    | Pending message -> [ message ]
                    | Invalid message -> [ message ]
                    | Empty -> Commands.helpLines () |> List.truncate (max 0 (model.Panels.DockHeight - 1))

                DockInfo("Commands", lines)
            | Buffers when not prompt.Completions.IsEmpty ->
                DockCompletions("Buffers", prompt.Completions, prompt.SelectedCompletion)
            | Buffers -> DockInfo("Buffers", [ "Type buffer id or name." ])
            | Search ->
                let line =
                    match prompt.SearchPreview with
                    | Some preview when preview.Matches.IsEmpty -> "no matches"
                    | Some preview -> $"match {preview.Current + 1}/{preview.Matches.Length}"
                    | None -> "Type to search the active buffer."

                DockInfo("Find", [ line ])
        else
            NoDock

    let metrics (model: Model) : DockMetrics =
        let width = max 1 model.Terminal.Width
        let height = max 1 model.Terminal.Height

        let pickerView = pickerViewOfPromptSession model

        // The picker takes over the dock when open (a prefix cannot build in
        // a picker session — keys route straight to the prompt); an in-flight
        // sequence prefix shows its which-key panel ahead of the prompt
        // panel; otherwise the prompt panel paints.
        let whichKey = whichKeyPanel model

        let activePanel =
            match pickerView, whichKey with
            | Some _, _ -> NoDock
            | None, Some(title, lines) -> DockInfo(title, lines)
            | None, None -> panel model

        // Menu-style list surfaces (the picker, the completion lists, and the
        // which-key panel) size to their content, capped at the configured
        // dock height, so a filter that leaves few rows yields a short dock
        // that grows back up to the cap. The dock is bottom-anchored, so a
        // smaller height hands the freed rows to the editor. The prose
        // info/help dock keeps the full configured height.
        let configuredMax = min model.Panels.DockHeight (max 3 (height / 3))

        let dockHeight =
            match pickerView, whichKey, activePanel with
            | Some view, _, _ -> min configuredMax (Pickers.desiredRows view)
            | None, Some(_, lines), _ -> min configuredMax (1 + lines.Length)
            | None, None, DockCompletions(_, items, _) -> min configuredMax (1 + max 1 items.Length)
            | None, None, DockInfo _ -> configuredMax
            | None, None, NoDock -> 0

        let statusY = max 0 (height - dockHeight - 2)

        let sidebarWidth =
            if model.Panels.SidebarVisible && width >= 40 then
                min model.Panels.SidebarWidth (max 18 (width / 3))
            else
                0

        let editorX = if sidebarWidth > 0 then sidebarWidth + 1 else 0

        { PickerView = pickerView
          Panel = activePanel
          DockHeight = dockHeight
          StatusY = statusY
          DockY = max 0 (height - dockHeight - 1)
          CommandY = height - 1
          MainHeight = max 1 statusY
          SidebarWidth = sidebarWidth
          EditorX = editorX
          EditorWidth = max 1 (width - editorX) }

    /// Sidebar scroll state shared by View.renderSidebar (painting) and
    /// Editor (mouse hit-testing): the visible entries plus the entry index
    /// painted on the sidebar's first row. `height` is `metrics.MainHeight`.
    let sidebarRows (model: Model) (height: int) : WorkspaceEntry list * int =
        let entries = Workspace.visibleEntries model.Workspace

        let selectedIndex =
            entries |> List.tryFindIndex _.IsSelected |> Option.defaultValue 0

        entries, max 0 (min (max 0 (entries.Length - height)) (selectedIndex - (height / 2)))
