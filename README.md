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
- `Left` collapses the selected directory or moves to its parent.
- `Right` expands the selected directory.
- `Enter` opens a file or toggles a directory.
- `Escape` returns focus to the editor.

Command bar commands:

- `open <path>`: Open a file from the current workspace.
- `write`: Save the active buffer.
- `writeas <path>`: Save the active buffer to another path.
- `quit`: Exit the editor.
- `sidebar`: Toggle the sidebar.
- `tree`: Focus the file tree.
- `editor`: Focus the editor.
- `reload`: Reload the workspace tree.
- `next`: Activate the next open buffer.
- `prev`: Activate the previous open buffer.
- `help`: Show command help in the dock panel.

## How It Works

The project is a single executable defined by `fedit.fsproj`, with `Program.fs` as the only compiled source file. Startup reads the first command-line argument as the workspace root. If no argument is provided, it uses the current directory.

At runtime, `fedit` scans the workspace into a tree model and skips `.DS_Store`, `.git`, `.dotnet`, `bin`, and `obj`. The UI keeps a model containing the workspace tree, open buffers, focus target, terminal size, notifications, and panel state.

Text buffers are stored with a piece table. The original file contents stay in one string, inserted text is appended to another string, and the visible document is represented as a list of pieces. This keeps inserts and deletes local to the piece list while preserving enough state for undo and redo snapshots.

The editor loop follows a simple update-and-render flow:

1. Console input is mapped into editor key events.
2. The editor model is updated.
3. File system effects such as scanning, loading, and saving are executed.
4. The terminal screen is rendered with ANSI escape sequences.

The renderer uses the alternate screen buffer while the app is running and restores the terminal when the editor exits.
