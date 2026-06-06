# Picker Redesign

## Purpose

Replace the current plugin/macro/keybinding list-manager implementation with a reusable Picker primitive that is driven by semantic data rather than ad hoc table cells and per-manager rendering branches.

The immediate goal is to make the plugin, macro, and keybinding surfaces feel intentional:

- plugins and macros render as a list with a selected-item inspector;
- keybindings render as searchable results;
- action keys, destructive actions, disabled actions, confirmation, status, and metadata are styled from structured data;
- update logic routes through one generic picker action path instead of bolted-on manager-specific key handling.

This is a refactor of the in-editor picker surfaces only. Plugin persistence, macro persistence, and the public plugin API are outside this design.

## Current Problems

The current implementation uses `ListManagerState`, raw row cells, and hardcoded action handling in `Editor.fs`. It works mechanically, but the data model is too weak for a polished UI.

Specific issues:

- Row data is table-shaped even when the UI should not be a table.
- Action hints are rendered as flat strings, so shortcuts cannot be styled distinctly from labels.
- Destructive and disabled actions are not first-class concepts in the view model.
- Plugin and macro item details are crammed into a single detail line instead of a selected-item inspector.
- Keybindings need a palette/search-result presentation, not an inspector-heavy management UI.
- The update loop has too much knowledge of individual manager action keys.

## Borrowed Vocabulary

The generic primitive is named **Picker**.

This follows editor precedent:

- Helix calls interactive searchable selection lists "pickers."
- VS Code exposes a similar API concept as `QuickPick`, while the product surface is the Command Palette.
- Zed uses command-palette and keymap terminology around actions and bindings.
- Vim does not have one universal primitive name, but its quickfix/location lists reinforce the idea that searchable editor lists deserve explicit interaction models.

For fedit, `Picker` is the best generic term because the surface is not always a command palette, not always a table, and not only a manager.

Reference terms:

- `PickerItem`: one selectable object in the picker.
- `PickerAction`: an action available for the selected item or picker.
- `PickerLayout`: hardcoded presentation style for a picker kind.
- `PickerInspector`: selected-item details for inspector-style pickers.
- `PickerBadge` and `PickerAccessory`: small semantic metadata attached to an item.

## Picker Kinds And Layouts

Picker kinds are domain-specific; layouts are generic:

```fsharp
type PickerKind =
    | PluginPicker
    | MacroPicker
    | KeyBindingPicker

type PickerLayout =
    | ListWithInspector
    | SearchResults
```

Layouts are not user-toggleable. Each picker kind chooses the layout that matches the workflow:

| Picker | Layout | Reason |
| --- | --- | --- |
| `PluginPicker` | `ListWithInspector` | Plugins need status, path/error detail, counts, and lifecycle actions. |
| `MacroPicker` | `ListWithInspector` | Macros need selected register detail, sequence preview, and register actions. |
| `KeyBindingPicker` | `SearchResults` | Keybindings are primarily searched and inspected inline. |

## Picker State

Picker state stores only interaction state, not generated rows:

```fsharp
type PickerState =
    { Kind: PickerKind
      SelectedItemId: string option
      Filter: string
      PendingConfirmation: PickerPendingConfirmation option }

type PickerPendingConfirmation =
    { ItemId: string option
      ActionId: PickerActionId
      Key: Chord
      Label: string }
```

Generated picker content is recomputed from `Model + PickerState`. This keeps async changes, plugin rescans, and macro recording state naturally reflected in the picker view.

## Picker Items

Picker items are semantic. They do not expose table cells.

```fsharp
type PickerItem =
    { Id: string
      Title: string
      Subtitle: string option
      Badge: PickerBadge option
      Accessories: PickerAccessory list
      Inspector: PickerInspector option
      SearchTerms: string list
      Actions: PickerAction list }
```

`Title` is the primary visible label. `Subtitle` is a short secondary description. `Badge` carries state such as loaded, disabled, failed, ready, recording, empty, user, or default. `Accessories` are trailing metadata such as counts, versions, contexts, or source labels. `Inspector` contains selected-item details for `ListWithInspector`.

Search uses `SearchTerms`, not rendered text. In this slice filtering remains simple case-insensitive substring matching, but modeling search terms separately leaves room for field-aware filters later.

## Badges And Accessories

Badges and accessories provide styling hooks without renderer-specific strings.

```fsharp
type PickerBadge =
    { Label: string
      Role: PickerBadgeRole }

type PickerBadgeRole =
    | Neutral
    | Success
    | Warning
    | Danger
    | Muted

type PickerAccessory =
    | TextAccessory of string
    | CountAccessory of label: string * value: int
    | ShortcutAccessory of Chord
```

Examples:

- plugin status: `loaded`, `disabled`, `failed`;
- plugin accessories: `v1.0.0`, `3 cmd`, `2 keys`;
- macro accessories: `14 chords`, `last`;
- keybinding accessories: `editor`, `default`, `user`.

## Inspector Content

Inspector content is structured enough for clean rendering, but not over-modeled.

```fsharp
type PickerInspector =
    { Title: string
      Subtitle: string option
      Lines: PickerInspectorLine list }

type PickerInspectorLine =
    | TextLine of string
    | PathLine of string
    | ErrorLine of string
    | ShortcutSequenceLine of Chord list
```

Plugin inspector examples:

- title: plugin name;
- subtitle: version and description;
- lines: path, load error, command count, keybinding count.

Macro inspector examples:

- title: register name;
- subtitle: status;
- lines: rendered chord sequence or empty-state text.

Keybinding search results do not need a side inspector in this slice. Their context/source/action information should render inline.

## Picker Actions

Picker actions are first-class data. They describe what can happen, how it should be shown, and what should happen to the picker after execution. They do not contain closures.

```fsharp
type PickerActionId =
    | PluginEnable
    | PluginDisable
    | PluginReloadAll
    | PluginUninstall
    | MacroReplay
    | MacroRecord
    | MacroMarkLast
    | MacroClear
    | PickerClose

type PickerAction =
    { Id: PickerActionId
      Key: Chord
      Label: string
      Role: PickerActionRole
      State: PickerActionState
      Confirmation: PickerConfirmation option
      Dismissal: PickerDismissal }

type PickerActionRole =
    | Primary
    | Secondary
    | Destructive

type PickerActionState =
    | Enabled
    | Disabled of reason: string

type PickerConfirmation =
    { Label: string }

type PickerDismissal =
    | KeepOpen
    | Close
    | Refresh
```

The update loop handles action keys generically:

1. Build the current `PickerView`.
2. Find the selected item.
3. Find an enabled action whose `Key` matches the pressed chord.
4. If the action requires confirmation and is not already armed, store `PendingConfirmation`.
5. If confirmed or no confirmation is needed, dispatch by `PickerActionId`.
6. Apply `PickerDismissal`.
7. Clamp selection after model changes.

This preserves testability because actions are data and execution remains explicit in a runner.

## Picker View

`PickerView` is render-ready semantic data produced from `Model + PickerState`.

```fsharp
type PickerView =
    { Title: string
      Layout: PickerLayout
      Filter: string
      Items: PickerItem list
      SelectedIndex: int
      EmptyText: string
      Footer: PickerFooter }

type PickerFooter =
    | ActionFooter of PickerAction list
    | ConfirmationFooter of label: string * key: Chord
```

The renderer should consume only `PickerView`, not `Model`. This makes it possible to test row generation and rendering separately.

## Rendering Rules

### Shared Rules

- Action keys render as distinct key badges, not inline text fragments.
- Destructive actions use a destructive role color.
- Disabled actions render muted in inspector layout and are hidden or deemphasized in search-results layout.
- Confirmation replaces the footer with a focused confirmation message.
- Long text is cropped, not wrapped.
- Filter text appears in the picker title area.
- Empty states use `EmptyText`.

### `ListWithInspector`

Used by plugin and macro pickers.

Layout:

- left area: filtered item list;
- right area: selected-item inspector;
- bottom footer: selected-item actions.

The left list should show title, badge, subtitle or compact summary, and accessories. The inspector should show selected item title, subtitle, detail lines, and action hints. This layout makes selected-item state and consequences visible before running actions.

### `SearchResults`

Used by keybinding picker.

Layout:

- title/filter row;
- result rows with shortcut badge, action title, context, and source;
- compact footer with close/search hints.

No side inspector. The selected row can use one extra detail line if necessary, but keybindings should stay optimized for fast searching.

## Domain Mapping

### Plugins

Items:

- title: plugin name;
- subtitle: description or path;
- badge: loaded, disabled, failed;
- accessories: version, command count, keybinding count;
- inspector: path, version, description, conflicts or load error.

Actions:

- `Enter`: enable/disable loaded or disabled plugin; reload all for failed plugin;
- `e`: enable;
- `d`: disable;
- `r`: reload all;
- `u`: uninstall, destructive confirmation.

Dismissal:

- enable/disable/reload keep picker open and refresh data;
- uninstall keeps picker open and clamps selection after removal.

### Macros

Items:

- title: register, such as `@a`;
- subtitle: rendered sequence preview or empty text;
- badge: empty, ready, recording, last;
- accessories: chord count;
- inspector: sequence preview and status.

Actions:

- `Enter`: replay selected non-empty macro;
- `o`: record or stop recording selected register;
- `m`: mark selected non-empty register as last;
- `c`: clear selected register, destructive confirmation.

Dismissal:

- replay closes picker;
- start/stop recording closes picker;
- mark-last keeps picker open;
- clear keeps picker open and refreshes selected state.

### Keybindings

Items:

- title: rendered stroke;
- subtitle: action name;
- badge: user or default;
- accessories: context;
- search terms: stroke, action, context, source.

Actions:

- `Enter`: close;
- `Esc`: close;
- typing filters.

No mutation actions in this slice.

## Files And Boundaries

Expected production files:

- `PickerTypes.fs`: picker semantic types. Add this before `Model.fs` in the project file.
- `Model.fs`: rename list-manager state to picker state.
- `Pickers.fs`: build picker views and item/action data for plugin, macro, and keybinding pickers.
- `Editor.fs`: generic picker input handling and action runner.
- `Primitives.fs`: dock panel case for a picker view.
- `View.fs`: render `PickerView` in `ListWithInspector` and `SearchResults` layouts.

The renderer should not know plugin, macro, or keybinding business rules. It should only know picker layouts and semantic roles.

Theme mapping should use existing theme roles first:

- selected row: existing selection style;
- normal badge/accessory: chrome/accent foreground on dock background;
- success badge: existing accent if no success color exists;
- warning/danger badge and destructive actions: existing notification warning/error colors if exposed, otherwise introduce semantic theme slots in a separate small theme change;
- muted/disabled text: chrome foreground with non-bold styling.

Disabled actions are visible in `ListWithInspector` so users can see why a selected plugin or macro cannot do something. Disabled actions are hidden in `SearchResults` because the keybinding picker has no mutation actions in this slice.

## Testing

Unit tests should cover:

- commands open the correct picker kind and layout;
- picker input blocks normal editor key handling while active;
- filtering updates items and clears pending confirmation;
- selection clamps after filtering and async plugin changes;
- action lookup ignores disabled actions;
- destructive actions require two matching keypresses on the same item/action;
- macro replay emits `ReplayKeys` and closes picker;
- macro recording starts/stops and closes picker;
- plugin enable/disable updates config, saves config, and rescans plugins;
- keybinding picker renders user/default source and context as semantic data;
- `ListWithInspector` renders action key badges, item badge, accessories, and inspector content;
- `SearchResults` renders shortcut badge, action title, context, source, and compact footer;
- long labels/details are cropped without corrupting terminal rows.

Verification:

- focused picker update/render tests;
- full `dotnet test`;
- `just test`;
- `git diff --check`.

## Out Of Scope

- Macro persistence.
- Editing keybindings from the picker.
- Field-aware filters such as `status:failed`.
- User-toggleable picker layouts.
- Changes to `Fedit.PluginApi`.
- A full table layout engine.

## References

- Helix picker docs: <https://docs.helix-editor.com/master/pickers.html>
- Helix keymap docs: <https://docs.helix-editor.com/keymap.html>
- VS Code keybindings docs: <https://code.visualstudio.com/docs/getstarted/keybindings>
- Zed key bindings docs: <https://zed.dev/docs/key-bindings>
- Zed command palette docs: <https://zed.dev/docs/command-palette.html>
- Vim mapping docs: <https://vimhelp.org/map.txt.html>
- Vim quickfix docs: <https://vimhelp.org/quickfix.txt.html>
