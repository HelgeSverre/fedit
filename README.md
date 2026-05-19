<p>
  <img src="brand/symbol.svg" alt="" width="48" align="left" />
</p>

# fedit

**Edit files in the terminal. Small. Written in F#.**

[![CI](https://github.com/HelgeSverre/fedit/actions/workflows/ci.yml/badge.svg)](https://github.com/HelgeSverre/fedit/actions/workflows/ci.yml)
[![.NET SDK](https://img.shields.io/badge/dotnet-9%2B-blue.svg)](https://dotnet.microsoft.com/download)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
![Project Type](https://img.shields.io/badge/language-F%23-blue.svg)

Opens a workspace, shows a file tree, edits files, saves to disk. Multi-buffer, undo/redo, find, command palette, system clipboard. Seven color themes. Persists the last 20 opened files and your theme across sessions.

Brand assets and theme spec in [`brand/`](brand/). Marketing site in [`website/`](website/) — run with `just website::dev`.

## Requirements

- .NET SDK 9 (pinned via `global.json` to `9.0.312` with `rollForward: latestFeature`, so any `9.0.x` patch ≥ 312 works)
- `just` for the convenience recipes in `justfile`

This repository includes a local `.dotnet` SDK directory. The `fedit` wrapper script and the `justfile` recipes prepend `.dotnet` to `PATH`, so the local SDK is used when it is present.

## Quick Start

Run the editor in the current directory using the included `fedit` shell shim (a thin wrapper that pins the local `.dotnet` SDK on `$PATH` and invokes `dotnet run`):

```shell
./fedit .
```

Run it directly through .NET:

```shell
dotnet run --project src/Fedit/Fedit.fsproj -- .
```

Or use the just recipe:

```shell
just run .
```

Pass a file or directory path as the first argument. If no path is provided, `fedit` uses the current working directory.

### Command-line flags

- `--log <path>`: Append a UTC-timestamped trace of every `Msg` and `Effect` to `<path>`. Useful for debugging without polluting the TUI.

## Build Commands

Build the whole solution (both projects):

```shell
dotnet build Fedit.slnx
```

Or just the editor:

```shell
dotnet build src/Fedit/Fedit.fsproj
```

Run the editor:

```shell
dotnet run --project src/Fedit/Fedit.fsproj -- .
```

Clean generated output:

```shell
dotnet clean Fedit.slnx
rm -rf src/Fedit/bin src/Fedit/obj tests/Fedit.Tests/bin tests/Fedit.Tests/obj
```

## Justfile Recipes

List available recipes:

```shell
just
```

Start the app under `dotnet watch` (re-launches on source changes; expect a brief flash as the alt-screen tears down and re-enters on each rebuild):

```shell
just dev .
```

Build the project:

```shell
just build
```

Run the editor:

```shell
just run .
```

Clean compiled output:

```shell
just clean
```

Format F# sources with [fantomas](https://fsprojects.github.io/fantomas/) (restored from `.config/dotnet-tools.json`):

```shell
just format
```

Verify formatting without writing — fails if anything would change:

```shell
just lint
```

Run the xUnit test suite (Tier 1 coverage of `PieceTable`, `Buffer`, `Commands`, `Workspace`, `Editor.update`, plus FsCheck properties on the piece table):

```shell
just test
```

Run lint, build, and test together as a single pre-commit gate:

```shell
just check
```

Publish a self-contained single-file binary and install it to `~/.local/bin` (override with `just install path/to/bin`):

```shell
just install
```

Remove a previously installed binary:

```shell
just uninstall
```

## Using the Editor

Global shortcuts:

- `Ctrl+P`: Open the command bar.
- `Ctrl+F`: Find in the active buffer.
- `Ctrl+B`: Focus the file tree.
- `Ctrl+E`: Focus the editor.
- `Ctrl+S`: Save the active buffer.
- `Ctrl+R`: Reload the workspace tree.
- `Ctrl+Q`: Quit (prompts once if buffers are dirty; press again to discard).
- `Ctrl+Z`: Undo.
- `Ctrl+Y`: Redo.

Editor keys:

- Arrow keys, `Home`, `End`, `PageUp`, and `PageDown` move the cursor.
- `Alt+Left` / `Alt+Right` move the cursor by word.
- `Ctrl+Backspace` / `Ctrl+Delete` delete the previous / next word.
- `Shift+Arrow`, `Shift+Home`, `Shift+End` extend the text selection.
- `Ctrl+A` selects the whole buffer.
- `Ctrl+C` / `Ctrl+X` copy or cut the current selection to the system clipboard (via `pbcopy` on macOS, `xclip` on Linux).
- `Ctrl+V` pastes from the system clipboard.
- `Tab` indents the current line.
- `Shift+Tab` unindents the current line.
- `Enter`, `Backspace`, and `Delete` edit text normally; with a selection, they replace it.

Find keys (after `Ctrl+F`):

- Type to extend the query; matches highlight live and the cursor jumps to the first hit.
- `Enter` or `Down` advances to the next match; `Up` goes to the previous.
- `Backspace` shortens the query; on an empty query it closes the prompt.
- `Escape` closes the prompt and clears the highlights.

File tree keys:

- `Up` and `Down` move selection.
- `PageUp` and `PageDown` jump ten entries at a time.
- `Home` and `End` jump to the first or last visible entry.
- `Left` collapses the selected directory or moves to its parent.
- `Right` expands the selected directory.
- `Enter` opens a file or toggles a directory.
- `Escape` returns focus to the editor.

Command bar keys:

- Type to filter; the dock panel shows matching commands or workspace paths.
- `Tab` and `Shift+Tab` cycle through completions.
- `Up` and `Down` walk through recent command history (up to 20 entries).
- `Enter` runs the parsed command, or applies the highlighted completion when the command is incomplete.
- `Left`, `Right`, `Home`, `End`, `Backspace`, and `Delete` edit the input.
- `Escape` closes the command bar without running anything.

Command bar commands:

- `open <path>`: Open a file from the current workspace. Activates the existing buffer if the file is already open.
- `write`: Save the active buffer.
- `writeas <path>`: Save the active buffer to another path.
- `quit`: Exit the editor.
- `sidebar`: Toggle the sidebar.
- `tree`: Focus the file tree.
- `editor`: Focus the editor.
- `reload`: Reload the workspace tree.
- `next`: Activate the next open buffer.
- `prev`: Activate the previous open buffer.
- `theme <name>`: Switch the accent color. Tab through `green` (default), `blue`, `orange`, `cyan`, `teal`, `yellow`, or `red`; the UI live-previews each highlight as you cycle. The choice persists to `~/.config/fedit/config.json` and is restored on next launch.
- `recent <path>`: Pick a recently opened file. Tab to cycle through the last 20 files; the list persists in the same config file.
- `buffers <id-or-name>`: Switch to an open buffer by numeric id or name. Completion shows `{id} {name}` with the file path as detail.
- `help`: Show command help in the dock panel.
- `<line>` / `<line>:<column>`: Jump the cursor to an absolute 1-based position. `:42` goes to line 42 column 1; `:100:6` goes to line 100 column 6. Out-of-range values clamp to the end of the buffer / line.

## Configuration

`fedit` reads `~/.config/fedit/config.json` at startup. The file is created automatically the first time the editor persists state (`:theme` or opening a file updates `recent`). Hand-edited values are preserved on every save — unknown keys you add to the file are kept intact.

| Key               | Type     | Default  | Range     | What it controls                                                                                  |
| ----------------- | -------- | -------- | --------- | ------------------------------------------------------------------------------------------------- |
| `theme`           | string   | `cyan`   | —         | Accent palette name (bundled or user theme from `~/.config/fedit/themes/*.json`).                 |
| `recent`          | string[] | `[]`     | up to 20  | Recently opened files; managed automatically.                                                     |
| `completionLimit` | int      | `8`      | 1–64      | Max items considered for `:open`, `:writeas`, `:recent`, `:buffers` completions.                  |
| `sidebarIndent`   | int      | `2`      | 0–16      | Spaces per depth level in the file tree.                                                          |
| `sidebarWidth`    | int      | `30`     | 10–200    | Initial sidebar width in columns.                                                                 |
| `dockHeight`      | int      | `5`      | 1–40      | Dock panel height in rows (used for the completion list and `:help`).                             |
| `wordMotion`      | string   | `wordEnd` | `wordEnd` or `nextWordStart` | Where `Alt+Right` / `Ctrl+Delete` land — end of current word (default) or start of next word (vim `w`). |

Changes take effect on next launch. Out-of-range values clamp to the nearest valid bound rather than failing.

## How It Works

The project is an executable defined by `src/Fedit/Fedit.fsproj`, with sources split across 14 numbered `.fs` files under `namespace Fedit` (see `<Compile Include="…">` entries in the fsproj for the canonical order). `Program.fs` is the entry-point shell; the actual logic lives in `Primitives.fs` → `PieceTable.fs` → `Buffer.fs` → `Workspace.fs` → `Themes.fs` → `Commands.fs` → `Model.fs` → `Editor.fs` → `Screen.fs` → `Renderer.fs` → `Input.fs` → `View.fs` → `Runtime.fs`. The test project lives in `tests/Fedit.Tests/` and the `Fedit.slnx` solution at the repo root ties both together. Startup reads the first non-flag command-line argument as the workspace root. If no argument is provided, it uses the current directory.

At runtime, `fedit` scans the workspace into a tree model and skips `.DS_Store`, `.git`, `.dotnet`, `bin`, and `obj`. A `FileSystemWatcher` is installed on the same workspace root so external edits, creations, deletions, and renames trigger a debounced rescan (300ms) without `Ctrl+R`. The UI keeps a model containing the workspace tree, open buffers, focus target, terminal size, notifications, and panel state.

Text buffers are stored with a piece table. The original file contents stay in one string, inserted text is appended to another string, and the visible document is represented as a list of pieces. This keeps inserts and deletes local to the piece list while preserving enough state for undo and redo snapshots. Each open buffer keeps its own undo and redo stacks, cursor position, and viewport.

Files are read as UTF-8. The line ending of the loaded file (`LF` or `CRLF`) is detected and reused on save; the buffer always works in `\n` form internally. Saving writes UTF-8 without a byte-order mark.

The UI is split into a sidebar (file tree), an editor pane with a line-number gutter, a status line, a dock panel that shows contextual help or completions, and a single-line command bar at the bottom. The status line reports the current focus (`TREE`/`EDIT`/`CMD`/`FIND`), active file path, dirty marker, cursor position with total line count (`Ln 12/238`), the line-ending style (`LF` or `CRLF`), the count of open buffers, and the most recent notification.

Themes are pure accent palettes — the dock title, status bar, selection highlight, and current-line gutter all swap together while the grayscale chrome stays constant across themes. The chosen theme, the most recent 20 opened files, and the runtime tunables described in [Configuration](#configuration) all live in `~/.config/fedit/config.json` and are restored on the next launch; the default theme is `green` (phosphor green, the brand accent).

### Architecture

`fedit` uses an Elm-style update loop. The `Model` is pure data. `Editor.update` is a pure function that takes a `Msg` and returns a new model plus a list of effects. `runEffect` is the only impure code path — it does file I/O and folds its result back into the loop as another `Msg`. `Layout.render` projects the model to a `Screen` (a `Cell[,]` grid), and `Renderer.render` writes that grid out as ANSI escapes.

```
                        +----------------------+
                        |        Model         |
                        |  workspace . tree    |
                        |  buffers + cursors   |
                        |  focus, theme,       |
                        |  panels, terminal    |
                        +-----+----------+-----+
                              |          |
              read by         |          |     read by
        +---------------------+          +-----------------------+
        |                                                        |
        v                                                        v
+-----------------+                                    +-------------------+
|  Editor.update  |                                    |   Layout.render   |
|     (pure)      |                                    |      (pure)       |
+--------+--------+                                    +---------+---------+
         |                                                       |
   (model', effects)                                          Screen
         |                                                       |
         v                                                       v
+-----------------+                                    +-------------------+
|    runEffect    |                                    |  Renderer.render  |
|    (impure)     |                                    |   ANSI escapes    |
|  ScanWorkspace  |                                    +---------+---------+
|  LoadFile       |                                              |
|  SaveBuffer     |                                              v
+--------+--------+                                       terminal output
         |
         v
        Msg  ----------> dispatch <---------- KeyPressed / Resize
                            ^                       (main loop)
                            |
                       feeds back
```

`dispatch` is the integration point: every effect produces a `Msg`, which goes back through `Editor.update`, which may yield more effects, until the queue is drained. Effects run on the .NET thread pool via `Task.Run` and post their result `Msg` into a `ConcurrentQueue`; the main loop drains the queue each tick. `ScanWorkspace` and `LoadFile` each carry a single `CancellationTokenSource` so a second instance dropping the previous result Msg. Large workspaces and large files therefore never freeze input.

### Lifecycle

Startup is sequential: parse arg, set up the console, build the initial model, drain its startup effects (the workspace scan), then switch to the alternate screen and enter the loop. The loop polls — there is no event source or second thread.

```
  $ fedit .
       |
       v
  +-----------+     +-----------------+     +-----------------------+
  |   main    |---->|   Runtime.run   |---->|  Console setup        |
  | argv[0]   |     |    rootPath     |     |  UTF-8 + Ctrl-C input |
  +-----------+     +--------+--------+     +-----------+-----------+
                             |                          |
                             |  +-----------------------+
                             |  |
                             v  v
                    +---------------------+
                    |    Editor.init      |
                    | model0 + [Scan...]  |
                    +----------+----------+
                               |
                               v
                    +---------------------+
                    |  drain startup fx   |
                    | (workspace -> tree) |
                    +----------+----------+
                               |
                               v
                    +---------------------+
                    |   Renderer.enter    |
                    | alt screen + clear  |
                    +----------+----------+
                               |
                               v
  +=========================================================+
  |              main loop  (while not ShouldQuit)          |
  |                                                         |
  |     +-- poll WindowSize --> if changed: Resize msg --+  |
  |     |                                                |  |
  |     |   +-- if needsRender --> Layout -> Renderer    |  |
  |     |   |                                            |  |
  |     |   |   +-- KeyAvailable? --> ReadKey -> Input   |  |
  |     |   |   |        |               -> KeyPressed   |  |
  |     |   |   |        |                  -> dispatch  |  |
  |     |   |   |        |                                  |
  |     |   |   |        +-- no key --> Thread.Sleep 16ms   |
  |     |   |   |                                           |
  |     +---+---+--- loop back ------------------------+    |
  +=========================================================+
                               |
                               | ShouldQuit = true
                               v
                    +---------------------+
                    |   Renderer.leave    |
                    |  (finally block:    |
                    |   restores terminal |
                    |   even on crash)    |
                    +---------------------+
```

The workspace scan, file open, file save, clipboard, and config writes all run on the thread pool via `Task.Run`. The main loop drains a `ConcurrentQueue<Msg>` of completed effects each tick, so input never blocks behind I/O. The initial render happens immediately on the freshly-built model — the file tree appears once the scan task completes and the `WorkspaceLoaded` `Msg` is drained.
