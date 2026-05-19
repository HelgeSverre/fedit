```
:::===== :::===== :::====  ::: :::====
:::      :::      :::  === ::: :::====
======   ======   ===  === ===   ===
===      ===      ===  === ===   ===
===      ======== =======  ===   ===
```

[![.NET SDK](https://img.shields.io/badge/dotnet-9%2B-blue.svg)](https://dotnet.microsoft.com/download)
[![License: MIT](https://img.shields.io/badge/license-MIT-yellow.svg)](LICENSE)
![Project Type](https://img.shields.io/badge/language-F%23-blue.svg)

---

`fedit` is a small terminal text editor written in F#. It opens a workspace, shows a file tree, edits files in a terminal UI, and saves changes back to disk.

## Requirements

- .NET SDK 9 or newer
- `just` for the convenience recipes in `justfile`

This repository includes a local `.dotnet` SDK directory. The `fedit` wrapper script and the `justfile` recipes prepend `.dotnet` to `PATH`, so the local SDK is used when it is present.

## Quick Start

Run the editor in the current directory:

```sh
./fedit .
```

Run it directly through .NET:

```sh
dotnet run --project fedit.fsproj -- .
```

Or use the just recipe:

```sh
just run .
```

Pass a file or directory path as the first argument. If no path is provided, `fedit` uses the current working directory.

## Build Commands

Build the project:

```sh
dotnet build fedit.fsproj
```

Run the project:

```sh
dotnet run --project fedit.fsproj -- .
```

Clean generated output:

```sh
dotnet clean fedit.fsproj
rm -rf bin obj
```

## Justfile Recipes

List available recipes:

```sh
just
```

Start the app under `dotnet watch`:

```sh
just dev .
```

Build the project:

```sh
just build
```

Run the editor:

```sh
just run .
```

Clean compiled output:

```sh
just clean
```

Format F# sources with [fantomas](https://fsprojects.github.io/fantomas/) (restored from `.config/dotnet-tools.json`):

```sh
just format
```

Verify formatting without writing — fails if anything would change:

```sh
just format-check
```

Run format-check and build together as a single gate (handy before a commit):

```sh
just check
```

Publish a self-contained single-file binary and install it to `~/.local/bin` (override with `just install path/to/bin`):

```sh
just install
```

Remove a previously installed binary:

```sh
just uninstall
```

## Using the Editor

Global shortcuts:

- `Ctrl+P`: Open the command bar.
- `Ctrl+B`: Focus the file tree.
- `Ctrl+E`: Focus the editor.
- `Ctrl+S`: Save the active buffer.
- `Ctrl+R`: Reload the workspace tree.
- `Ctrl+Q`: Quit.
- `Ctrl+Z`: Undo.
- `Ctrl+Y`: Redo.

Editor keys:

- Arrow keys, `Home`, `End`, `PageUp`, and `PageDown` move the cursor.
- `Tab` indents the current line.
- `Shift+Tab` unindents the current line.
- `Enter`, `Backspace`, and `Delete` edit text normally.

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
- `theme <name>`: Switch the accent color. Tab through `cyan`, `teal`, `green`, `yellow`, `orange`, `red`, `magenta`, or `purple`; the UI live-previews each highlight as you cycle.
- `help`: Show command help in the dock panel.

## How It Works

The project is a single executable defined by `fedit.fsproj`, with `Program.fs` as the only compiled source file. Startup reads the first command-line argument as the workspace root. If no argument is provided, it uses the current directory.

At runtime, `fedit` scans the workspace into a tree model and skips `.DS_Store`, `.git`, `.dotnet`, `bin`, and `obj`. The UI keeps a model containing the workspace tree, open buffers, focus target, terminal size, notifications, and panel state.

Text buffers are stored with a piece table. The original file contents stay in one string, inserted text is appended to another string, and the visible document is represented as a list of pieces. This keeps inserts and deletes local to the piece list while preserving enough state for undo and redo snapshots. Each open buffer keeps its own undo and redo stacks, cursor position, and viewport.

Files are read as UTF-8. The line ending of the loaded file (`LF` or `CRLF`) is detected and reused on save; the buffer always works in `\n` form internally. Saving writes UTF-8 without a byte-order mark.

The UI is split into a sidebar (file tree), an editor pane with a line-number gutter, a status line, a dock panel that shows contextual help or completions, and a single-line command bar at the bottom. The status line reports the current focus, active file path, dirty marker, cursor position, and the most recent notification.

Themes are pure accent palettes — the dock title, status bar, selection highlight, and current-line gutter all swap together while the grayscale chrome stays constant across themes. The active theme lives in the model and is not persisted between runs (default: `cyan`).

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

`dispatch` is the recursive knot: every effect produces a `Msg`, which goes back through `Editor.update`, which may yield more effects, until the list is empty. All of it runs on the main thread.

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

Two things to notice. First, the workspace scan happens *before* the alternate screen is entered, so a slow scan blocks startup with no UI feedback. Second, because `runEffect` is synchronous, file I/O during the session freezes input until it completes — large saves or reloads show as a brief input stall.
