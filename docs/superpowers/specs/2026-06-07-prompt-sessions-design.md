# Prompt Sessions

## Purpose

Replace the separate picker interaction model with named prompt sessions.

The goal is one editor-wide interaction modality: when the user needs to type,
filter, select, inspect, or run an item-specific action, they use the prompt.
Commands such as `:plugins`, `:macros`, and `:keybinds` still open the
surfaces, but after opening them the prompt is no longer a command string. It
becomes a named session such as `Plugins`, `Macros`, or `Keybindings`.

This design supersedes the standalone picker direction in
`2026-06-06-picker-redesign-design.md`. The semantic item/action work remains
useful, but the active state and key routing should belong to prompt sessions,
not to a separate `Model.Picker`.

## Current Problem

The current implementation has two different systems that solve almost the
same interaction problem.

The prompt handles:

- text input;
- cursor movement;
- history;
- completions;
- selected completion;
- command execution;
- file opening;
- buffer switching;
- incremental search.

The picker handles:

- filter text;
- selected item;
- item actions;
- destructive confirmation;
- plugin, macro, and keybinding lists;
- a separate key-routing path before normal editor dispatch.

They are visually similar because both render through the command line and
dock, but architecturally they are separate modes. This creates duplicate
state, duplicate keyboard behavior, and a larger surface area for bugs.

## Product Model

The prompt should be the only transient interaction surface.

Opening a surface should feel like this:

1. The user types `:plugins`.
2. The command executes.
3. The command prompt closes as a command prompt.
4. A `Plugins` prompt session opens.
5. The input line now filters plugins.
6. Up/down moves the selected plugin.
7. Enter or action keys run actions for the selected plugin.
8. Escape closes the session and returns to the editor.

The command is only the doorway. The session is the room.

This avoids a confusing command string like `:plugins alpha` where `alpha` is
not really a command argument. The prompt line should instead show the current
session identity directly, for example:

```text
Plugins: alpha
```

or with the existing command-line chrome:

```text
[Plugins] alpha
```

The exact rendering can be decided during implementation, but the model should
not treat `alpha` as part of a command.

## Core Types

The model should replace `PromptMode` and `PickerState` with a richer prompt
session model.

```fsharp
type PromptSessionKind =
    | FileOpen
    | Command
    | Search
    | BufferSwitch
    | Plugins
    | Macros
    | Keybindings

type PromptSession =
    { Kind: PromptSessionKind
      Text: string
      Cursor: int
      SelectedIndex: int
      HistoryIndex: int option
      PendingConfirmation: PromptConfirmation option
      SearchPreview: SearchPreview option }
```

`PromptState` should then describe whether a session is active and carry shared
history:

```fsharp
type PromptState =
    { Active: bool
      Session: PromptSession option
      History: string list }
```

The exact shape can change during implementation, but the important boundary is
that active transient interaction state lives under `Prompt`, not in a sibling
`Picker` field.

## Render Model

Prompt sessions should produce a render-ready surface.

```fsharp
type PromptSurface =
    { Title: string
      Query: string
      Items: PromptItem list
      SelectedIndex: int
      EmptyText: string
      Preview: PromptPreview option
      Footer: PromptFooter }
```

This keeps the renderer generic. It should not know plugin, macro, keybinding,
file, buffer, or command business rules.

`PromptItem` should carry semantic data:

```fsharp
type PromptItem =
    { Id: string
      Title: string
      Subtitle: string option
      Badge: PromptBadge option
      Accessories: PromptAccessory list
      SearchTerms: string list
      Actions: PromptAction list }
```

The previous picker semantic concepts still apply:

- badges for state such as `loaded`, `disabled`, `failed`, `ready`, `empty`,
  `user`, or `default`;
- accessories for counts, versions, contexts, and shortcuts;
- actions as data rather than closures;
- destructive confirmation as session state.

The names should change from `Picker*` to `Prompt*` only where the concept is
now genuinely prompt-owned. Domain builders can still be named directly, such
as `PluginPrompt`, `MacroPrompt`, or `KeybindingPrompt`, if that reads better
in F#.

## Session Behavior

All sessions share a baseline controller:

- `Escape`: close the prompt session;
- text input: insert into the session query;
- `Backspace` and `Delete`: edit the query;
- left/right/home/end: move the query cursor;
- up/down/page-up/page-down: move selection when the session has items;
- `Enter`: run the primary action for the selected item or session;
- action keys: run enabled actions for the selected item;
- filter changes clear pending confirmation.

Session-specific behavior is implemented by a session definition:

```fsharp
type PromptSessionDefinition =
    { Kind: PromptSessionKind
      BuildSurface: Model -> PromptSession -> PromptSurface
      ExecuteAction: PromptActionId -> PromptItem option -> Model -> PromptSession -> Model * Effect list }
```

This keeps one key loop while preserving explicit domain behavior.

## Session Mapping

### Command

The command session is the current `:` mode.

It owns command completion, command parsing, goto parsing, command history, and
theme preview. Running `:plugins`, `:macros`, or bare `:keybinds` should open
the corresponding named prompt session.

### FileOpen

The file-open session is the current empty or `Ctrl+O` prompt behavior.

It filters recent files and workspace files. `Enter` opens the selected file.

### Search

The search session is the current `/` behavior.

It keeps incremental search preview and match navigation. It may remain more
special than list sessions because it updates the active editor preview while
typing.

### BufferSwitch

The buffer session is the current `@` behavior.

It filters buffers and switches to the selected buffer on `Enter`.

### Plugins

Opened by `:plugins`.

It filters plugins. Items show plugin name, description, status badge, version,
command count, keybinding count, and selected-plugin detail. Actions include
enable, disable, reload all, and uninstall with confirmation.

### Macros

Opened by `:macros`.

It filters macro registers. Items show register, status, chord count, and a
sequence preview. Actions include replay, record or stop recording, mark last,
and clear with confirmation.

### Keybindings

Opened by bare `:keybinds`.

It filters keybindings. Items show shortcut, action, context, and source. This
session is search-result oriented and does not need mutation actions in this
slice.

## Rendering

The command line should show the active session identity and query.

Examples:

```text
:theme green
Plugins: todo
Macros: @a
Keybindings: save
/needle
```

The dock should render a `PromptSurface`:

- command/file/buffer sessions can keep the current compact completion layout;
- plugin and macro sessions use a list-with-preview layout;
- keybinding sessions use a search-results layout;
- search keeps its small match-status layout unless a fuller search result
  surface is introduced later.

This means layout is still allowed to vary by session, but the input lifecycle
does not vary.

## Migration Strategy

Implement this as a refactor, not a visual redesign.

1. Introduce prompt-session types while keeping current behavior.
2. Move command/file/search/buffer prompt behavior onto the new session model.
3. Move picker item/action semantic types under the prompt-session surface.
4. Rehome plugin, macro, and keybinding builders from `Pickers.fs` into prompt
   session builders.
5. Replace `Model.Picker` with prompt sessions.
6. Delete the separate picker key-routing path.
7. Delete `PickerTypes.fs` and `Pickers.fs` once no production or test code
   references them.

During the migration, avoid changing plugin persistence, macro persistence, the
public plugin API, or keybinding file semantics.

## Testing

Tests should cover:

- `:plugins`, `:macros`, and bare `:keybinds` open named prompt sessions;
- opening a named session clears command text and uses a fresh query;
- Escape closes every prompt session consistently;
- typing filters command, file, buffer, plugin, macro, and keybinding sessions
  through the same edit path;
- up/down selection behavior is shared;
- action lookup ignores disabled actions;
- destructive actions require repeated confirmation on the same selected item;
- changing the filter clears pending confirmation;
- plugin enable/disable persists config and rescans plugins;
- macro replay and record actions close the prompt session where appropriate;
- search still previews matches while typing;
- theme preview still works in command sessions;
- render tests verify session title, query, items, preview, footer, and empty
  states.

Verification should include focused prompt-session tests, render snapshots for
the new surfaces, `just test`, and `git diff --check`.

## Out Of Scope

- New plugin persistence behavior.
- Macro persistence.
- Editing keybindings from the keybinding session.
- User-configurable layouts.
- A general table layout engine.
- A full command language for nested sessions.
