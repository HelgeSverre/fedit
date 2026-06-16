<p>
  <img src="brand/symbol.svg" alt="" width="48" align="left" />
</p>

# fedit

**Edit files in the terminal. Small. Written in F#.**

[![CI](https://github.com/HelgeSverre/fedit/actions/workflows/ci.yml/badge.svg)](https://github.com/HelgeSverre/fedit/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/HelgeSverre/fedit/branch/main/graph/badge.svg)](https://codecov.io/gh/HelgeSverre/fedit)
[![.NET SDK](https://img.shields.io/badge/dotnet-10%2B-blue.svg)](https://dotnet.microsoft.com/download)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
![Project Type](https://img.shields.io/badge/language-F%23-blue.svg)

Opens a workspace, shows a file tree, edits files, saves to disk. Multi-buffer, undo/redo, find, command palette, system clipboard. Click and drag to select. Thirteen color themes. Persists the last 20 opened files and your theme across sessions.

Brand assets and theme spec in [`brand/`](brand/). Marketing site in [`website/`](website/) — run with `just website::dev`.

## Install

### macOS

Homebrew, or the install script — both detect your CPU (arm64 / x64):

```shell
brew install helgesverre/tap/fedit
# or
curl -fsSL https://fedit.dev/install.sh | sh
```

Homebrew updates via `brew upgrade fedit`; the script installs to `~/.local/bin`.

### Linux

```shell
curl -fsSL https://fedit.dev/install.sh | sh
```

Detects your CPU, verifies the checksum, and installs to `~/.local/bin` (set `FEDIT_VERSION` to pin a release, `FEDIT_BIN_DIR` / `FEDIT_LIB_DIR` to relocate). Homebrew on Linux works too, or grab the `*-unknown-linux-gnu.tar.xz` from [the latest release](https://github.com/HelgeSverre/fedit/releases/latest) by hand.

### Windows

```powershell
irm https://fedit.dev/install.ps1 | iex
```

Installs to `%LOCALAPPDATA%\Programs\fedit` and adds it to your user `PATH` (set `$env:FEDIT_VERSION` / `$env:FEDIT_DIR` to override). Or download `fedit-x86_64-pc-windows-msvc.zip` from [the latest release](https://github.com/HelgeSverre/fedit/releases/latest), extract, and add the folder to your `PATH`.

### From source

Requires .NET SDK 10 (pinned via `global.json` to `10.0.100` with `rollForward: latestFeature`, so any `10.0.x` patch ≥ 100 works) and [`just`](https://github.com/casey/just). The repo includes a local `.dotnet` SDK directory — the `fedit` wrapper script and `justfile` recipes prepend it to `PATH`, so a fresh clone has everything it needs.

```shell
git clone https://github.com/HelgeSverre/fedit
cd fedit
just install
fedit .
```

`just install` publishes a self-contained single-file binary to `~/.local/bin` (override with `just install path/to/bin`; remove with `just uninstall`).

### Run without installing

Use the included `fedit` shell shim:

```shell
./fedit .
```

Or through .NET directly:

```shell
dotnet run --project src/Fedit/Fedit.fsproj -- .
just run .              # via the recipe
```

Pass a file or directory path as the first argument. If no path is provided, `fedit` uses the current working directory.

### Command-line flags

- `-h` / `--help`: Show usage and exit.
- `-V` / `--version`: Print the version and exit.
- `--log <path>`: Append a UTC-timestamped trace of every `Msg` and `Effect` to `<path>`. Useful for debugging without polluting the TUI.

### Subcommands

`fedit` also exposes non-interactive subcommands for plugin management, shell-completion installation, and introspection:

- `fedit plugins install <path-or-url-or-zip>`: Install a plugin from a local folder, a git URL, or a `.zip`. Wraps the same code path as in-editor `:plugin install`.
- `fedit plugins remove <name>`: Uninstall a plugin by its `plugin.json` name.
- `fedit plugins list [--build] [--names] [--plain]`: Manifest-only listing by default — prints the install dir once, then `name` + status per row (npm/pipx style). `--build` compiles + loads each plugin (slower, mirrors `:plugin list`); `--plain` emits tab-separated `name<TAB>version<TAB>status<TAB>path` for scripts (silent when empty); `--names` prints one name per line for shell-completion scripts.
- `fedit plugins validate <path>`: Parse a `plugin.json` and report whether it's a valid manifest.
- `fedit completions <zsh|bash|fish|pwsh|nu|elvish|xonsh|yash|murex> [--install]`: Generate a shell-completion script. Without `--install`, prints to stdout. With `--install`, writes to the shell's standard location (fpath / XDG / `~/.config/<shell>/…`; yash to `~/.local/share/yash/completion/fedit` — the printed instructions tell you to add `~/.local/share/yash` to `$YASH_LOADPATH`; murex to `~/.config/fedit/completions/fedit.mx`, sourced from `~/.murex_profile`) and prints next-step instructions. Homebrew installs the bash/zsh/fish scripts automatically; pwsh/nu/xonsh/murex users source the generated file from their rc, elvish users `use fedit-completions`, yash autoloads it. OSH (Oils) is not a separate target — it reuses the bash script. The bash/zsh/fish/pwsh/xonsh emitters parse-check in CI; all nine plus OSH are exercised in a Docker harness via `just test-completions`. The generated scripts also complete file paths for the `fedit <path>` positional.
- `fedit keybinds [--json]`: Print the compiled-in default keybindings as an aligned table, or as a JSON array with `--json`.
- `fedit themes [--json]`: Print the bundled themes (name, appearance, accent) as a table, or as a JSON array with every chrome surface resolved to hex with `--json`.

`fedit plugin <verb>` (singular) is a hidden alias for `fedit plugins <verb>` so muscle memory from the in-editor `:plugin` verb keeps working.

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

## Syntax highlighting

`fedit` highlights source files with
[tree-sitter](https://tree-sitter.github.io/tree-sitter/): F# (`.fs`,
`.fsi`, `.fsx`, via the
[ionide/tree-sitter-fsharp](https://github.com/ionide/tree-sitter-fsharp)
grammar), shell scripts (`.sh`/`.bash`/`.zsh`, shell dotfiles, or a
`#!/bin/sh`-style shebang), and JavaScript/TypeScript, Python, Go, Rust,
C/C#, JSON, HTML/CSS, and more. See
[docs/syntax-highlighting.md](docs/syntax-highlighting.md) for the full
list and how files are detected.

Toggle from the command bar:

- `:syntax on` — enable
- `:syntax off` — disable
- `:syntax toggle` — flip

The choice persists to `~/.config/fedit/config.json` under
`syntaxHighlighting`. Themes carry 16 capture-to-color mappings under
each theme record (and an optional `syntax` block in user-theme JSON);
see [docs/syntax-highlighting.md](docs/syntax-highlighting.md) for the
capture map and palette tuning.

Contributors: the native grammar lives in
`vendor/tree-sitter-fsharp/` as a git submodule. Run `git submodule
update --init` after cloning, then `just build-grammars` for the host
RID or `just build-grammars-all` for all five. The compiled `.dylib` /
`.so` / `.dll` files are not tracked — CI builds the matching RID on
each matrix leg.

## Plugins

`fedit` supports third-party plugins written in F#. Plugins register
commands and keybindings via the `Fedit.PluginApi` library; the host
builds and loads them at startup.

- **Marketing-tinted introduction:** [fedit.dev/plugins](https://fedit.dev/plugins) — hero, quick start, lifecycle diagram, action cheatsheet, example cards.
- **Author guide:** [docs/plugins.md](docs/plugins.md) — manifest reference, every type, conflict policy, debugging.
- **Reference implementations:** [`examples/`](examples/) — `wordcount`, `journal`, and three TODO finders (`todo-count`, `todo-list`, `todo-next`).

```bash
mkdir -p ~/.config/fedit/plugins
cp -R examples/wordcount ~/.config/fedit/plugins/
fedit .              # in editor: Ctrl+P → plugin reload → wc
```

## Using the Editor

Global shortcuts:

- `Ctrl+P`: Open the prompt in command mode (`:` prefix).
- `Ctrl+O`: Open the prompt in file-picker mode (recent + workspace files).
- `Ctrl+F`: Open the prompt in search mode (`/` prefix) for the active buffer.
- `Ctrl+B`: Toggle the file tree — hidden → show + focus; visible elsewhere → focus; visible + focused → hide and return to editor.
- `Ctrl+E`: Focus the editor.
- `Ctrl+S`: Save the active buffer.
- `Ctrl+R`: Reload the workspace tree.
- `Ctrl+Q`: Quit (prompts once if buffers are dirty; press again to discard).
- `Ctrl+Z`: Undo.
- `Ctrl+Y`: Redo.
- `Ctrl+PageDown` / `Ctrl+PageUp`: Cycle to the next / previous open buffer.
- `Ctrl+1`…`Ctrl+9`: Jump to the open buffer at sorted index 1..9 (silent no-op if out of range).

Editor keys:

- Arrow keys, `Home`, `End`, `PageUp`, and `PageDown` move the cursor.
- `Alt+Left` / `Alt+Right` (or `Ctrl+Left` / `Ctrl+Right`) move the cursor by word.
- `Ctrl+Backspace` / `Ctrl+Delete` delete the previous / next word.
- `Shift+Arrow`, `Shift+Home`, `Shift+End` extend the text selection.
- `Ctrl+A` selects the whole buffer.
- `Ctrl+C` / `Ctrl+X` copy or cut the current selection to the system clipboard (via `pbcopy` on macOS, `xclip` on Linux).
- `Ctrl+V` pastes from the system clipboard.
- `Tab` indents the current line.
- `Shift+Tab` unindents the current line.
- `Enter`, `Backspace`, and `Delete` edit text normally; with a selection, they replace it.
- The mouse wheel scrolls the viewport; the cursor follows only when it would cross the `scrollOff` margin. Set `scrollMode` to `line` to make the wheel move the cursor instead. While fedit runs it captures the mouse — hold `Shift` (or `Option` on macOS) for the terminal's own selection and scrollback.
- **Click** in the editor to place the cursor. **Drag** to select text. Clicking also restores focus to the editor when the sidebar or prompt had it.

Find keys (after `Ctrl+F`):

- Type to extend the query; matches highlight live and the cursor jumps to the first hit.
- `Enter` or `Down` advances to the next match; `Up` goes to the previous.
- `Backspace` shortens the query (no-op once empty).
- `Escape` closes the prompt and clears the highlights.

File tree keys:

- `Up` and `Down` move selection.
- `PageUp` and `PageDown` jump by `treePageJump` entries at a time (default 10; configurable).
- `Home` and `End` jump to the first or last visible entry.
- `Left` collapses the selected directory or moves to its parent.
- `Right` expands the selected directory.
- `Enter` opens a file or toggles a directory.
- `Escape` returns focus to the editor.

Prompt keys (any mode):

- Type to extend the query; the dock panel shows matching items or live feedback for the active mode.
- `Up` / `Down` (and `Shift+Tab`) move the highlight through the completion list. The viewport scrolls so the selected item stays visible. In Search mode `Up` / `Down` cycle through matches instead.
- `Tab` autofills the prompt with the highlighted completion's text — `:o<Tab>` becomes `:open`, ready for arguments. No-op when the completion list is empty.
- `Alt+Up` / `Alt+Down` walk through recent prompt history (up to 20 entries).
- `Enter` runs the parsed command, applies the highlighted completion, opens the selected file/buffer, or advances to the next search match — depending on the mode.
- `Left`, `Right`, `Home`, `End`, `Backspace`, and `Delete` edit the input. Backspace through the prefix character flips the mode (e.g. `/foo` → backspace → empty FilePicker); backspace at the start of an empty prompt is a no-op (use `Escape` to close).
- `Escape` is the sole way to close the prompt; in Search mode it also clears the match highlights.

Prompt modes — type the prefix to switch modes inside the prompt, or use the dedicated chord to open in that mode directly:

| Prefix | Mode       | Opens via | What it does                                                                                                                                      |
| :----: | ---------- | --------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
|  `:`   | Command    | `Ctrl+P`  | Named commands and `:LINE[:COL]` cursor jumps. Argument starting with a digit is parsed as goto (`:42` or `:100:6`); otherwise as a command name. |
| (none) | FilePicker | `Ctrl+O`  | Recent files first, then workspace files; type to filter; Enter opens.                                                                            |
|  `/`   | Search     | `Ctrl+F`  | Incremental search in the active buffer. Cursor jumps live to the first match.                                                                    |
|  `@`   | Buffers    | —         | Switch to an open buffer by numeric id or name.                                                                                                   |

Named commands (typed after `:`):

- `open <path>`: Open a file from the current workspace. Activates the existing buffer if the file is already open.
- `write`: Save the active buffer.
- `writeas <path>`: Save the active buffer to another path.
- `quit`: Exit the editor.
- `config`: Open `~/.config/fedit/config.json` in a buffer; creates it from the running config on first call.
- `reload`: Reload the workspace tree.
- `next` / `prev`: Cycle buffers (also bound to `Ctrl+PageDown` / `Ctrl+PageUp`).
- `theme <name>`: Switch the theme. The accent themes — `green` (default), `blue`, `orange`, `cyan`, `teal`, `yellow`, `red`, `graphite`, `evergreen`, `mono-amber` — swap the accent family and keep the terminal-default chrome; `github-light`, `github-dark`, and `ayu` repaint the full surface. The UI live-previews each highlight as you cycle. The choice persists to `~/.config/fedit/config.json` and is restored on next launch.
- `recent <path>`: Pick a recently opened file. Tab to cycle through the last 20 files; the list persists in the same config file.
- `buffers <id-or-name>`: Switch to an open buffer by numeric id or name. Completion shows `{id} {name}` with the file path as detail.
- `syntax <on|off|toggle>`: Toggle syntax highlighting; persists to config under `syntaxHighlighting`.
- `plugin <verb> [arg]`: In-editor plugin manager. See `docs/plugins.md` for the verbs.
- `plugins`: Open the plugin manager picker.
- `macros`: Open the macro manager picker.
- `keybind [reload | <stroke>]`: List the effective keybindings, reload the keybinds file (`keybind reload`), or show what a stroke resolves to in each context (`keybind ctrl+s`).

A few keyboard-first verbs (`sidebar`, `tree`, `editor`) still parse if typed, but are hidden from the completion menu since `Ctrl+B` / `Ctrl+E` cover the same ground more richly.

## Keybindings

The defaults above are a compiled-in keymap. Override it with a plain-text file at
`~/.config/fedit/keybinds`; the file is layered on top of the defaults, so you
only list what you change. Reload it with `:keybind reload` (or just save the
file through fedit). A malformed line is skipped and reported as a startup
notification — the editor always boots.

Each line is `[context] stroke[ stroke…] = action[:arg]`:

```
# Rebind save; the context defaults to "editor" when omitted.
ctrl+s            = save
# Contexts: global, editor, sidebar, prompt. Global fires in every focus.
global  ctrl+r    = reload-workspace
# A multi-key sequence (press in order; the status bar shows the pending prefix).
editor  ctrl+k ctrl+s = save
# Free a binding entirely with an empty right-hand side (does NOT fall through).
global  ctrl+r    =
# Actions can take a ":" argument.
editor  f6        = set-theme:gruvbox
# Bind a plugin command: run-plugin:<source>/<name> [plugin-arg]
editor  ctrl+k ctrl+w = run-plugin:wordcount/wc selection
```

A stroke is a `+`-joined chord such as `ctrl+shift+p`, `alt+left`, `f6`, or
`enter`; modifier aliases `cmd`/`super`, `opt`/`alt` are accepted. Precedence is
**user keymap → plugin chords → text input**: a binding you set always beats a
plugin's, and a context binding beats a `global` one. A copyable starter file
lives at [`examples/keybinds`](examples/keybinds).

### Macros

Record a run of keystrokes and replay it. `Ctrl+Shift+M` starts and stops
recording into register `a` — the status bar shows `REC @a` while it captures.
`Ctrl+Shift+R` replays the register; `Ctrl+Shift+.` repeats the last macro.
Triggers are modifier chords so bare keys stay text input. Macros live in
memory for the session; they are not yet persisted to the keybinds file.

## Configuration

`fedit` reads `~/.config/fedit/config.json` at startup. The file is created automatically the first time the editor persists state (`:theme`, `:syntax`, plugin enable/disable, or quitting after opening files — `recent` is written at quit, not per open). Hand-edited values are preserved on every save — unknown keys you add to the file are kept intact.

| Key                  | Type     | Default    | Range                        | What it controls                                                                                                                                                                                                                 |
| -------------------- | -------- | ---------- | ---------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `theme`              | string   | `green`    | —                            | Theme name (bundled — `green` / `blue` / `orange` / `cyan` / `teal` / `yellow` / `red` / `graphite` / `evergreen` / `mono-amber` / `github-light` / `github-dark` / `ayu` — or user theme from `~/.config/fedit/themes/*.json`). |
| `recent`             | string[] | `[]`       | up to 20                     | Recently opened files; managed automatically.                                                                                                                                                                                    |
| `disabledPlugins`    | string[] | `[]`       | —                            | Plugins skipped at scan time. Managed by `:plugin enable` / `:plugin disable` and the `:plugins` picker.                                                                                                                         |
| `syntaxHighlighting` | bool     | `true`     | —                            | Tree-sitter syntax highlighting. Toggled by `:syntax on\|off\|toggle`.                                                                                                                                                           |
| `completionLimit`    | int      | `8`        | 1–64                         | Max items considered for `:open`, `:writeas`, `:recent`, `:buffers` completions.                                                                                                                                                 |
| `sidebarIndent`      | int      | `2`        | 0–16                         | Spaces per depth level in the file tree.                                                                                                                                                                                         |
| `sidebarWidth`       | int      | `30`       | 10–200                       | Initial sidebar width in columns.                                                                                                                                                                                                |
| `dockHeight`         | int      | `8`        | 1–40                         | Dock panel height in rows (used for prompt completions and mode hints).                                                                                                                                                          |
| `wordMotion`         | string   | `wordEnd`  | `wordEnd` or `nextWordStart` | Where `Alt+Right` / `Ctrl+Delete` land — end of current word (default) or start of next word (vim `w`).                                                                                                                          |
| `pageOverlap`        | int      | `2`        | 0–32                         | Lines kept on screen between `PageUp` / `PageDown` jumps in the editor. Editor jumps by `viewportHeight - pageOverlap`. Matches Zed / VSCode / token-editor defaults.                                                            |
| `scrollMode`         | string   | `viewport` | `viewport` or `line`         | What the mouse wheel does. `viewport` (default) scrolls the view, dragging the cursor only to honour `scrollOff`; `line` keeps the legacy behaviour where the wheel moves the cursor line.                                       |
| `scrollOff`          | int      | `5`        | 0–50                         | Lines kept between the cursor and the top/bottom edge (vim/helix `scrolloff`). Applies to all cursor movement, not just the wheel.                                                                                               |
| `mouseScrollLines`   | int      | `3`        | 1–20                         | Lines moved per mouse-wheel tick (matches nvim `mousescroll` `ver:3`).                                                                                                                                                           |
| `treePageJump`       | int      | `10`       | 1–500                        | Entries jumped on `PageUp` / `PageDown` in the file-tree sidebar.                                                                                                                                                                |
| `tabWidth`           | int      | `4`        | 1–16                         | Spaces inserted by `Tab` and removed by `Shift+Tab`.                                                                                                                                                                             |
| `icons`              | string   | `off`      | `off` or `nerd`              | File-tree icon style. `nerd` swaps in Nerd Font PUA glyphs (requires a Nerd Font in your terminal); `off` keeps the ASCII `[+] / [-] / 4-space` markers.                                                                         |
| `statusFormat`       | string   | see below  | —                            | Status bar layout template. Tokens like `[MODE]`, `[LINE]`, `[BUFFER]` resolve against the model; `<EXPAND>` is a flex spacer that absorbs leftover width.                                                                       |

Changes take effect on next launch. Out-of-range values clamp to the nearest valid bound rather than failing.

#### `statusFormat` grammar

The default format is:

```
[MODE]  [CURRENT_FILE:short][DIRTY] <EXPAND> [NOTIFICATION]  [LINE]:[COLUMN]  [LINE_ENDING]  [BUFFER]
```

Tokens are case-insensitive. Unknown tokens render literally so typos are visible (e.g. `[xyx]` stays as `[xyx]`). Multiple `<EXPAND>` placeholders share leftover width; odd remainder distributes left-to-right.

| Token                  | Resolves to                                                                                |
| ---------------------- | ------------------------------------------------------------------------------------------ |
| `[MODE]`               | Focus label (`EDIT` / `CMD` / `FILE` / `FIND` / `BUF` / `TREE`).                           |
| `[CURRENT_FILE]`       | Filename only (e.g. `README.md`); `[scratch]` for unsaved buffers.                         |
| `[CURRENT_FILE:short]` | Path with `$HOME` tildified (e.g. `~/code/fedit/README.md`).                               |
| `[CURRENT_FILE:full]`  | Absolute path.                                                                             |
| `[DIRTY]`              | ` [+]` when the buffer is dirty (with leading space so it disappears cleanly), else empty. |
| `[LINE]` / `[COLUMN]`  | 1-based cursor position.                                                                   |
| `[LINE_ENDING]`        | `LF` or `CRLF`.                                                                            |
| `[BUFFER]`             | Sorted index / count (e.g. `2/5`).                                                         |
| `[NOTIFICATION]`       | Active notification message, or empty.                                                     |
| `<EXPAND>`             | Flex spacer; multiple expand placeholders split remaining columns evenly.                  |

### User themes

Drop a JSON file in `~/.config/fedit/themes/`. The schema:

```json
{
    "name": "midnight",
    "description": "Hand-rolled midnight palette",
    "accent": "#7AA2F7",
    "statusFg": "brightWhite",
    "statusBg": "#1A1B26",
    "selectedBg": "#283457",
    "currentLine": "#3B4261"
}
```

Color fields accept either a hex string (`#RGB` or `#RRGGBB`) or a named color (case- / kebab- / snake- / camel-insensitive). Standard 16 ANSI names (`red`, `brightWhite`, …) plus the curated cube picks defined in `Color.fs` (`deepSkyBlue`, `phosphorGreen`, `burntOrange`, …) are recognised. Modern terminals render hex values as truecolor (`38;2;r;g;b`); the renderer doesn't downgrade today, so a Nerd Font / truecolor-capable terminal is assumed. User themes load at startup; a malformed file is logged in the startup notification rather than crashing.

## How It Works

The project is an executable defined by `src/Fedit/Fedit.fsproj`. The bulk of the codebase sits under `namespace Fedit` (see `<Compile Include="…">` in the fsproj for the canonical order): `Primitives.fs` → `Keys.fs` → `Events.fs` → `TerminalCapabilities.fs` → `MouseProtocol.fs` → `ImageProtocol.fs` → `KittyImage.fs` → `PieceTable.fs` → `Buffer.fs` → `Workspace.fs` → `Screen.fs` → `Color.fs` → `Themes.fs` → `Highlight.fs` → `Commands.fs` → `Actions.fs` → `Keymap.fs` → `Plugins.fs` → `PickerTypes.fs` → `PromptTypes.fs` → `Model.fs` → `Config.fs` → `Pickers.fs` → `KeymapIO.fs` → `Prompt.fs` → `Dock.fs` → `Editor.fs` → `Status.fs` → `Renderer.fs` → `Input.fs` → `View.fs` → `Terminal.fs` → `Runtime.fs` → `Cli.fs` → `Cli/Commands/*.fs` → `Program.fs`. The CLI surface lives under `namespace Fedit.Cli` per .NET convention: the parser at `Cli.fs` (`Fedit.Cli.Parser`) and the subcommand handlers under `Cli/Commands/`. The test project lives in `tests/Fedit.Tests/` and the `Fedit.slnx` solution at the repo root ties both together. Startup reads the first non-flag command-line argument as a file or workspace path. Existing files open in their parent workspace; directories open as workspaces. If no argument is provided, it uses the current directory.

At runtime, `fedit` scans the workspace into a tree model and skips `.DS_Store`, `.git`, `.dotnet`, `bin`, and `obj`. A `FileSystemWatcher` is installed on the same workspace root so external edits, creations, deletions, and renames trigger a debounced rescan (300ms) without `Ctrl+R`. The UI keeps a model containing the workspace tree, open buffers, focus target, terminal size, notifications, and panel state.

Text buffers are stored with a piece table. The original file contents stay in one string, inserted text is appended to another string, and the visible document is represented as a list of pieces. This keeps inserts and deletes local to the piece list while preserving enough state for undo and redo snapshots. Each open buffer keeps its own undo and redo stacks, cursor position, and viewport.

Files are read as UTF-8. The line ending of the loaded file (`LF` or `CRLF`) is detected and reused on save; the buffer always works in `\n` form internally. Saving writes UTF-8 without a byte-order mark. Rendering currently assumes one display cell per UTF-16 code unit; wide glyphs, emoji, and combining marks can misalign cursor placement until the display-width layer lands.

The UI is split into a sidebar (file tree), an editor pane with a line-number gutter, a status line, a single-line prompt row at the bottom, and a dock panel that's hidden when the prompt is inactive. The dock appears automatically when the prompt is active — showing completions for file/command/buffer modes and a match counter or jump hint for search and goto. The status line is template-driven via `statusFormat` in config (see Configuration); the default layout puts the focus mode on the left, the current notification floating in the middle, and line/column / encoding / buffer-index pinned to the right edge.

Themes own the full chrome surface — each `Theme` record carries an explicit fg/bg per region (editor, gutter, prompt, dock, status, selection, active line) on top of the accent family. The accent themes set `Default` backgrounds so the terminal's own background shows through; `github-light`, `github-dark`, and `ayu` supply real backgrounds and repaint everything. The chosen theme, the most recent 20 opened files, and the runtime tunables described in [Configuration](#configuration) all live in `~/.config/fedit/config.json` and are restored on the next launch; the default theme is `green` (phosphor green, the brand accent).

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
