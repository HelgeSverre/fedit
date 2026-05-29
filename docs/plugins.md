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

| What plugins can do                                      | What's not in scope yet                                    |
| -------------------------------------------------------- | ---------------------------------------------------------- |
| Register named commands (`:wc`, `:todocount`, …)         | Async handlers — runs on the UI thread, target < 50 ms     |
| Bind chords to commands (`Ctrl+T`, `Alt+x`, `F5`, …)     | Custom panels, themes, file types, LSP                     |
| Read text, cursor, file path, workspace root             | Plugin sandbox / capability restriction (full trust)       |
| Emit `Notify`, `InsertText`, `MoveCursor`, `OpenFile`, … | Cross-language plugins — F# only                           |
| Chain into built-ins via `RunCommand "open foo.fs"`      | Persistent per-plugin enable/disable (rescan only for MVP) |

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

The `examples/wordcount/` plugin you just installed is 12 lines:

```fsharp
namespace Wordcount

open Fedit.PluginApi

module Plugin =
    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "wc"
              Usage = "wc"
              Summary = "Count words in the active buffer."
              Run =
                fun ctx ->
                    let n =
                        ctx.ActiveBuffer.Text.Split(
                            [| ' '; '\t'; '\n'; '\r' |],
                            System.StringSplitOptions.RemoveEmptyEntries
                        )
                        |> Array.length

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

The folder name **must** match the `name` field in `plugin.json`.
Folders without a manifest are silently skipped. Folders whose manifest
fails to parse show up in `:plugin list` as `FAIL: <reason>`.

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
| `name`          | yes      | Kebab-case. Must match the folder name on disk.                                             |
| `version`       | yes      | Semver. Informational only in MVP.                                                          |
| `apiVersion`    | yes      | Plugin-API major version. MVP is `"1"`; mismatches refuse to load.                          |
| `description`   | no       | Shown in `:plugin list`.                                                                    |
| `author`        | no       | Informational.                                                                              |
| `homepage`      | no       | Informational.                                                                              |
| `entryAssembly` | yes      | DLL filename produced by `dotnet build`. Loaded from `bin/Release/net10.0/<entryAssembly>`. |
| `entryType`     | yes      | Fully-qualified F# module containing a `register : IPluginHost -> unit` function.           |

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
      Selection: (CursorPosition * CursorPosition) option }   // None in MVP

type WorkspaceView = { RootPath: string }

type PluginContext =
    { ActiveBuffer: BufferView
      AllBuffers: BufferView list
      Workspace: WorkspaceView }
```

`PluginContext` is a snapshot. Plugins never see live state — the host
builds a fresh context each time the command fires.

### Changing the world

Plugin commands return a `PluginAction list`. The host applies each
action in order. Pick the right action for the effect you want:

| Action                               | Use it when                                       | Example                          |
| ------------------------------------ | ------------------------------------------------- | -------------------------------- |
| `Notify(Info, "…")`                  | Reporting a result; no side effect on the buffer  | Word count, TODO summary         |
| `InsertText "abc"`                   | Add text at the cursor                            | Timestamp, UUID, snippet         |
| `ReplaceSelection "abc"`             | Replace the current selection (insert if no sel.) | Surround, reformat, kebabify     |
| `MoveCursor { Line = …; Column = …}` | Jump the cursor (1-based coords)                  | "Next TODO", "match brace"       |
| `OpenFile "path"`                    | Open a file relative to the workspace root        | "Jump to definition"             |
| `SaveActiveBuffer`                   | Trigger the same save path as `:write`            | Auto-save after a rewrite        |
| `RunCommand "open foo.fs"`           | Chain into a built-in (or another plugin) by name | Open the file you just generated |
| `SetClipboard "abc"`                 | Copy text to the system clipboard                 | "Yank current line"              |

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
- Plugin keybindings can shadow built-in editor keys (e.g. take over
  `Ctrl+S`). Use sparingly and document the intent in your README.

## Five reference plugins

The repo ships five plugins under [`examples/`](../examples) — each
demonstrates a different combination of actions.

| Plugin                                 | Command      | Actions used                                             | What it shows                                 |
| -------------------------------------- | ------------ | -------------------------------------------------------- | --------------------------------------------- |
| [`wordcount`](../examples/wordcount)   | `:wc`        | `Notify`                                                 | Smallest possible plugin                      |
| [`journal`](../examples/journal)       | `:journal`   | `InsertText` + `Notify`                                  | Insert at cursor, confirm via dock            |
| [`todo-count`](../examples/todo-count) | `:todocount` | `Notify`                                                 | Walk the workspace via `Workspace.RootPath`   |
| [`todo-list`](../examples/todo-list)   | `:todolist`  | `Notify` (multi-line)                                    | Dock multi-line output as a file:line listing |
| [`todo-next`](../examples/todo-next)   | `:todonext`  | `MoveCursor` + `Notify` + `RegisterKeybinding(Ctrl 't')` | Read cursor, jump elsewhere, bind to a chord  |

## The `:plugin` command

| Invocation                      | Behavior                                                                |
| ------------------------------- | ----------------------------------------------------------------------- |
| `:plugin list`                  | Show plugins with status (`ok`, `disabled`, `FAIL: ...`).               |
| `:plugin install <url-or-path>` | Detect kind, install, rescan. Sources: folder path, git URL, `.zip`.    |
| `:plugin remove <name>`         | Delete the plugin folder and re-scan.                                   |
| `:plugin reload`                | Re-scan disk; rebuilds stale plugins.                                   |
| `:plugin validate <path>`       | Parse-only check: does the manifest read, what would be registered.     |
| `:plugin enable <name>`         | (MVP no-op beyond rescan — per-plugin disable persistence lands in v2.) |
| `:plugin disable <name>`        | (Same as above.)                                                        |

Tab completion suggests verbs first, then arguments.

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

- **Per-plugin disable persistence** — `:plugin disable` rescans but
  doesn't write to `~/.config/fedit/config.json`.
- **Async / long-running commands** — handlers block the UI thread.
  Plugins doing real I/O need to keep work brief.
- **`Selection` in `BufferView`** — always `None`. Plugins reading
  selection should fall back to `ActiveBuffer.Text` and the cursor
  position.
- **Per-plugin settings** — no `IPluginHost.Config<T>()` yet. Plugins
  that need configuration can read their own JSON file relative to
  `~/.config/fedit/plugins/<name>/`.
- **Workspace mutation** — no create/delete file API. The workaround
  is to `RunCommand "open <path>"` (which loads or creates a buffer
  for `<path>`).
- **API v2** — when `apiVersion` ticks, v1 plugins keep working via
  the host shipping `Fedit.PluginApi.v1.dll` alongside the new one.
- **Configurable keybindings (in design)** — a forthcoming user keymap
  (`~/.config/fedit/keybinds`) will let users bind any stroke — including
  `Ctrl+Shift`, F-keys, and multi-key sequences — to plugin commands,
  independent of the v1 `KeyChord`. Two changes will affect plugins: the user
  keymap resolves **before** plugin-registered chords (so a user can always
  reclaim a chord a plugin grabbed), and the expanded key decoder will make
  the currently-dormant `Alt`/`CtrlShift`/`F` `KeyChord` variants actually
  fire. `RegisterKeybinding` and `KeyChord` themselves stay unchanged. See
  [the keybindings spec §6.7](superpowers/specs/2026-05-29-keybindings-spec.md#67-plugin-system--pluginapi-boundary).

If your use case hits one of these walls, open an issue at
[github.com/HelgeSverre/fedit](https://github.com/HelgeSverre/fedit) —
the API is still soft enough to bend.
