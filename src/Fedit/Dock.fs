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
        | PromptSessionKind.LocationsSession -> Some PickerKind.LocationPicker
        | PromptSessionKind.LanguageServersSession -> Some PickerKind.LanguageServerPicker
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
            // The transient LSP info panel (hover, `:lsp log`) uses the dock
            // whenever the prompt doesn't. Dismissed on the next keypress
            // (Editor's KeyPressed chokepoint); View truncates the lines to
            // the dock height.
            match model.Lsp.Panel with
            | Some panel -> DockInfo(panel.Title, panel.Lines)
            | None -> NoDock

    /// Effective dock height cap: the configured height limited to a third
    /// of the terminal (minimum 3 rows). The prose info dock (`DockInfo`)
    /// always renders exactly this tall; list surfaces size to content
    /// below it. Shared with `Editor`'s `:lsp log` tail so the kept lines
    /// always fit the rows actually painted — this is layout arithmetic,
    /// so it lives here, never in a single consumer.
    let effectiveHeightCap (model: Model) : int =
        min model.Panels.DockHeight (max 3 (max 1 model.Terminal.Height / 3))

    let metrics (model: Model) : DockMetrics =
        let width = max 1 model.Terminal.Width
        let height = max 1 model.Terminal.Height

        let pickerView = pickerViewOfPromptSession model

        // The picker takes over the dock when open; otherwise the prompt
        // panel does.
        let activePanel =
            match pickerView with
            | Some _ -> NoDock
            | None -> panel model

        // Menu-style list surfaces (the picker and the completion lists) size
        // to their content, capped at the effective dock height, so a filter
        // that leaves few rows yields a short dock that grows back up to the
        // cap. The dock is bottom-anchored, so a smaller height hands the
        // freed rows to the editor. The prose info/help dock keeps the full
        // effective height.
        let configuredMax = effectiveHeightCap model

        let dockHeight =
            match pickerView, activePanel with
            | Some view, _ -> min configuredMax (Pickers.desiredRows view)
            | None, DockCompletions(_, items, _) -> min configuredMax (1 + max 1 items.Length)
            | None, DockInfo _ -> configuredMax
            | None, NoDock -> 0

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
