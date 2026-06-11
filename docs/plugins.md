# fedit plugins

Third-party F# plugins extend fedit with new commands and keybindings.
Plugins live in `~/.config/fedit/plugins/<name>/`, are built lazily by
the host on startup, and register themselves through a small contract
defined in [`Fedit.PluginApi`](../src/Fedit.PluginApi).

This page is the authoring guide. For the marketing-tinted introduction
see [the plugins page on fedit.dev](https://fedit.dev/plugins).

> **Trust model.** Plugins run as full .NET code with no sandbox — they
> can read any file, open any socket, and run any process. Treat
> installation like installing a shell tool: only run plugins from
> sources you trust.

## At a glance

| What plugins can do                                       | What's not in scope yet                                |
| --------------------------------------------------------- | ------------------------------------------------------ |
| Register named commands (`:wc`, `:todocount`, …)          | Async handlers — runs on the UI thread, target < 50 ms |
| Bind chords to commands (`Ctrl+T`, `Alt+x`, `F5`, …)      | Custom panels, themes, file types, LSP                 |
| Read text, cursor, file path, workspace root + file index | Plugin sandbox / capability restriction (full trust)   |
| Emit `Notify`, `InsertText`, `MoveCursor`, `OpenFile`, …  | Cross-language plugins — F# only                       |
| Chain into built-ins via `RunCommand "open foo.fs"`       | Per-plugin settings — no `IPluginHost.Config<T>()` yet |

## Five-minute quickstart

```bash
mkdir -p ~/.config/fedit/plugins
cp -R examples/wordcount ~/.config/fedit/plugins/
./fedit .
```

Inside fedit:

1. `Ctrl+P` opens the prompt.
2. Type `plugin reload` — the host rescans, builds, and loads.
3. Type `wc` — the active buffer's word count appears in the dock.

The `examples/wordcount/` plugin you just installed fits on one screen:

```fsharp
namespace Wordcount

open Fedit.PluginApi

/// Counts whitespace-separated tokens in the active buffer.
/// Demonstrates: reading `ActiveBuffer.Text`; returning a single `Notify` action.
module Plugin =
    let private countWords (text: string) =
        if System.String.IsNullOrWhiteSpace text then
            0
        else
            text.Split([| ' '; '\t'; '\n'; '\r' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.length

    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "wc"
              Usage = "wc"
              Summary = "Count words in the active buffer."
              Run =
                fun ctx ->
                    let n = countWords ctx.ActiveBuffer.Text
                    [ Notify(Info, $"{n} words") ] }
```

That's the whole authoring surface — a `register` function, one or
more `RegisterCommand` calls, and an optional `RegisterKeybinding`.

## Plugin lifecycle

```
  on fedit startup
        │
        ▼
  scan ~/.config/fedit/plugins/        ← Plugins.discover
        │
        ▼
  for each <name>/ with plugin.json    ← Plugins.tryParseManifest
        │
        ▼
  is bin/Release/net10.0/<dll>         ← Plugins.isBuildStale
   missing or older than any .fs?
        │
        ▼ (yes)
  generate plugin.generated.fsproj     ← Plugins.ensureFsproj
  dotnet build -c Release              ← Plugins.runDotnetBuild
        │
        ▼
  load DLL in isolated ALC             ← AssemblyLoadContext
  reflect entryType.register           ← Plugins.resolveRegister
  call register(collector)             ← collector implements IPluginHost
        │
        ▼
  merge commands + keybindings into    ← PluginRegistry in Model
  the global PluginRegistry
```

Triggered manually via `:plugin reload` after editing a plugin.

## Anatomy of a plugin folder

```
~/.config/fedit/plugins/wordcount/
├── plugin.json          # manifest (required)
├── Plugin.fs            # source (required; multiple .fs allowed)
├── plugin.fsproj        # optional — host generates one if absent
└── bin/Release/net10.0/ # build output (auto-managed)
```

Match the folder name to the `name` field in `plugin.json` — a
convention, not an enforced rule: discovery keys plugins by manifest
name, and `:plugin install` normalizes the folder to the declared name
for you. Folders without a manifest are silently skipped. Folders whose
manifest fails to parse show up in `:plugin list` as `FAIL: <reason>`.

### `plugin.json`

```json
{
    "name": "wordcount",
    "version": "0.1.0",
    "apiVersion": "1",
    "description": "Adds :wc to count words in the active buffer.",
    "author": "fedit maintainers",
    "homepage": "https://github.com/example/fedit-wordcount",
    "entryAssembly": "wordcount.dll",
    "entryType": "Wordcount.Plugin"
}
```

| Field           | Required | Notes                                                                                       |
| --------------- | -------- | ------------------------------------------------------------------------------------------- |
| `name`          | yes      | Kebab-case. By convention matches the folder name on disk.                                  |
| `version`       | yes      | Semver. Informational only in MVP.                                                          |
| `apiVersion`    | yes      | Plugin-API major version. MVP is `"1"`; mismatches refuse to load.                          |
| `description`   | no       | Shown in `:plugin list`.                                                                    |
| `author`        | no       | Informational.                                                                              |
| `homepage`      | no       | Informational.                                                                              |
| `entryAssembly` | yes      | DLL filename produced by `dotnet build`. Loaded from `bin/Release/net10.0/<entryAssembly>`. |
| `entryType`     | yes      | Fully-qualified F# module containing a `register : IPluginHost -> unit` function.           |

Compatibility within `apiVersion` `"1"` is **append-only**: new
`PluginAction` cases are appended at the end of the union (never
inserted or reordered — union tags are binary), and new fields land
only on host-constructed records (`WorkspaceView`, `PluginContext`,
`BufferView`). A plugin compiled against an older 1.x contract keeps
loading and running; one compiled against a newer contract that
constructs a case the host doesn't know simply won't load. Anything
that breaks this rule bumps `apiVersion`.

### Entry contract

The host calls one function per plugin at load time:

```fsharp
val register : IPluginHost -> unit
```

It runs synchronously, exactly once, and must not throw. An exception
marks the plugin as `Failed` and the host carries on. Any registrations
the plugin made before the throw are dropped (no partial state).

## The API surface

All types live in the `Fedit.PluginApi` namespace and ship as a
separate assembly (`Fedit.PluginApi.dll`) so plugin authors depend on a
minimal, stable surface — not on the editor internals.

### Reading the world

```fsharp
type CursorPosition = { Line: int; Column: int }   // 1-based

type BufferView =
    { Id: int
      Name: string
      FilePath: string option
      Text: string
      Cursor: CursorPosition
      Selection: (CursorPosition * CursorPosition) option }   // (start, end) when a selection is active

type WorkspaceView =
    { RootPath: string
      SelectedPath: string option   // sidebar's selected entry (absolute path)
      Files: string list }          // root-relative paths of every indexed file,
                                    // sorted tree order — the file picker's list.
                                    // Empty until the workspace scan completes.

type PluginContext =
    { ActiveBuffer: BufferView
      AllBuffers: BufferView list
      Workspace: WorkspaceView }
```

`PluginContext` is a snapshot. Plugins never see live state — the host
builds a fresh context each time the command fires.

These context records are host-constructed: plugins only ever receive
them and never build one, which is what lets the host append new fields
(like `SelectedPath` and `Files`) without breaking plugins compiled
against an older contract.

### Changing the world

Plugin commands return a `PluginAction list`. The host applies each
action in order. Pick the right action for the effect you want:

| Action                               | Use it when                                                                                                                                                                                                      | Example                                          |
| ------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------ |
| `Notify(Info, "…")`                  | Reporting a result; no side effect on the buffer                                                                                                                                                                 | Word count, TODO summary                         |
| `InsertText "abc"`                   | Add text at the cursor                                                                                                                                                                                           | Timestamp, UUID, snippet                         |
| `ReplaceSelection "abc"`             | Replace the current selection (insert if no sel.)                                                                                                                                                                | Surround, reformat, kebabify                     |
| `MoveCursor { Line = …; Column = …}` | Jump the cursor (1-based coords)                                                                                                                                                                                 | "Next TODO", "match brace"                       |
| `SelectRange(anchor, cursor)`        | Select a range; caret lands on `cursor`                                                                                                                                                                          | "Select word", "expand to line"                  |
| `OpenFile "path"`                    | Open an existing file (workspace-root relative)                                                                                                                                                                  | "Jump to definition"                             |
| `SaveActiveBuffer`                   | Trigger the same save path as `:write`                                                                                                                                                                           | Auto-save after a rewrite                        |
| `RunCommand "open foo.fs"`           | Chain into a built-in command by name                                                                                                                                                                            | Open the file you just generated                 |
| `SetClipboard "abc"`                 | Copy text to the system clipboard                                                                                                                                                                                | "Yank current line"                              |
| `OpenFilePreview "path"`             | Open into the preview slot (the sidebar's Space behavior); an already-open file is activated instead                                                                                                             | Peek at a search hit                             |
| `RevealPath "path"`                  | Expand ancestors + select in the sidebar, showing it without stealing focus; paths outside the workspace are a no-op                                                                                             | Sidebar follows plugin output                    |
| `ReplaceRange(from, to_, text)`      | Replace the span between two 1-based positions as a single edit — one undo entry. Ends swap when `from` is after `to_`; out-of-range coords clamp. Cursor lands after the inserted text; any selection collapses | Formatters, codemods, surround                   |
| `ClearSelection`                     | Collapse the selection to a caret (no-op without one)                                                                                                                                                            | Tidy up after `SelectRange`                      |
| `DeleteSelection`                    | Delete the selected text as one undo entry (no-op without one)                                                                                                                                                   | Cut without replacement text                     |
| `SwitchBuffer id`                    | Activate the buffer with this `BufferView.Id`; unknown ids raise an error notification                                                                                                                           | Act across `AllBuffers`                          |
| `NewBuffer(name, text)`              | Create a scratch buffer (empty `name` defaults to `"plugin"`) and make it active; later actions in the same list target it                                                                                       | Show generated output as a real, editable buffer |

Two coordination notes:

- **Batched `ReplaceRange`s see post-edit coordinates.** Each edit
  shifts the line/column positions of everything after it, and later
  actions in the list run against the already-edited buffer — emit
  multiple edits bottom-up so earlier spans stay where the snapshot
  said they were.
- **Editing a previewed buffer promotes it.** A buffer opened via
  `OpenFilePreview` sits in the preview slot and is replaced by the
  next preview — unless it's edited, which promotes it to a regular
  buffer.

### Registration

```fsharp
type PluginCommand =
    { Name: string
      Usage: string
      Summary: string
      Run: PluginContext -> PluginAction list }

type KeyChord =
    | Char of char     // RESERVED — text input; registrations rejected
    | Ctrl of char
    | Alt of char
    | CtrlShift of char
    | F of int         // F1..F12

type IPluginHost =
    abstract member RegisterCommand: PluginCommand -> unit
    abstract member RegisterKeybinding: chord: KeyChord * commandName: string -> unit
    abstract member Log: message: string -> unit
```

### Conflict policy

- Plugin commands cannot shadow built-ins. A plugin trying to register
  `open` is silently dropped; the built-in wins.
- Two plugins registering the same name: first wins (directory
  enumeration order). The loser surfaces in `:plugin list` via the
  registry's `Conflicts` field.
- Plugin keybindings only fire when the editor pane has focus (not in
  the prompt or sidebar) for MVP.
- **The user keymap takes precedence over plugin keybindings.** A chord
  bound by the built-in defaults or the user's `~/.config/fedit/keybinds`
  file resolves first; a plugin's `RegisterKeybinding` only fires when no
  keymap binding claims that chord. A plugin can therefore no longer shadow
  a built-in or user key. To bind a plugin command yourself, add a line to
  the keybinds file with the `run-plugin:<source>/<name> [arg]` action — e.g.
  `editor ctrl+k ctrl+w = run-plugin:wordcount/wc` — which wins over both the
  plugin's own chord and any built-in. See the
  [Keybindings section of the README](../README.md#keybindings).

## Six reference plugins

The repo ships six plugins under [`examples/`](../examples) — each
demonstrates a different combination of actions.

| Plugin                                 | Command                    | Actions used                                                                                                   | What it shows                                                  |
| -------------------------------------- | -------------------------- | -------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| [`wordcount`](../examples/wordcount)   | `:wc`                      | `Notify`                                                                                                       | Smallest possible plugin                                       |
| [`journal`](../examples/journal)       | `:journal`                 | `InsertText` + `RevealPath` + `Notify`                                                                         | Insert at cursor; the sidebar follows the stamped file         |
| [`todo-count`](../examples/todo-count) | `:todocount`               | `Notify`                                                                                                       | Walk the workspace via `Workspace.RootPath`                    |
| [`todo-list`](../examples/todo-list)   | `:todolist`                | `NewBuffer` + `Notify`                                                                                         | Scan `Workspace.Files`, report into an editable scratch buffer |
| [`todo-next`](../examples/todo-next)   | `:todonext`                | `MoveCursor` + `SwitchBuffer` + `Notify` + `RegisterKeybinding(Ctrl 't')`                                      | Jump to the next match, continuing across every open buffer    |
| [`jot`](../examples/jot)               | `:jot` `:jotdone` `:jotgo` | `NewBuffer` + `SwitchBuffer` + `MoveCursor` + `InsertText` + `ReplaceRange` + `RevealPath` + `OpenFilePreview` | Session scratchpad exercising the full post-MVP action set     |

## The `:plugin` command

| Invocation                      | Behavior                                                             |
| ------------------------------- | -------------------------------------------------------------------- |
| `:plugin list`                  | Show plugins with status (`ok`, `disabled`, `FAIL: ...`).            |
| `:plugin install <url-or-path>` | Detect kind, install, rescan. Sources: folder path, git URL, `.zip`. |
| `:plugin remove <name>`         | Delete the plugin folder and re-scan.                                |
| `:plugin reload`                | Re-scan disk; rebuilds stale plugins.                                |
| `:plugin validate <path>`       | Parse-only check: does the manifest read, what would be registered.  |
| `:plugin enable <name>`         | Remove the plugin from `disabledPlugins` in config, persist, rescan. |
| `:plugin disable <name>`        | Add the plugin to `disabledPlugins` in config, persist, rescan.      |

Tab completion suggests verbs first, then arguments.

`:plugins` (plural, no argument) opens the plugin manager picker — the
same list/enable/disable surface as a navigable dock session.

## Distributing a plugin

A plugin is a folder. Suggested layout for a public repo:

```
fedit-plugin-<name>/
├── plugin.json
├── Plugin.fs
├── plugin.fsproj            # optional but improves repo readability
├── README.md                # what it does, key choices, screenshots
└── LICENSE
```

The naming convention `fedit-plugin-<name>` is a soft convention — it
makes plugins searchable on GitHub.

Users install with:

```
:plugin install https://github.com/<you>/fedit-plugin-<name>
```

The host runs `git clone --depth 1` into a staging directory, validates
the manifest, then moves the folder into place under the plugin's
declared `name`. Zip and folder sources work the same way.

## Debugging

- `./fedit --log /tmp/fedit.log .` captures every `Msg` and `Effect`.
  Plugin scan results, build output, and command dispatches all land
  there in order.
- `host.Log "message"` from inside your plugin appears as
  `[plugin:<name>] message` in the same log.
- Build failures surface in `:plugin list` as
  `FAIL: dotnet build failed (exit 1): <stdout>\n<stderr>`. Run the
  failing command directly in the plugin folder to iterate faster:

    ```bash
    cd ~/.config/fedit/plugins/<name>
    dotnet build -c Release
    ```

## Limitations and roadmap

This is the MVP. Concretely deferred to v2:

- **Async / long-running commands** — handlers block the UI thread.
  Plugins doing real I/O need to keep work brief.
- **Per-plugin settings** — no `IPluginHost.Config<T>()` yet. Plugins
  that need configuration can read their own JSON file relative to
  `~/.config/fedit/plugins/<name>/`.
- **Workspace mutation** — no create/delete file API. The workaround
  is to create or modify the file yourself, then `RunCommand "open <path>"`
  to open an existing file in a buffer.
- **API v2** — when `apiVersion` ticks, v1 plugins keep working via
  the host shipping `Fedit.PluginApi.v1.dll` alongside the new one.
- **Configurable keybindings** — the user keymap
  (`~/.config/fedit/keybinds`) lets users bind strokes to plugin commands,
  independent of the v1 `KeyChord`. The user keymap resolves **before**
  plugin-registered chords, so a user can always reclaim a chord a plugin
  grabbed. `RegisterKeybinding` supports `Ctrl`, `Alt`, `CtrlShift`, and
  `F` chords; plain printable `Char` chords are reserved for text entry and
  are rejected.

If your use case hits one of these walls, open an issue at
[github.com/HelgeSverre/fedit](https://github.com/HelgeSverre/fedit) —
the API is still soft enough to bend.
