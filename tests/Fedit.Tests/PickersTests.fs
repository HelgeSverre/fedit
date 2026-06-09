module Fedit.Tests.PickersTests

open Fedit
open Fedit.PickerTypes
open Fedit.PromptTypes
open Xunit
open FsUnit.Xunit

// Picker primitive: view generation (Pickers.buildView and friends) and the
// generic action runner in Editor.update. These cover the semantic data model
// — badges, accessories, inspector content, disabled-action lookup, and
// destructive confirmation — that the renderer styles from.

let private baseModel () =
    let model, _ =
        Editor.init "/root" { Width = 100; Height = 24 } (Config.defaults Themes.defaultTheme) [] None

    model

let private chr c : Chord = { Mods = Set.empty; Key = Key.Char c }
let private nk n : Chord = { Mods = Set.empty; Key = Named n }

let private loadedPlugin name path status =
    let manifest =
        { Name = name
          Version = "1.0.0"
          ApiVersion = "1"
          Description = "desc"
          Author = ""
          Homepage = ""
          EntryAssembly = $"{name}.dll"
          EntryType = "X.Plugin" }

    { Manifest = manifest
      Path = path
      Status = status
      Commands = []
      Keybindings = []
      Conflicts = [] }

let private withPlugins plugins model =
    { model with
        Plugins =
            { PluginRegistry.empty with
                Loaded = plugins |> List.map (fun p -> p.Manifest.Name, p) |> Map.ofList } }

let private pluginPicker selected : PickerState =
    { Kind = PluginPicker
      SelectedItemId = selected
      Filter = ""
      PendingConfirmation = None }

let private withPromptSession session selected filter model =
    { model with
        Focus = Prompt
        Prompt =
            { model.Prompt with
                Active = true
                Session = session
                Text = filter
                Cursor = filter.Length
                SelectedItemId = selected
                PendingConfirmation = None } }

let private renderRows model =
    let screen = Layout.render model

    [| for row in 0 .. screen.Height - 1 ->
           System.String([| for col in 0 .. screen.Width - 1 -> screen.Cells[row, col].Glyph |]) |]

let private renderText model = renderRows model |> String.concat "\n"

let private item id : PickerItem =
    { Id = id
      Title = id
      Subtitle = None
      Badge = None
      Accessories = []
      Inspector = None
      SearchTerms = []
      Actions = [] }

let private view layout items selectedIndex : PickerView =
    { Title = "T"
      Layout = layout
      Filter = ""
      Items = items
      SelectedIndex = selectedIndex
      EmptyText = "empty"
      Footer = ActionFooter [] }

// ── View generation ───────────────────────────────────────────────────

[<Fact>]
let ``plugin picker view uses the ListWithInspector layout`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]

    let view = Pickers.buildView model (pluginPicker (Some "alpha"))

    view.Layout |> should equal ListWithInspector
    view.Title |> should equal "Plugins"

[<Fact>]
let ``a loaded plugin item carries a loaded badge and a version accessory`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]

    let view = Pickers.buildView model (pluginPicker (Some "alpha"))
    let item = view.Items |> List.exactlyOne

    item.Badge |> should equal (Some { Label = "loaded"; Role = Success })
    item.Accessories |> List.contains (TextAccessory "1.0.0") |> should equal true

[<Fact>]
let ``a failed plugin item shows a danger badge and an inspector error line`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" (PluginLoadStatus.Failed "boom") ]

    let view = Pickers.buildView model (pluginPicker (Some "alpha"))
    let item = view.Items |> List.exactlyOne

    item.Badge |> should equal (Some { Label = "failed"; Role = Danger })

    item.Inspector
    |> Option.map (fun ins -> ins.Lines |> List.contains (ErrorLine "boom"))
    |> should equal (Some true)

// ── Action lookup ─────────────────────────────────────────────────────

[<Fact>]
let ``pressing enable on an already-loaded plugin is a no-op (disabled action ignored)`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]
        |> withPromptSession PromptSessionKind.PluginsSession (Some "alpha") ""

    let next, effects = Editor.update (KeyPressed(chr 'e')) model

    Set.isEmpty next.Config.DisabledPlugins |> should equal true
    Assert.Empty effects

// ── Destructive confirmation ──────────────────────────────────────────

[<Fact>]
let ``uninstall arms a pending confirmation on the first press`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]
        |> withPromptSession PromptSessionKind.PluginsSession (Some "alpha") ""

    let armed, effects = Editor.update (KeyPressed(chr 'u')) model

    armed.Prompt.PendingConfirmation.IsSome |> should equal true
    Assert.Empty effects

[<Fact>]
let ``uninstall confirmed on the second matching press removes the plugin`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]
        |> withPromptSession PromptSessionKind.PluginsSession (Some "alpha") ""

    let armed, _ = Editor.update (KeyPressed(chr 'u')) model
    let confirmed, effects = Editor.update (KeyPressed(chr 'u')) armed

    effects |> should equal [ RemovePluginDir "alpha" ]
    confirmed.Prompt.PendingConfirmation |> should equal None

[<Fact>]
let ``typing into the filter clears a pending confirmation`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]
        |> withPromptSession PromptSessionKind.PluginsSession (Some "alpha") ""

    let armed, _ = Editor.update (KeyPressed(chr 'u')) model
    armed.Prompt.PendingConfirmation.IsSome |> should equal true

    let filtered, _ = Editor.update (KeyPressed(chr 'z')) armed
    filtered.Prompt.PendingConfirmation |> should equal None
    filtered.Prompt.Text |> should equal "z"

// ── Selection clamping ────────────────────────────────────────────────

[<Fact>]
let ``selection clamps to a still-visible item after filtering`` () =
    let model =
        baseModel ()
        |> withPlugins
            [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded
              loadedPlugin "beta" "/p/beta" PluginLoadStatus.Loaded ]

    let clamped =
        Pickers.clampSelection
            model
            { pluginPicker (Some "beta") with
                Filter = "alph" }

    clamped.SelectedItemId |> should equal (Some "alpha")

// ── Keybinding picker (SearchResults) ─────────────────────────────────

[<Fact>]
let ``keybinding picker uses SearchResults layout with default source and context data`` () =
    let view =
        Pickers.buildView
            (baseModel ())
            { Kind = KeyBindingPicker
              SelectedItemId = None
              Filter = ""
              PendingConfirmation = None }

    view.Layout |> should equal SearchResults

    view.Items
    |> List.exists (fun i -> i.Badge = Some { Label = "default"; Role = Neutral })
    |> should equal true

    view.Items
    |> List.forall (fun i -> not (List.isEmpty i.Accessories))
    |> should equal true

// ── Rendering ─────────────────────────────────────────────────────────

[<Fact>]
let ``plugin picker render shows the uninstall action key chip and label`` () =
    let model =
        baseModel ()
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]
        |> withPromptSession PromptSessionKind.PluginsSession (Some "alpha") ""

    let text = renderText model

    text |> should haveSubstring "[u]"
    text |> should haveSubstring "Uninstall"

[<Fact>]
let ``keybinding picker render shows default source badge and context for global bindings`` () =
    // Empty filter lists every binding; the default keymap sorts global-context
    // (default-source) bindings first, so those render in the visible window.
    let model =
        baseModel () |> withPromptSession PromptSessionKind.KeybindingsSession None ""

    let text = renderText model

    text |> should haveSubstring "Keybindings"
    text |> should haveSubstring "default"
    text |> should haveSubstring "global"

// ── desiredRows (content-aware dock height) ───────────────────────────

[<Fact>]
let ``desiredRows for SearchResults is title + items + footer`` () =
    let v = view SearchResults [ item "a"; item "b"; item "c" ] 0
    Pickers.desiredRows v |> should equal 5

[<Fact>]
let ``desiredRows for ListWithInspector counts the selected item's inspector lines`` () =
    let inspector =
        { Title = "alpha"
          Subtitle = Some "1.0.0"
          Lines = [ TextLine "desc"; PathLine "/p/alpha" ] }

    let only =
        { item "alpha" with
            Inspector = Some inspector }

    let v = view ListWithInspector [ only ] 0
    // content = max(1 item, 1 title + 1 subtitle + 2 lines = 4) = 4; + title + footer
    Pickers.desiredRows v |> should equal 6

[<Fact>]
let ``desiredRows for an empty picker is the minimum three rows`` () =
    Pickers.desiredRows (view SearchResults [] 0) |> should equal 3

[<Fact>]
let ``plugin picker dock shrinks to fit a single item instead of filling the configured max`` () =
    // height 40, default DockHeight 8 → cap = min(8, max(3, 40/3)) = 8.
    // One plugin with a 4-line inspector → desiredRows 6 < 8, so the dock is
    // shorter than the cap and its title sits below the would-be full-height top
    // (full height would place the title at 40 - 8 - 1 = 31).
    let model, _ =
        Editor.init "/root" { Width = 100; Height = 40 } (Config.defaults Themes.defaultTheme) [] None

    let model =
        model
        |> withPlugins [ loadedPlugin "alpha" "/p/alpha" PluginLoadStatus.Loaded ]
        |> withPromptSession PromptSessionKind.PluginsSession (Some "alpha") ""

    let titleRow = renderRows model |> Array.findIndex (fun r -> r.Contains "Plugins")

    (titleRow > 31) |> should equal true

[<Fact>]
let ``command completion dock shrinks to the number of completions`` () =
    // The `:` command palette is the second in-scope list surface: with two
    // completions the dock is 1 + 2 = 3 rows, far below the cap of 8, so the
    // "Commands" title sits well below the full-height position (40 - 8 - 1 = 31).
    let completions =
        [ { Label = "open"
            ApplyText = "open"
            Detail = ""
            Kind = CompletionKind.Command }
          { Label = "quit"
            ApplyText = "quit"
            Detail = ""
            Kind = CompletionKind.Command } ]

    let model, _ =
        Editor.init "/root" { Width = 100; Height = 40 } (Config.defaults Themes.defaultTheme) [] None

    let model =
        { model with
            Focus = Prompt
            Prompt =
                { model.Prompt with
                    Active = true
                    Text = ":o"
                    Mode = PromptMode.Command
                    Completions = completions
                    SelectedCompletion = 0 } }

    let titleRow = renderRows model |> Array.findIndex (fun r -> r.Contains "Commands")

    (titleRow > 31) |> should equal true
