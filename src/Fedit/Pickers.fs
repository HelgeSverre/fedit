namespace Fedit

open Fedit.PickerTypes

open System

[<RequireQualifiedAccess>]
module Pickers =
    /// Map from PickerKind to its layout.
    let layoutForKind =
        function
        | PluginPicker -> ListWithInspector
        | MacroPicker -> ListWithInspector
        | KeyBindingPicker -> SearchResults

    /// Map from PickerKind to its title.
    let titleForKind =
        function
        | PluginPicker -> "Plugins"
        | MacroPicker -> "Macros"
        | KeyBindingPicker -> "Keybindings"

    /// Default empty text for each picker kind.
    let defaultEmptyText =
        function
        | PluginPicker -> "No plugins found."
        | MacroPicker -> "No macros found."
        | KeyBindingPicker -> "No keybindings found."

    /// Case-insensitive substring matching
    let private containsIgnoreCase (needle: string) (haystack: string) =
        String.IsNullOrWhiteSpace needle
        || haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0

    // ========================================================================
    // Badge helpers
    // ========================================================================

    let private pluginStatusBadge =
        function
        | PluginLoadStatus.Loaded ->
            Some
                { Label = "loaded"
                  Role = PickerBadgeRole.Success }
        | PluginLoadStatus.Disabled ->
            Some
                { Label = "disabled"
                  Role = PickerBadgeRole.Muted }
        | PluginLoadStatus.Failed _ ->
            Some
                { Label = "failed"
                  Role = PickerBadgeRole.Danger }

    let private macroStatusBadge (model: Model) register chords =
        match model.Recording, model.LastMacro with
        | Some active, _ when active = register ->
            Some
                { Label = "recording"
                  Role = PickerBadgeRole.Success }
        | _, Some last when last = register && not (List.isEmpty chords) ->
            Some
                { Label = "last"
                  Role = PickerBadgeRole.Neutral }
        | _ when not (List.isEmpty chords) ->
            Some
                { Label = "ready"
                  Role = PickerBadgeRole.Success }
        | _ ->
            Some
                { Label = "empty"
                  Role = PickerBadgeRole.Muted }

    let private keybindingSourceBadge isDefault =
        if isDefault then
            Some
                { Label = "default"
                  Role = PickerBadgeRole.Neutral }
        else
            Some
                { Label = "user"
                  Role = PickerBadgeRole.Success }

    // ========================================================================
    // Action helpers
    // ========================================================================

    let private pluginActions (model: Model) (plugin: LoadedPlugin) : PickerAction list =
        let pluginName = plugin.Manifest.Name

        let isFailed =
            match plugin.Status with
            | PluginLoadStatus.Failed _ -> true
            | _ -> false

        let isDisabled = plugin.Status = PluginLoadStatus.Disabled
        let isLoaded = plugin.Status = PluginLoadStatus.Loaded

        [ { Id = PickerActionId.PluginEnable
            Key = Chord.ofChar 'e'
            Label = "Enable"
            Role = PickerActionRole.Primary
            State =
              if isDisabled then
                  PickerActionState.Enabled
              else
                  PickerActionState.Disabled "Plugin is already enabled"
            Confirmation = None
            Dismissal = PickerDismissal.Refresh }
          { Id = PickerActionId.PluginDisable
            Key = Chord.ofChar 'd'
            Label = "Disable"
            Role = PickerActionRole.Primary
            State =
              if isLoaded then
                  PickerActionState.Enabled
              else
                  PickerActionState.Disabled "Plugin is not loaded"
            Confirmation = None
            Dismissal = PickerDismissal.Refresh }
          { Id = PickerActionId.PluginReloadAll
            Key = Chord.ofChar 'r'
            Label = "Reload all"
            Role = PickerActionRole.Secondary
            State =
              if isFailed then
                  PickerActionState.Enabled
              else
                  PickerActionState.Disabled "Reload is only available when plugins have failed"
            Confirmation = None
            Dismissal = PickerDismissal.Refresh }
          { Id = PickerActionId.PluginUninstall
            Key = Chord.ofChar 'u'
            Label = "Uninstall"
            Role = PickerActionRole.Destructive
            State = Enabled
            Confirmation = Some { Label = "uninstall" }
            Dismissal = PickerDismissal.KeepOpen } ]

    let private macroActions (model: Model) (register: char) (chords: Chord list) : PickerAction list =
        let isRecording = model.Recording = Some register
        let isEmpty = List.isEmpty chords
        let isLast = model.LastMacro = Some register

        [ { Id = PickerActionId.MacroReplay
            Key = Chord.bareNamed Enter
            Label = "Replay"
            Role = PickerActionRole.Primary
            State =
              if not isEmpty then
                  PickerActionState.Enabled
              else
                  PickerActionState.Disabled "No keys recorded"
            Confirmation = None
            Dismissal = PickerDismissal.Close }
          { Id = PickerActionId.MacroRecord
            Key = Chord.ofChar 'r'
            Label = if isRecording then "Stop recording" else "Record"
            Role = PickerActionRole.Primary
            State = PickerActionState.Enabled
            Confirmation = None
            Dismissal = PickerDismissal.Close }
          { Id = PickerActionId.MacroMarkLast
            Key = Chord.ofChar 'm'
            Label = "Mark last"
            Role = PickerActionRole.Secondary
            State =
              if not isEmpty then
                  PickerActionState.Enabled
              else
                  PickerActionState.Disabled "No keys recorded"
            Confirmation = None
            Dismissal = PickerDismissal.KeepOpen }
          { Id = PickerActionId.MacroClear
            Key = Chord.ofChar 'c'
            Label = "Clear"
            Role = PickerActionRole.Destructive
            State =
              if not isEmpty || isRecording then
                  PickerActionState.Enabled
              else
                  PickerActionState.Disabled "Nothing to clear"
            Confirmation = Some { Label = "clear" }
            Dismissal = PickerDismissal.KeepOpen } ]

    let private keybindingActions: PickerAction list =
        [ { Id = PickerActionId.PickerClose
            Key = Chord.bareNamed Enter
            Label = "Close"
            Role = PickerActionRole.Primary
            State = PickerActionState.Enabled
            Confirmation = None
            Dismissal = PickerDismissal.Close }
          { Id = PickerActionId.PickerClose
            Key = Chord.bareNamed Escape
            Label = "Close"
            Role = PickerActionRole.Primary
            State = PickerActionState.Enabled
            Confirmation = None
            Dismissal = PickerDismissal.Close } ]

    // ========================================================================
    // Plugin picker items
    // ========================================================================

    let private pluginItems (model: Model) : PickerItem list =
        model.Plugins.Loaded
        |> Map.toList
        |> List.map (fun (_, loadedPlugin) ->
            let status = pluginStatusBadge loadedPlugin.Status

            let accessories =
                [ TextAccessory loadedPlugin.Manifest.Version
                  CountAccessory("cmd", loadedPlugin.Commands.Length)
                  CountAccessory("keys", loadedPlugin.Keybindings.Length) ]

            let inspector =
                Some
                    { Title = loadedPlugin.Manifest.Name
                      Subtitle = Some loadedPlugin.Manifest.Version
                      Lines =
                        [ TextLine loadedPlugin.Manifest.Description
                          PathLine loadedPlugin.Path
                          match loadedPlugin.Status with
                          | PluginLoadStatus.Failed reason -> ErrorLine reason
                          | _ -> () ] }

            let searchTerms =
                [ loadedPlugin.Manifest.Name
                  loadedPlugin.Manifest.Description
                  loadedPlugin.Manifest.Version
                  loadedPlugin.Path ]

            { Id = loadedPlugin.Manifest.Name
              Title = loadedPlugin.Manifest.Name
              Subtitle = Some loadedPlugin.Manifest.Description
              Badge = status
              Accessories = accessories
              Inspector = inspector
              SearchTerms = searchTerms
              Actions = pluginActions model loadedPlugin })
        |> List.sortBy (fun item -> item.Title.ToLowerInvariant())

    // ========================================================================
    // Macro picker items
    // ========================================================================

    let private macroItems (model: Model) : PickerItem list =
        let letters = [ 'a' .. 'z' ] |> Set.ofList

        let extra =
            [ yield! model.Registers |> Map.keys
              yield! model.Recording |> Option.toList
              yield! model.LastMacro |> Option.toList ]
            |> Set.ofList

        Set.union letters extra
        |> Set.toList
        |> List.map (fun register ->
            let chords = model.Registers |> Map.tryFind register |> Option.defaultValue []
            let sequence = Chord.renderStroke chords
            let status = macroStatusBadge model register chords

            let accessories = [ CountAccessory("chords", chords.Length) ]

            let inspector =
                Some
                    { Title = $"@{register}"
                      Subtitle = Some(if List.isEmpty chords then "No keys recorded" else sequence)
                      Lines =
                        if List.isEmpty chords then
                            [ TextLine "No keys recorded." ]
                        else
                            [ ShortcutSequenceLine chords ] }

            let searchTerms = [ $"@{register}"; sequence ]

            { Id = string register
              Title = $"@{register}"
              Subtitle = Some(if List.isEmpty chords then "No keys recorded" else sequence)
              Badge = status
              Accessories = accessories
              Inspector = inspector
              SearchTerms = searchTerms
              Actions = macroActions model register chords })

    // ========================================================================
    // Keybinding picker items
    // ========================================================================

    let private ctxName =
        function
        | Context.Global -> "global"
        | Context.Editor -> "editor"
        | Context.Sidebar -> "sidebar"
        | Context.Prompt -> "prompt"

    let private ctxRank =
        function
        | Context.Global -> 0
        | Context.Editor -> 1
        | Context.Sidebar -> 2
        | Context.Prompt -> 3

    let private actionLabel =
        function
        | Some action -> Action.name action
        | None -> "(unbound)"

    let private keybindingItems (model: Model) : PickerItem list =
        let defaults = Keymap.defaults |> Set.ofList

        model.Keymap
        |> List.fold (fun acc binding -> Map.add (binding.Context, binding.Stroke) binding acc) Map.empty
        |> Map.toList
        |> List.sortBy (fun ((ctx, stroke), _) -> ctxRank ctx, Chord.renderStroke stroke)
        |> List.map (fun ((ctx, stroke), binding) ->
            let strokeText = Chord.renderStroke stroke
            let actionText = actionLabel binding.Action
            let source = if defaults.Contains binding then "default" else "user"
            let context = ctxName ctx
            let id = $"{context}:{strokeText}"

            let badge = keybindingSourceBadge (defaults.Contains binding)

            let accessories = [ TextAccessory context ]

            let searchTerms = [ strokeText; actionText; context; source ]

            { Id = id
              Title = strokeText
              Subtitle = Some actionText
              Badge = badge
              Accessories = accessories
              Inspector = None
              SearchTerms = searchTerms
              Actions = keybindingActions })

    // ========================================================================
    // Picker view construction
    // ========================================================================

    /// Filter items based on the filter string
    let private filterItems (filter: string) (items: PickerItem list) : PickerItem list =
        if String.IsNullOrWhiteSpace filter then
            items
        else
            items
            |> List.filter (fun item ->
                item.SearchTerms |> List.exists (containsIgnoreCase filter)
                || containsIgnoreCase filter item.Title
                || (item.Subtitle
                    |> Option.map (containsIgnoreCase filter)
                    |> Option.defaultValue false)
                || (item.Badge
                    |> Option.map (fun b -> containsIgnoreCase filter b.Label)
                    |> Option.defaultValue false))

    /// Get the items for a picker kind
    let private itemsForKind (model: Model) (kind: PickerKind) : PickerItem list =
        match kind with
        | PickerKind.PluginPicker -> pluginItems model
        | PickerKind.MacroPicker -> macroItems model
        | PickerKind.KeyBindingPicker -> keybindingItems model

    /// Build a complete PickerView from model and picker state
    let buildView (model: Model) (pickerState: PickerState) : PickerView =
        let kind = pickerState.Kind
        let layout = layoutForKind kind
        let title = titleForKind kind

        let allItems = itemsForKind model kind
        let filteredItems = filterItems pickerState.Filter allItems

        let selectedIndex =
            match pickerState.SelectedItemId with
            | Some id ->
                filteredItems
                |> List.tryFindIndex (fun item -> item.Id = id)
                |> Option.defaultValue 0
            | None -> 0

        let emptyText = defaultEmptyText kind

        let footer =
            match pickerState.PendingConfirmation with
            | Some pending -> ConfirmationFooter(pending.Label, pending.Key)
            | None ->
                let visibleActions =
                    match kind with
                    | PickerKind.PluginPicker ->
                        filteredItems
                        |> List.tryItem selectedIndex
                        |> Option.map (fun item -> item.Actions)
                        |> Option.defaultValue []
                    | PickerKind.MacroPicker ->
                        filteredItems
                        |> List.tryItem selectedIndex
                        |> Option.map (fun item -> item.Actions)
                        |> Option.defaultValue []
                    | PickerKind.KeyBindingPicker -> keybindingActions

                ActionFooter visibleActions

        { Title = title
          Layout = layout
          Filter = pickerState.Filter
          Items = filteredItems
          SelectedIndex = selectedIndex
          EmptyText = emptyText
          Footer = footer }

    /// Rows the picker wants to occupy: title + content + footer. The renderer's
    /// caller clamps this to the configured dock max. Content is the list length,
    /// or — for the inspector layout — the larger of the list and the selected
    /// item's inspector lines, so the inspector is never clipped.
    let desiredRows (view: PickerView) : int =
        let contentRows =
            if List.isEmpty view.Items then
                1
            else
                match view.Layout with
                | SearchResults -> view.Items.Length
                | ListWithInspector ->
                    let inspectorRows =
                        view.Items
                        |> List.tryItem view.SelectedIndex
                        |> Option.bind (fun item -> item.Inspector)
                        |> Option.map (fun ins ->
                            1 + (if Option.isSome ins.Subtitle then 1 else 0) + List.length ins.Lines)
                        |> Option.defaultValue 0

                    max view.Items.Length inspectorRows

        2 + contentRows

    /// Clamp the selected item ID after filtering or model changes
    let clampSelection (model: Model) (pickerState: PickerState) : PickerState =
        let allItems = itemsForKind model pickerState.Kind
        let filteredItems = filterItems pickerState.Filter allItems

        let selected =
            match filteredItems, pickerState.SelectedItemId with
            | [], _ -> None
            | rows, Some id when rows |> List.exists (fun row -> row.Id = id) -> Some id
            | row :: _, _ -> Some row.Id

        { pickerState with
            SelectedItemId = selected }

    /// Move selection by delta
    let moveSelection delta (model: Model) (pickerState: PickerState) : PickerState =
        let allItems = itemsForKind model pickerState.Kind
        let filteredItems = filterItems pickerState.Filter allItems

        if filteredItems.IsEmpty then
            { pickerState with
                SelectedItemId = None
                PendingConfirmation = None }
        else
            let current =
                match pickerState.SelectedItemId with
                | Some id ->
                    filteredItems
                    |> List.tryFindIndex (fun row -> row.Id = id)
                    |> Option.defaultValue 0
                | None -> 0

            let next = max 0 (min (filteredItems.Length - 1) (current + delta))

            { pickerState with
                SelectedItemId = Some filteredItems[next].Id
                PendingConfirmation = None }

    /// Set selection to a specific index
    let setSelection index (model: Model) (pickerState: PickerState) : PickerState =
        let allItems = itemsForKind model pickerState.Kind
        let filteredItems = filterItems pickerState.Filter allItems

        if filteredItems.IsEmpty then
            { pickerState with
                SelectedItemId = None
                PendingConfirmation = None }
        else
            let next = max 0 (min (filteredItems.Length - 1) index)

            { pickerState with
                SelectedItemId = Some filteredItems[next].Id
                PendingConfirmation = None }

    /// Append to filter
    let appendFilter value (model: Model) (pickerState: PickerState) : PickerState =
        let nextState =
            { pickerState with
                Filter = pickerState.Filter + value
                PendingConfirmation = None }
            |> clampSelection model

        nextState

    /// Backspace filter
    let backspaceFilter (model: Model) (pickerState: PickerState) : PickerState =
        if String.IsNullOrEmpty pickerState.Filter then
            { pickerState with
                PendingConfirmation = None }
        else
            let nextState =
                { pickerState with
                    Filter = pickerState.Filter.Remove(pickerState.Filter.Length - 1)
                    PendingConfirmation = None }
                |> clampSelection model

            nextState

    /// Get the first item ID for a picker kind
    let firstItemId (model: Model) (kind: PickerKind) : string option =
        itemsForKind model kind |> List.tryHead |> Option.map _.Id
