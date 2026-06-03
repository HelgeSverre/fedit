# fedit changelog

Historical record of work shipped to date. Active work and future ideas
live in [`TODO.md`](TODO.md).

## Shipped

| Phase | What landed                                                                                                                                                                           |
| ----: | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
|     0 | `:theme` command, 8 bundled palettes, live preview, persistence to `~/.config/fedit/config.json` (9/9).                                                                               |
|     1 | Architecture findings F1–F7 (gutter width, view-string placement, workspace metadata, Buffer.serialize, Renderer wrapper, collapseSelected, scan errors).                             |
|     2 | Module reorganization — `Program.fs` is an entry-point shell; 13 numbered files under `namespace Fedit`.                                                                              |
|     3 | UX: find-in-buffer, open recent, confirm-quit, buffer picker, line-ending indicator, word motion, selection + clipboard, file watcher, user themes (11/11).                           |
|     4 | DX: install recipe, Tier 1 tests (63 passing), CI on `{ubuntu, macos, windows}`, format/lint, crash handler, `--log` flag, `dotnet watch` docs.                                       |
|     5 | Performance: P1 line cache, P2 async + cancellation I/O, P3 struct types, P4 undo cap (200), P5 idiom cleanups.                                                                       |
|     6 | .NET conventions: `global.json`, `Directory.Build.props`, `.editorconfig`, publish settings in fsproj, `src/` + `tests/` restructure, `Fedit.slnx`, OS-matrix CI, repo hygiene (8/8). |
|    UX | Command Bar & Dock: Vertical completion navigation, virtual scrolling, dimmed details, slim dock (hidden by default), `:help` toggle.                                                 |
| Brand | `brand/` system: caret SVG mark, Phosphor Green `#00B86B` accent, Departure Mono (brand) + JetBrains Mono (code), 5 voice rules, do-not gallery. Seven themes (`green` default, `blue`, `orange`, `cyan`, `teal`, `yellow`, `red`); `purple`/`magenta` dropped per brand bans. |
|  Site | `website/` Astro 5 + bun: `/` (features, install, commands, themes, architecture) and `/brand` (mark, palette, typography, voice). Hero with typing-style SVG mascot, CTA → smooth scroll to install. `just website::{dev,build,check,lint,format}`. |
|  Dist | Release pipeline: `just release <version>` tags + pushes. `.github/workflows/release.yml` cross-builds 5 RIDs (mac arm64/x64 on macos-14, linux arm64/x64, win-x64), publishes `.tar.xz`/`.zip` + SHA256 to GitHub Release, renders `scripts/fedit.rb.tmpl` and auto-commits the updated formula to `HelgeSverre/homebrew-tap`. Install via `brew install helgesverre/tap/fedit`. |
|  Docs | Root `just format`/`lint` now runs prettier on `**/*.md` alongside fantomas. README install section leads with Homebrew; build-from-source demoted. `CLAUDE.md` added for agent onboarding. |
|  Keys | Keybindings: structured `Chord`/`KeyStroke` key model decoding the full key universe (Ctrl+Shift, F-keys, Ctrl+arrows); a data-driven keymap (`Keymap.fs`) with compiled-in defaults overlaid by a user `~/.config/fedit/keybinds` file (contexts, multi-key sequences, `unbind`, `run-plugin:` bindings, `:keybind` introspection + reload). Reachable for the first time: `Ctrl+O` file picker, `Ctrl+arrow` word motion. **Behavior change:** plugin keybindings no longer shadow built-in or user keybindings — the user keymap takes precedence. |
| Macros | Record and replay keystroke macros (keybindings phase 4). `Ctrl+Shift+M` toggles recording into register `a`, `Ctrl+Shift+R` replays it, `Ctrl+Shift+.` repeats the last macro. Recording captures chords (not actions), so replay re-runs live keymap resolution and reassembles sequences; injected keys are bracketed by `MacroReplayStart`/`End` markers so a replay is never self-recorded. Status bar shows `REC @a` while recording. Triggers are modifier chords — bare `Char` stays text input. In-memory for the session; keybinds-file persistence, stop-on-no-op, and nested replay are deferred. |
| Prompt | Unified `CommandBarState` + `SearchState` into one `PromptState` with prefix dispatch — modes derive from the first character (`:` command/goto, `/` search, `@` buffers, empty file picker). `Ctrl+P` opens in command mode, `Ctrl+O` file picker, `Ctrl+F` search. `Ctrl+B` is a three-state sidebar toggle (Zed-style). Sidebar gets VS Code / Finder-style type-ahead (case-insensitive prefix, fallback to single-char on mismatch, cycle on re-type, status shows `find:Mod`). Status bar gets 1ch padding; legend strip removed; `:help` retired. `Prompt.fs` module + `design/modality-explorer.html` capture the design path. |
|     7 | Tier 2 snapshot tests: `Snapshot.fs` projector + 8 scenarios covering focus / prompt mode / sidebar separator / resize. Inline goldens, no Verify dependency. |
|     8 | Tier 3 binary smoke: `--help` and `--version` short-circuits added to `Program.fs`; 3 smoke tests spawn `fedit` via `dotnet run --no-build`. Explicit `FSharp.Core` package reference so the DLL ships in `bin/` for non-SDK invocations. |
|     9 | Quick wins: edit primitives compute `Lines` once per keystroke via `finalizeEdit`; `runEditor` cursor motion collapsed onto `move` / `extend` / `pageJump` helpers; `jsonEscape` was already removed during the config-tunables work. |
|    10 | Module splits: `BufferRef = ById int \| ByName string` typed payload for `SwitchBuffer`; `Config.fs` (`ConfigIO` module) carved out of `Runtime.fs`. The CommandBar / Search split was subsumed by the prompt unification. |
|    11 | Renderer diff: `Renderer.render` takes `previous: Screen voption` and emits cursor jumps + SGR only for changed cells. Style tracked across rows. Typing-quiet frames go from ~30 KB to <100 bytes of ANSI. |
|    12 | Async follow-ups: `EditTick`-guarded `markSaved` (no false-clean on concurrent edits); serialized config writes via a single task chain; `RunSearch` effect with cancellation token (search is no longer synchronous inside `update`). |
|    13 | Workspace `ByPath` flat map + pre-sorted children (held `Down` no longer reads cold on large trees); `loadConfig` / `loadUserThemes` return errors that fold into the startup notification instead of silently swallowing. |
|    14 | Polish: tab width is config-driven (`tabWidth`, default 4); `Recent` persists at quit instead of every file-open; theme preview is now derived in `View` (not stored on `PromptState`); orphan `Workspace.metadata` removed. |
|    15 | Unicode `│` sidebar separator (U+2502); opt-in Nerd Font file/folder glyphs via `"icons": "nerd"` (default `"off"`, no first-run glyph breakage). |
|    16 | `Buffer.ensureViewport` simplified via a shared `slideViewport` helper. Lines→Offsets cache and delta-based undo deferred — each is a multi-day refactor better done in a dedicated session. |
|    17 | .NET 10 LTS upgrade: `global.json` → `10.0.100`, both fsproj → `net10.0`, `FSharp.Core` 10.0.100, `FsCheck.Xunit` out of RC. Release workflow's setup-dotnet pin bumped accordingly. |
|    18 | Central Package Management: `Directory.Packages.props` at the repo root; `Fedit.Tests.fsproj` no longer carries versions. |
|    19 | Release automation: tag-triggered `release.yml` matrix-publishes 5 RIDs, attaches `tar.xz`/`zip` + SHA256 sidecars to a GitHub Release, renders + commits the Homebrew formula. |
|    20 | CI hardening: Dependabot config (actions + nuget grouped); NuGet cache; `concurrency: cancel-in-progress`; `ContinuousIntegrationBuild=true` for deterministic builds. CodeQL deferred to GitHub default code-scanning (no F#-specific queries to gain from a YAML workflow). |
|    21 | Repo hygiene: `SECURITY.md`, slim `bug_report.md` issue template, `PULL_REQUEST_TEMPLATE.md`. |
| Color | Unified `Color = Default \| Indexed of byte \| Rgb of byte*byte*byte`. New `Color.fs` with standard-16 + 26 curated cube picks named for bundled themes, `Color.ofHex` / `tryOfHex`, `Color.tryOfName`, `Color.tryParse` (hex first, then name), `Color.toIndexed` quantization. `Renderer.sgrColor` emits truecolor `38;2;r;g;b` directly. Bundled themes rewritten using named statics; visual output pixel-identical. User-theme JSON now expects hex or named colors (breaking — no in-repo user themes use the old integer schema). |
| Plugin | MVP plugin API. New `src/Fedit.PluginApi/` library exposes `IPluginHost`, `PluginCommand`, `PluginAction`, `KeyChord`. New `src/Fedit/Plugins.fs` discovers `~/.config/fedit/plugins/<name>/`, auto-generates a fsproj if missing, runs `dotnet build -c Release`, loads the DLL via an isolated `AssemblyLoadContext`, and reflects out a `register : IPluginHost -> unit`. Built-in `plugin <list/install/remove/reload/validate>` command; plugin commands merge into the prompt's surface; plugin keybindings (`Ctrl+<char>`, `Alt+...`, `F1..F12`) dispatch in editor focus. Five reference implementations under `examples/`: `wordcount`, `journal`, plus three `TODO:` finders (`todo-count`, `todo-list`, `todo-next`). 17 unit tests + one end-to-end build-and-load against the wordcount example. Author guide at `docs/plugins.md`; marketing/intro page at `/plugins` on the website (lifecycle diagram, action cheatsheet, example cards). |
| CLI    | `src/Fedit/Cli.fs` becomes a declarative arg-parser. `CliApp<'Option>` bundles program name, summary, positionals, and options; `Cli.formatHelp` / `formatUsage` / `formatErrors` are pure-string renderers so `--help` is generated from the same spec the parser uses (no more hand-written `printHelp`). Spec uses `Short: char option` + `Long: string` (clap/commander shape) — type-system enforces at-most-one-short. Strict parsing: unknown flags now exit 2 with a Levenshtein-based "Did you mean '--help'?" hint instead of being silently swallowed as positionals (`fedit --whatver` used to boot the editor and try to open `--whatver/` as a workspace). `--flag=value` inline syntax and the `--` end-of-flags sentinel both supported. All errors collected per pass (`Result<_, CliError list>`), not just the first. 38 tests in `CliTests.fs` (~94.7% line / 84.3% branch on `Cli.fs`). New `just coverage` recipe wires Coverlet + ReportGenerator to produce `coverage/index.html`. |
| Subcmds | `fedit plugins <install|remove|list|validate>` and `fedit completions <zsh|bash|fish> [--install]`. Subcommand surface lives in `src/Fedit/Cli/Commands/{Plugins,Completions}.fs`; the plugins handlers wrap the same `Plugins.install/uninstall/discover` helpers that back the in-editor `:plugin` verb so the two paths can't drift. `Cli.route` adds top-level subcommand routing with hidden aliases (`fedit plugin install …` resolves to `plugins` without being shown in `--help`). `CliCompletionKind` (`FilePath` / `DirectoryPath` / `DynamicCommand` / `Choices`) tags positionals + option values; the completions generator walks the `CliCommandDescriptor` projection of the real `CliApp<_>` so the emitted scripts can't lie about parser config. `fedit plugins list --names` exists for shell-side dynamic completion of installed plugin names. |
| Cli.* ns | Reorganized the CLI corner to follow .NET convention: `namespace Fedit.Cli` with the parser in `Cli.Parser`, types at namespace level, and command handlers at `Fedit.Cli.Commands.Plugins` / `Fedit.Cli.Commands.Completions`. Folder structure mirrors namespace (`src/Fedit/Cli.fs` + `src/Fedit/Cli/Commands/`). Mirrors the shape used by FSharp.Compiler.Service, Fable, Fantomas. The rest of the codebase stays flat (`namespace Fedit + module X =`) — the migration is scoped to the CLI surface where the depth started to pay off. |
| Palette | `:config` opens `~/.config/fedit/config.json` in a buffer, materializing it from the running config on first call so a fresh install gets a useful starting point. Tab in the prompt now applies the highlighted completion's text instead of cycling (`:o<Tab>` → `:open`, ready for arguments); cycling moved fully to Up/Down/ShiftTab. `Commands.Spec` gains a `Hidden: bool` flag — hidden specs still resolve through `parseWith` (muscle memory + future scripting) but skip the completion menu and help listing. `sidebar` / `tree` / `editor` flagged hidden since `Ctrl+B` and `Ctrl+E` are richer than the typed verbs. Backspace at cursor 0 in the prompt is now a no-op; Esc is the single way out (was: held backspace silently dismissed the palette). |
| Status | Status bar is now template-driven via `Config.StatusFormat`. New `Status.fs` parses `[TOKEN]` (with optional `:modifier`) and `<EXPAND>` (flex spacer) and resolves against the model. Tokens: `[MODE]`, `[CURRENT_FILE]` / `[:short]` / `[:full]`, `[DIRTY]`, `[LINE]`, `[COLUMN]`, `[LINE_ENDING]`, `[BUFFER]` (sorted index/count), `[NOTIFICATION]`. Unknown tokens render literally so typos surface. Multiple `<EXPAND>` placeholders share leftover width; odd remainder distributes left-to-right. Default format pushes line/col/encoding/buffer-index to the right edge while leaving notification floating in the middle — matches the layout common in modern editors. Shell-command tokens (`[$(cmd)/refresh:5s/truncate:50]`) logged as a deferred follow-up. |
| Buffers | Buffer-switching keybindings land in the global key handler: `Ctrl+PageDown` next buffer, `Ctrl+PageUp` previous, `Ctrl+1..9` jump to the buffer at sorted index 1..9. Matches Zed / VS Code / IntelliJ defaults. Out-of-range jumps (`Ctrl+5` with 3 buffers open) are a silent no-op rather than a notification. `KeyInput` grows `CtrlPageUp` / `CtrlPageDown` / `CtrlDigit of int` cases; `Input.tryMap` recovers the digit value from the contiguous `ConsoleKey.D0..D9` range. Legacy macOS Terminal.app may not pass `Ctrl+digit` through cleanly — flagged as a known limitation; Plan B is `Alt+1..9`. |

## Architecture review findings (all resolved)

All seven items were FRICTION severity — no structural breaks, but each
one either hid a latent bug or made future change more expensive than
it needed to be. References are by symbol so they survive edits; grep
the symbol if a line is wanted.

| #   | Where                                              | Finding                                                                                                                                                                                            |
| --- | -------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| F1  | `Editor.updateActiveBuffer`, `Layout.renderEditor` | Viewport gutter width hardcoded `8` in `updateActiveBuffer`; `renderEditor` computes it as `digits + 2`. They will diverge for buffers with few or many lines and produce wrong horizontal scroll. |
| F2  | `Editor.statusLine`, `Editor.dockPanel`            | Build view strings inside the update module. They belong in the view layer.                                                                                                                        |
| F3  | `Workspace.metadata`                               | Returns user-facing strings like `"Ctrl+B tree"`. UI strings leaking into the workspace data module.                                                                                               |
| F4  | `Editor.saveActiveBuffer`                          | Newline conversion (`text.Replace("\n", buffer.Newline)`) lives here. `Buffer` knows the original line ending but doesn't expose a `serialize`. Caller has to remember the convention.             |
| F5  | `type Renderer`, `module Renderer`                 | `Renderer` is a single-field wrapper over `TextWriter`. Adds indirection without reducing complexity for the caller.                                                                               |
| F6  | `Workspace.collapseSelected`                       | Also reparents selection when the entry isn't expandable. Behavior invisible from the name.                                                                                                        |
| F7  | `Runtime.scanNode`                                 | Wraps each child iteration in `try ... with _ -> ()`, so permission-denied folders or unreadable entries silently disappear from the tree. No surfacing to the user.                               |

---

## Phase 0 — Theming ✅

Goal: let the user swap fedit's blue/cyan accent for another color
family — green, yellow, purple, etc. — through a `:theme` command with
tab completion and live preview, without giving up the well-tested
grayscale chrome the editor uses today.

### What "accent" means today

`Layout` hardcodes its colors at the top of `module Layout` (the
`surface`, `chrome`, `accent`, `status`, `commandBar`, `selected`,
`lineNumber`, `currentLineNumber` `let private`s). The chromatic slots
— the only ones that should change with a theme — are:

| Slot                   |        ANSI 256 | Role                                 |
| ---------------------- | --------------: | ------------------------------------ |
| `accent`               | 81 (light cyan) | Dock/panel titles                    |
| `status` bg            |  24 (deep blue) | Status bar background                |
| `selected` bg          |       31 (teal) | Selected row in tree and completions |
| `currentLineNumber` fg | 153 (pale blue) | Gutter highlight on cursor line      |

The grayscale slots (`surface` 252, `chrome` 244, `lineNumber` 241,
command-bar fg/bg 230/237) carry no hue and stay fixed across themes.
They test well across light and dark terminal backgrounds.

### Two possible scopes

**Tier A — accent-family swap (minimum viable, shipped first cut).**
A theme is a record of the four chromatic indices above (plus a
fallback `statusFg` for palettes where white-on-color reads badly, e.g.
yellow backgrounds). Bundled list, hand-picked from the 6×6×6 ANSI
cube so every terminal renders them identically:

| Theme            | accent | status bg | selected bg | current line | status fg |
| ---------------- | -----: | --------: | ----------: | -----------: | --------: |
| `cyan` (default) |     81 |        24 |          31 |          153 |        15 |
| `teal`           |     80 |        23 |          30 |          159 |        15 |
| `green`          |     82 |        22 |          28 |          157 |        15 |
| `yellow`         |    220 |       100 |         178 |          229 |         0 |
| `orange`         |    215 |       130 |         166 |          222 |         0 |
| `red`            |    203 |        88 |         124 |          217 |        15 |
| `magenta`        |    213 |        90 |         127 |          219 |        15 |
| `purple`         |    141 |        54 |          92 |          183 |        15 |

Picked by hand, not derived — contrast on the status bar needs eyes,
not math. The grayscale slots are _not_ part of the record.

**Tier B — full themes from YAML (later, optional).** Shipped as JSON
files in `~/.config/fedit/themes/*.json` during Phase 3 (zero new
dependency vs. YAML).

### Implementation

- [x] `Theme` record + `Themes` module with bundled list, `tryFind`,
      `all`. `cyan` is the default.
- [x] `Layout` color constants replaced with theme-derived values via
      `accentOf`/`statusOf`/`selectedOf`/`currentLineOf`. Grayscale
      slots stay module-level. **F1 folded in:** `Buffer.gutterWidth`
      extracted and used from both `Editor.updateActiveBuffer` and
      `Layout.renderEditor`.
- [x] `Theme of string` added to `Command` DU + `Spec` entry in
      `Commands.specs`.
- [x] `Commands.completions` extended so `theme ` completes against
      bundled (and later user) themes.
- [x] `Theme : Theme` added to `Model`, defaulting to
      `Themes.defaultTheme` in `Editor.init`.
- [x] **Live preview** via `PreviewTheme : Theme option` on
      `CommandBarState`. `Layout.effectiveTheme` resolves the visible
      theme as `model.CommandBar.PreviewTheme |> Option.defaultValue
    model.Theme`. Recomputed in `Editor.updatePreview` on text
      changes and Tab/ShiftTab cycling. Cleared in `closeCommandBar`.
- [x] On `Enter` with `Ready (Theme name)`, set `model.Theme` and
      persist `{"theme": "<name>"}` via `SaveConfig` effect →
      `ConfigSaved` Msg. Failure surfaces as a `Notification.warning`.
- [x] On startup, `Runtime.loadConfig` reads the file via
      `System.Text.Json.JsonDocument` and feeds the theme into
      `Editor.init`. All failure modes fall through to
      `Themes.defaultTheme`.
- [x] `:theme` with no argument resolves to `Pending "Theme name
    required."` so the command bar itself is the picker.

### UX notes

- Live preview recolors the dock panel and command bar background as
  the user Tabs through choices — by design, so the user sees the
  picker in the theme they're about to commit.
- `:help` includes the `theme <name>` line automatically because
  `Commands.helpLines` auto-derives from `Commands.specs`.

### Resolved questions (now closed)

- Cursor restyling (DECSCUSR) — out of scope; the terminal owns the
  cursor.
- 16-color terminal fallback — deferred; fedit emits 256-color codes
  unconditionally. Revisit if someone complains.
- `mono` theme for screenshots / colorblind users — not bundled. Easy
  to add via the JSON theme path (Phase 3 user themes).

---

## Phase 1 — Localized fixes ✅

The point of this phase was to land the smallest, cheapest fixes
first so each diff stayed reviewable and the bigger reshuffle in Phase
2 didn't also encode bug fixes.

**F1 was folded into the Phase 0 Layout rewire** (same function,
same edit). **F2 and F3 ended up landing in-place** rather than
during the Phase 2 split because the deferral cost (reading "F2 ✓
done" vs. "F2 → Phase 2") was higher than the avoided duplicate edit.

- [x] **F4** `Buffer.serialize` added; `Editor.saveActiveBuffer` uses it.
- [x] **F5** `type Renderer` deleted; `enter`/`leave`/`render` take a
      `TextWriter` directly.
- [x] **F6** `Workspace.collapseSelected` split into
      `tryCollapseSelected` (returns `Option`) + `selectParent`,
      composed in `runSidebar`.
- [x] **F7** `Runtime.scanNode` returns `FileNode * int` with skip
      count; surfaced as `"Indexed foo (3 skipped)"` in the
      `WorkspaceLoaded` notification.

---

## Phase 2 — Module reorganization ✅

Split landed. `Program.fs` is now the entry-point-only file; the rest
of the code lives in 13 numbered files under `namespace Fedit`:

| Order | File            | Contents                                                                                                                                      |
| ----: | --------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
|     1 | `Primitives.fs` | `Size`, `Position`, `Severity`, `Notification`, `FocusTarget`, `KeyInput`, `CompletionKind`, `CompletionItem`, `DockPanel`, helpers           |
|     2 | `PieceTable.fs` | `PieceSource`, `Piece`, `PieceTable` type + module                                                                                            |
|     3 | `Buffer.fs`     | `BufferRevision`, `BufferState`, `Buffer` module (incl. `serialize` and `gutterWidth`)                                                        |
|     4 | `Workspace.fs`  | `FileNode`, `WorkspaceEntry`, `WorkspaceState`, `SidebarAction`, `Workspace` module — `metadata` returns structured data, not display strings |
|     5 | `Themes.fs`     | `Theme` type, `Themes` module with bundled palette list                                                                                       |
|     6 | `Commands.fs`   | `Command`, `ParsedCommand`, `CommandContext`, `Commands` module                                                                               |
|     7 | `Model.fs`      | `EditorsState`, `CommandBarState`, `PanelsState`, `Model` (with `Theme : Theme`), `Msg`, `Effect`                                             |
|     8 | `Editor.fs`     | `Editor` module — pure `update`, `init`, helpers. No `statusLine`/`dockPanel`.                                                                |
|     9 | `Screen.fs`     | `Color`, `Style`, `Cell`, `Cursor`, `Screen`, `Style` + `Screen` modules                                                                      |
|    10 | `View.fs`       | `Layout.render`, `statusLine`, `dockPanel`, workspace-row formatting                                                                          |
|    11 | `Renderer.fs`   | `Renderer` module (now takes a `TextWriter` directly post-F5)                                                                                 |
|    12 | `Input.fs`      | `Input.tryMap`                                                                                                                                |
|    13 | `Runtime.fs`    | Effect interpreter, main loop                                                                                                                 |
|    14 | `Program.fs`    | `[<EntryPoint>]` only — argv parsing + `Runtime.run`                                                                                          |

Project file is `src/Fedit/Fedit.fsproj` (post Phase 6). Verified with
`just check` (build + 63-test suite) after the move.

---

## Phase 3 — UX features ✅

- [x] **Find in buffer.** `Ctrl+F` opens search input on the
      command-bar line; live inverted-style highlights; Enter / Up /
      Down navigate matches; Esc dismisses. Implemented via
      `model.Search : SearchState option` and a `Search` focus target.
- [x] **Open recent.** `:recent <path>` picker; persists in
      `~/.config/fedit/config.json` under `"recent"`, top 20 (LRU).
- [x] **Confirm quit on dirty.** `Ctrl+Q` warns when dirty buffers
      exist; press again to discard. `Model.QuitArmed` cleared by any
      other key.
- [x] **Buffer picker.** `:buffers <id-or-name>` picker. Completion
      shows `{id} {name}` with the path as detail.
- [x] **Line-ending indicator.** Status line shows `LF`/`CRLF`. File
      encoding is implicitly UTF-8 without BOM.
- [x] **Word-wise motion.** `Alt+Left`/`Alt+Right` for cursor;
      `Ctrl+Backspace`/`Ctrl+Delete` for word delete. Word boundary
      uses `Char.IsLetterOrDigit || c = '_'`.
- [x] **Selection + clipboard.** `Shift+Arrow`/`Shift+Home`/`Shift+End`
      extend selection; `Ctrl+A` selects all; `Ctrl+C`/`Ctrl+X`/`Ctrl+V`
      pipe through `pbcopy`/`pbpaste` on macOS or `xclip` on Linux
      (chosen by `RuntimeInformation.IsOSPlatform`). Selection state
      lives on `BufferState.Selection : int option`; render path
      highlights selected cells with the theme's `SelectedBg`.
- [x] **Tree refresh on file events.** `Runtime.run` installs a
      `FileSystemWatcher` rooted at the workspace, with the same
      excluded-names filter as the scanner (`.git`/`bin`/`obj`/etc).
      Events set a timestamp; the main loop debounces 300ms then
      dispatches a `WorkspaceChangedExternally` Msg which emits a
      `ScanWorkspace` effect.
- [x] **User themes from JSON (Tier B equivalent).** JSON files in
      `~/.config/fedit/themes/*.json` (zero new dependency vs. YAML).
      `Runtime.loadUserThemes` parses each file via
      `System.Text.Json.JsonDocument`; `Themes.merge` overlays them on
      top of the bundled list (user names override bundled on
      collision). Threaded through `Model.UserThemes`,
      `CommandContext.Themes`, and the live preview. The `theme` Spec
      constructor no longer validates statically — resolution moves to
      `executeCommand` so user themes work without recompiling.
- [x] **Status line polish.** `Ln x/N`, `LF`/`CRLF`, `buf {count}`.
      Dirty count surfaces via the `Ctrl+Q` confirm flow rather than
      cluttering the status line.
- [x] **Mouse support — closed decision: not implementing.** Correct
      handling requires replacing `Console.ReadKey` with a raw stdin
      byte reader so SGR mouse sequences can be parsed atomically.
      `Console.ReadKey` only recognizes well-known control sequences;
      enabling mouse mode without a custom parser would inject
      mouse-sequence chars into the buffer as text on every click.
      Keyboard navigation (`Ctrl+B` + arrows + Enter on the tree,
      `Ctrl+P` for the command picker, `Ctrl+F` for find) covers the
      same surface area. Revisit only with a real user request — at
      which point swap stdin to `System.IO.Pipelines`.

---

## Phase 4 — DX ✅

- [x] **Install recipe.** `just install` / `just uninstall`. Defaults
      to `~/.local/bin`. Publish settings (single-file, self-contained,
      embedded PDBs) source from the fsproj Release PropertyGroup as
      of Phase 6.
- [x] **Tests.** Sibling `tests/Fedit.Tests/` project with Tier 1
      coverage (PieceTable, Buffer, Commands.parse, Workspace,
      Editor.update). 63 tests, 3 FsCheck properties on `PieceTable`
      invariants. Runs via `just test`. Tier 2 and Tier 3 are tracked
      in `TODO.md` as Phase 7 and Phase 8.
- [x] **Format + lint.** `just format` / `just lint` / `just check`
      recipes in `justfile`. `just check` runs lint + build + test.
- [x] **CI.** GitHub Actions workflow at `.github/workflows/ci.yml`:
      Fantomas check on ubuntu-latest; build + test across the
      `{ubuntu, macos, windows}-latest` matrix.
- [x] **`dotnet watch` ergonomics.** Documented the alt-screen-flash
      limitation in the README's `just dev` section; not worth a
      debounce until someone complains.
- [x] **Logging escape hatch.** `--log <path>` flag implemented in
      `main`/`Runtime.run`; writes a UTC-timestamped trace of every
      `Msg` and `Effect` to the file.
- [x] **Crash handler.** `main` wraps `Runtime.run` in try/with; the
      Renderer's existing try/finally already restores the terminal,
      and the outer catch prints a clean error to stderr with exit
      code 1.

---

## Phase 5 — Performance & correctness ✅

Independent of the UI/UX and DX phases. These came out of an
architectural review against modern .NET coding-standards guidance.

- [x] **P1 — Cache derived line data on `BufferState`.** Lines are
      cached on the buffer; reads use the cache directly. Edits go
      through `withDocument` / `changeDocument` which recompute lines
      once. Net: every keystroke no longer rebuilds the entire
      document string several times. Public API
      (`Buffer.lines`/`line`/`lineCount`) unchanged.
- [x] **P2 — Async + cancellation for file I/O.** `Runtime.run` fires
      every `Effect` through `Task.Run`, posting the result `Msg` to
      a `ConcurrentQueue` that the main loop drains every tick.
      `ScanWorkspace` and `LoadFile` each carry a single
      `CancellationTokenSource`; starting a new one cancels the
      previous (the prior task still completes, but its result Msg is
      dropped). Large workspaces and large files no longer freeze
      input.
- [x] **P3 — `[<Struct>]` on small value types.** `Size`, `Position`,
      `Piece`, `Cell`, `Style`, `Cursor`. Saves one heap allocation
      per instance and improves cache behavior. No API changes.
- [x] **P4 — Cap the undo stack.** `Buffer.pushUndo` truncates the
      undo list to 200 entries. The piece table inside each revision
      shares `Original`/`Added` strings by reference, so memory cost
      is mostly the `Pieces` list — but it still grows.
- [x] **P5 — Minor idiom cleanups.** Redundant `|> List.sort` on
      `Map.keys` dropped in `Editor.switchBuffer` (F# `Map` is
      ordered). The newline-replace one-liner that lived in
      `Editor.saveActiveBuffer` is covered by F4.

---

## Phase 6 — .NET conventions & repo hygiene ✅

The codebase worked, built, shipped. This phase met the conventions
an experienced .NET developer would expect when they cloned the repo
— SDK pinning, project layout, shared MSBuild props, publish
settings, editor config, and CI breadth. None of these changed
runtime behavior; they reduce "works on my machine" surprises and
make the project legible to the broader .NET ecosystem.

- [x] **C1 — Pin the SDK with `global.json`.** Added `global.json` at
      the repo root with `sdk.version = "9.0.312"` and
      `rollForward: latestFeature`. Complements the `.dotnet/` shim:
      one pins the version, the other lets a checkout run without a
      system-wide install.
- [x] **C2 — Add `Directory.Build.props` for shared MSBuild settings.**
      Both projects pick up `LangVersion=latest`,
      `TreatWarningsAsErrors=true`, `Nullable=enable`,
      `InvariantGlobalization=true`, `DebugType=embedded`,
      `NeutralLanguage=en-US`. Verified via `dotnet msbuild
    -getProperty`.
- [x] **C3 — Add `.editorconfig`.** Pins Fantomas style explicitly:
      `indent_style = space`, `indent_size = 4`, `end_of_line = lf`,
      `insert_final_newline = true`, `trim_trailing_whitespace =
    true`. Fantomas reads `.editorconfig` and reports clean.
- [x] **C4 — Move publish settings into the project file.**
      `Fedit.fsproj` Release `PropertyGroup` now carries
      `PublishSingleFile`, `SelfContained`, `RuntimeIdentifiers`
      (osx-arm64;osx-x64;linux-x64;linux-arm64;win-x64),
      `IncludeNativeLibrariesForSelfExtract`. The `just install`
      recipe drops its inline `-p:` flags. Verified by publishing for
      osx-arm64 (77MB) and linux-x64 (71MB).
- [x] **C5 — Restructure to `src/Fedit/` + `tests/Fedit.Tests/`.**
      Project names are PascalCase per .NET convention; output binary
      stays lowercase via `AssemblyName`. `ProjectReference`,
      `justfile`, CI workflow, README, and the `fedit` shim all
      updated.
- [x] **C6 — Add a `Fedit.slnx` solution file.** The new XML-based
      `.slnx` format works under SDK 9+. `dotnet build Fedit.slnx`
      and `dotnet test Fedit.slnx` at the repo root pick up both
      projects. `just build` and `just test` point at the solution.
- [x] **C7 — Expand CI to a `{ubuntu, macos, windows}` matrix.**
      Two jobs: `format-check` (Fantomas, ubuntu-only — Windows line
      endings would false-positive) and `build-and-test` across the
      three OS matrix entries.
- [x] **C8 — Repo hygiene tidies.** Empty `docs/` removed.
      `.gitignore` replaced with `dotnet new gitignore` output plus
      local additions. `Fedit.fsproj` carries `Description`, `Authors`,
      `Copyright`, `RepositoryUrl`, `RepositoryType`, and
      `PackageLicenseExpression` metadata. The `fedit` shell shim is
      documented in the README's Quick Start.

---

## Verified post-Phase-6

- `Directory.Build.props` inheritance confirmed via `dotnet msbuild
-getProperty` on both projects.
- `global.json` honored: `dotnet --version` → `9.0.312`.
- `dotnet publish -c Release -r osx-arm64` → 77MB Mach-O arm64
  single-file binary; `-r linux-x64` → 71MB ELF.
- `just install` works without inline publish flags.
- Fantomas reads `.editorconfig` (exit 0 with no changes).
- Embedded PDBs: no `.pdb` files alongside the published binary.
- `dotnet test Fedit.slnx` → 63/63 passing.
- `NoWarn 3261` scoped only to the test project (empty in main).

---

## UX: Command Bar & Dock ✅

Goal: Make the command bar feel like a modern command palette with snappy
navigation and a cleaner visual hierarchy, while reclaiming screen real
estate by hiding the dock panel when not needed.

- [x] **Vertical completion navigation.** Up/Down arrows move the
      selection highlight in the completions list. Tab and Shift+Tab
      continue to work for forward/backward cycling.
- [x] **Alt-based history navigation.** Command history navigation moved
      to Alt+Up and Alt+Down to prevent collision with the completion
      list highlight.
- [x] **Virtual scrolling (Viewport).** The completion list now supports
      vertical scrolling. If the list is longer than the dock height, it
      scrolls so the selected item always stays in view.
- [x] **Visual hierarchy.** Labels in the completion list are rendered
      normally; details (like file paths) are dimmed using the `chrome`
      style to reduce visual noise.
- [x] **Status indicators.** The completion panel title includes a
      position indicator (e.g., `Completions (3/24)`).
- [x] **Slim dock.** The dock panel is now hidden by default (`NoDock`)
      and collapses to 0 height. It appears automatically for
      completions and active commands.
- [x] **Help toggle.** Added a `ShowHelp` flag to the model; toggled
      via the `:help` command. When active, the dock panel shows
      contextual help lines (Shortcuts/Commands) even when the command
      bar is inactive.

---

## View: dim-gray active-line background ✅

The active-line text style used the theme's `SelectedBg` (the saturated
accent), which made text harder to read when the cursor sat on a long
line — the saturation overwhelmed the surface foreground.

- [x] **`currentLineBg`** added as a fixed grayscale style
      (`fg 252, bg 236`) in `View.fs`. Used in place of `selected` when
      rendering the active line. The theme-derived `selected` style
      stays for the sidebar selected row, the completion list, and the
      multi-character text selection highlight — all places where the
      saturated callout is wanted.

The fixed style is intentionally not theme-derived; a source comment
documents why so the value isn't "fixed" by routing it through `Theme`
later.

---

## Buffer: 3-class word motion ✅

Word boundaries used a 2-class predicate (`IsLetterOrDigit || '_'`) and
treated everything else as a single "non-word" run. That collapses code
shapes like `foo.bar(baz)` into one motion stop. Surveyed `../token-editor`
(Rust, IntelliJ-style) and Zed for the standard upgrade.

- [x] **`CharClass` classifier.** Four classes — `Whitespace`, `WordChar`,
      `Punctuation`, `Other` — defined in `Buffer.fs`. `WordChar` keeps
      the existing `IsLetterOrDigit || '_'` (Unicode-aware via .NET).
      `Punctuation` is a hard-coded 31-char ASCII set matching IntelliJ
      and token-editor. `Other` catches non-ASCII symbols (§, ¶, emoji)
      so they don't sneak into `WordChar` runs.
- [x] **Class-transition boundaries.** `wordIndexLeft` / `wordIndexRight`
      stop at any class transition. `foo.bar(baz)` now has stops at
      `foo|`, `.|`, `bar|`, `(|`, `baz|`, `)|` instead of one giant move.
- [x] **`WordMotionLanding` DU.** `WordEnd` (default — matches Zed,
      VSCode, JetBrains, and the previous fedit behavior) lands at the
      end of the current run. `NextWordStart` additionally consumes
      trailing whitespace, landing at the start of the next non-whitespace
      run (vim `w`).
- [x] **Configurable.** `Config.WordMotion : WordMotionLanding` carries
      the preference, persisted in `~/.config/fedit/config.json` as
      `"wordMotion": "wordEnd" | "nextWordStart"`. Unknown / missing
      values fall back to `WordEnd`. No new commands; user edits config
      and restarts.
- [x] **Tests.** 13 new cases in `BufferTests.fs` covering each
      class-transition direction, both landing modes, multi-char
      punctuation runs, underscore-as-word, Unicode letters
      (`Café.txt`), and leading-whitespace skip. Total suite: 82
      tests, all passing.

Out of scope (parked): subword motion (camelCase / snake_case /
kebab-case via `Ctrl+Alt+arrow`, Zed-style); word-selection variants
(`Alt+Shift+arrow`); runtime `:wordmotion` command.
