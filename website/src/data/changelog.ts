/**
 * Source: ../../../CHANGELOG.md — hand-maintained mirror.
 *
 * Keep in sync with CHANGELOG.md on each release. DEFERRED: semi-automate
 * draft entries from the GitHub releases.atom feed
 * (https://github.com/HelgeSverre/fedit/releases.atom). No build-time fetch
 * yet — edit this file by hand for now.
 *
 * Note: fedit's CHANGELOG.md is a phase-based "Shipped" log, not a semver
 * keep-a-changelog. There is no Unreleased section and no Added/Fixed/Changed
 * groupings. Each entry below mirrors one row of the Shipped table, newest
 * first; `version` carries the phase label and `date` is "" (unreleased/undated).
 */
export interface ChangelogSection {
  /** e.g. "Added", "Fixed", "Changed". */
  type: string;
  items: string[];
}
export interface ChangelogEntry {
  version: string;
  /** ISO date YYYY-MM-DD, or "" if unreleased. */
  date: string;
  sections: ChangelogSection[];
}

const shipped = (version: string, item: string): ChangelogEntry => ({
  version,
  date: "",
  sections: [{ type: "shipped", items: [item] }],
});

export const changelog: ChangelogEntry[] = [
  shipped(
    "Startup",
    "Cut time-to-first-paint from ~412 ms to ~133 ms (warm, M2 Max). HighlightRegistry loads each tree-sitter grammar lazily on first lookup instead of building all ~25 in tryCreate (~180 ms to ~4 ms); the FileSystemWatcher starts on a background thread after the first frame paints rather than blocking it (~60 ms); PublishReadyToRun precompiles the shipped IL, cutting first-paint JIT. Plugins keep their runtime JIT; crossgen2 cross-targets all five release RIDs.",
  ),
  shipped("Deps", "Bumped FSharp.Core to 10.1.301 and the codecov action via Dependabot."),
  shipped(
    "Terminal",
    "Shortened terminal capability detection (DA1/DA2) from a 500 ms to a 100 ms timeout so startup does not stall on terminals that never reply.",
  ),
  shipped(
    "Perf",
    "Cut per-keystroke cost on the edit and highlight paths. Buffer splices its line cache per edit and shares an append-only add buffer (~13x faster typing, ~27x less allocation at 256 lines). Syntax reparse fires immediately on every keystroke — no debounce — on a background thread, bounded by a 2M-char parse cap and a grammar-less fast path. Hoisted Color.cubeRgb's standard-16 table. Numbers in docs/benchmarks.md.",
  ),
  shipped(
    "Activation",
    'Two append-only PluginAction cases: SetBufferActivation commandName runs a registered command when a line of the active buffer is activated (Enter or left-click), recorded against whatever buffer is active when the action runs; OpenFileAt(path, position, preview) opens a file and moves the cursor to a 1-based position, carrying the target through the async load so it survives the round-trip and applies in place when the file is already open. todo-list now opens a clickable TODO listing (SetBufferActivation + OpenFileAt) instead of a static report. apiVersion stays "1" (append-only).',
  ),
  shipped(
    "Actions",
    'Seven append-only PluginAction cases: OpenFilePreview, RevealPath, ReplaceRange(from, to_, text), ClearSelection, DeleteSelection, SwitchBuffer(id), NewBuffer(name, text). WorkspaceView grows SelectedPath (sidebar selection, absolute) and Files (root-relative sorted index); Buffer.replaceRange becomes public as the ReplaceRange primitive. apiVersion stays "1" under the now-documented append-only rule — new DU cases append at the end, new fields land only on host-constructed records. New examples/jot session scratchpad (:jot/:jotdone/:jotgo) exercises the set; todo-list reports into a NewBuffer scratch driven by Workspace.Files, todo-next continues into other buffers via SwitchBuffer, journal reveals the stamped file with RevealPath. Fedit.PluginApi 1.1.0.',
  ),
  shipped("Site", "Upgraded the website from Astro 5 to Astro 6."),
  shipped(
    "Format",
    "Switched markdown formatting from prettier to oxfmt in the root `just format` / `just lint` recipes (fantomas still owns F#). The website keeps prettier + prettier-plugin-astro under `just website::format`.",
  ),
  shipped(
    "Completions",
    "Expanded `fedit completions` from 3 shells to 9: zsh, bash, fish, pwsh, nushell, elvish, xonsh, yash, murex — plus OSH (Oils) via bash-script reuse. Generated scripts complete subcommands, flags, dynamic plugin names, and the `fedit <path>` positional. `--install` writes each shell's standard location and prints next-step instructions. Verified by a Docker smoke harness (`just test-completions`) loading all nine plus OSH, and parse checks in CI.",
  ),
  shipped(
    "Ayu",
    "Added the `ayu` theme — Ayu Dark palette as the third full-surface theme (after `github-light` / `github-dark`): real backgrounds on every chrome region, `#E6B450` accent, and its own surface-tuned syntax colors. Spec mirror at `brand/themes/ayu.json`. Brings the bundled count to 13.",
  ),
  shipped(
    "Plugins",
    "Hardened plugin name / path handling: validateName / validateFileName / childPath reject empty, dotted, rooted, or separator-bearing names so a plugin name can't escape the plugin root (path traversal). LoadedPlugin gains Conflicts so a session can surface keybinding / command clashes. The Fedit.PluginApi.dll contract now ships as a sidecar (in libexec on Homebrew, beside the binary on just install) so manifest-only plugins resolve it at build time.",
  ),
  shipped(
    "Prompt sessions",
    "Folded the plugin / macro / keybinding list-manager into named prompt sessions backed by a reusable Picker primitive (PickerTypes.fs, Pickers.fs, PromptTypes.fs). ':plugins' / ':macros' / ':keybinds' open a session (Plugins, Macros, Keybindings) instead of a command string; items, action keys, destructive / disabled actions, confirmation, and inspector metadata all render from structured data through one generic action path. Supersedes the standalone picker direction — ListManagerState and its per-manager key handling are gone.",
  ),
  shipped(
    "Grammars",
    "Added five more tree-sitter grammars: AppleScript, ReScript, Zig, Sema, and TOML. Vendored as submodules under vendor/, highlight queries under Resources/queries/<lang>/ (AppleScript's is hand-maintained — upstream ships none), embedded into the binary and registered in Highlight.fs. just download-queries refreshes queries from the submodules; just build-grammars cross-compiles the natives.",
  ),
  shipped(
    "Image",
    "Abstract ImageProtocol type plus Kitty APC graphics implementation (ImageProtocol.fs, KittyImage.fs). Transmit chunks base64 across 4096-byte APC frames with correct m=1/m=0 continuation flags; Clear sends a=d,d=A; QuerySupport probes with a 1x1 transparent PNG query and waits for an OK response. No inline images are wired into the layout yet — the protocol layer is ready for a later phase.",
  ),
  shipped(
    "Color",
    "Renderer.render now downgrades colors based on TerminalCapabilities.ColorSupport. ColorTrueColor passes RGB through; ColorAnsi256 quantizes Rgb to Indexed via Color.toIndexed; ColorAnsi16 maps to the nearest standard palette with Renderer.ansi16Of. Color.cubeRgb is now public so the renderer can reuse the 256-cube lookup for 16-color mapping. Fixed a missing $ on the indexed-background branch that leaked the literal {value} into 48;5;… escapes (surfaced as a stray value}m on 256-color terminals); Renderer.colorToAnsiCode is now the one source of truth and the dead Terminal.Ansi color-code twin was removed.",
  ),
  shipped(
    "Query",
    "Startup terminal capability query: Terminal.detectCapabilities sends DA1 (ESC[c) and DA2 (ESC[>0c) after entering the alternate screen, reads responses with a 500 ms timeout, and merges them with env-based detection via TerminalCapabilities.fromQueries. Known DA2 signatures upgrade kitty, ghostty, wezterm, and iTerm2 with more accurate keyboard protocol, image protocol, and unicode-placeholder flags than env vars alone.",
  ),
  shipped(
    "Mouse",
    "Click-to-place-cursor and drag-to-select in the editor. MouseEvent screen coordinates are mapped to buffer positions via Editor.mouseToBufferPosition, which mirrors the layout arithmetic from View.Layout.render. Left-button press sets the cursor and selection anchor; drag extends the selection; release clears the drag state. MouseDragState tracks the anchor per-buffer. Pressing in the editor also restores Focus = Editor.",
  ),
  shipped(
    "SelectRange",
    "New SelectRange(anchor, cursor) plugin action: the anchor pins one end and the caret lands on the other (mirrors shift+motion), so plugins can select arbitrary ranges instead of only reading or replacing an existing selection. Restores Buffer.setSelection as its primitive; corrects the CursorPosition doc to the real 1-based line+column convention. Forward plan for the rest of the action surface (Tier 1 additive cases, Tier 2 events/async/storage) at docs/plans/2026-06-04-plugin-action-expansion.md. Dead-code sweep alongside: dropped unused Color.rgb and Completions.shellName, plus stray unused bindings.",
  ),
  shipped(
    "Themes CLI",
    "fedit themes --json dumps Themes.all — the same records the editor renders — to JSON, every chrome surface resolved to hex via Color.toHex (null where a theme keeps the terminal default). Mirrors fedit keybinds --json; the website's theme previews consume the generated themes.json. Lives in Cli/Commands/Themes.fs, covered by ThemesCliTests.",
  ),
  shipped(
    "Keybinds",
    "':keybind' opens the effective keymap in a scrollable, searchable buffer instead of dumping every binding into the status line — grouped by context, aligned, deduped to the active binding per stroke, reused in place on repeat. ':keybind <stroke>' now reports on a single line; ':keybind reload' is unchanged.",
  ),
  shipped(
    "Render",
    "Screen.setCell coerces control characters to a space, enforcing the grid invariant that every cell holds one printable column. Fixes multi-line notifications (e.g. the old :keybind dump) writing a raw newline into the single-row status line, which desynced the diff renderer's cursor and corrupted the TUI until a repaint.",
  ),
  shipped(
    "Themes",
    'Chrome is now theme-owned: the previously-hardcoded editor/sidebar text, borders, dock panel, prompt, line numbers, active line, and selection became explicit fg/bg slots on the Theme record, so a palette controls the whole surface. The ten accent-only themes inherit green\'s chrome with default backgrounds, so they render byte-identical. Added the first two full-surface themes — github-light and github-dark (GitHub Primer palettes) — each with its own surface-readable syntax colors. User-theme JSON accepts the new fg/bg keys (and a literal "default") with back-compatible fallbacks.',
  ),
  shipped(
    "Buffers",
    "Buffer-switching keybindings land in the global key handler: Ctrl+PageDown next buffer, Ctrl+PageUp previous, Ctrl+1..9 jump to the buffer at sorted index 1..9. Matches Zed / VS Code / IntelliJ defaults. Out-of-range jumps (Ctrl+5 with 3 buffers open) are a silent no-op rather than a notification. KeyInput grows CtrlPageUp / CtrlPageDown / CtrlDigit of int cases; Input.tryMap recovers the digit value from the contiguous ConsoleKey.D0..D9 range. Legacy macOS Terminal.app may not pass Ctrl+digit through cleanly — flagged as a known limitation; Plan B is Alt+1..9.",
  ),
  shipped(
    "Status",
    "Status bar is now template-driven via Config.StatusFormat. New Status.fs parses [TOKEN] (with optional :modifier) and <EXPAND> (flex spacer) and resolves against the model. Tokens: [MODE], [CURRENT_FILE] / [:short] / [:full], [DIRTY], [LINE], [COLUMN], [LINE_ENDING], [BUFFER] (sorted index/count), [NOTIFICATION]. Unknown tokens render literally so typos surface. Multiple <EXPAND> placeholders share leftover width; odd remainder distributes left-to-right. Default format pushes line/col/encoding/buffer-index to the right edge while leaving notification floating in the middle. Shell-command tokens ([$(cmd)/refresh:5s/truncate:50]) logged as a deferred follow-up.",
  ),
  shipped(
    "Palette",
    "':config' opens ~/.config/fedit/config.json in a buffer, materializing it from the running config on first call so a fresh install gets a useful starting point. Tab in the prompt now applies the highlighted completion's text instead of cycling (:o<Tab> → :open, ready for arguments); cycling moved fully to Up/Down/ShiftTab. Commands.Spec gains a Hidden: bool flag — hidden specs still resolve through parseWith but skip the completion menu and help listing. sidebar / tree / editor flagged hidden since Ctrl+B and Ctrl+E are richer than the typed verbs. Backspace at cursor 0 in the prompt is now a no-op; Esc is the single way out.",
  ),
  shipped(
    "Cli.* ns",
    "Reorganized the CLI corner to follow .NET convention: namespace Fedit.Cli with the parser in Cli.Parser, types at namespace level, and command handlers at Fedit.Cli.Commands.Plugins / Fedit.Cli.Commands.Completions. Folder structure mirrors namespace (src/Fedit/Cli.fs + src/Fedit/Cli/Commands/). Mirrors the shape used by FSharp.Compiler.Service, Fable, Fantomas. The rest of the codebase stays flat — the migration is scoped to the CLI surface where the depth started to pay off.",
  ),
  shipped(
    "Subcmds",
    "fedit plugins <install|remove|list|validate> and fedit completions <zsh|bash|fish> [--install]. Subcommand surface lives in src/Fedit/Cli/Commands/{Plugins,Completions}.fs; the plugins handlers wrap the same Plugins.install/uninstall/discover helpers that back the in-editor :plugin verb so the two paths can't drift. Cli.route adds top-level subcommand routing with hidden aliases. CliCompletionKind (FilePath / DirectoryPath / DynamicCommand / Choices) tags positionals + option values; the completions generator walks the CliCommandDescriptor projection of the real CliApp<_>. fedit plugins list --names exists for shell-side dynamic completion of installed plugin names.",
  ),
  shipped(
    "CLI",
    "src/Fedit/Cli.fs becomes a declarative arg-parser. CliApp<'Option> bundles program name, summary, positionals, and options; Cli.formatHelp / formatUsage / formatErrors are pure-string renderers so --help is generated from the same spec the parser uses. Spec uses Short: char option + Long: string (clap/commander shape). Strict parsing: unknown flags now exit 2 with a Levenshtein-based \"Did you mean '--help'?\" hint instead of being silently swallowed as positionals. --flag=value inline syntax and the -- end-of-flags sentinel both supported. All errors collected per pass (Result<_, CliError list>). 38 tests in CliTests.fs (~94.7% line / 84.3% branch on Cli.fs). New just coverage recipe wires Coverlet + ReportGenerator to produce coverage/index.html.",
  ),
  shipped(
    "Plugin",
    "MVP plugin API. New src/Fedit.PluginApi/ library exposes IPluginHost, PluginCommand, PluginAction, KeyChord. New src/Fedit/Plugins.fs discovers ~/.config/fedit/plugins/<name>/, auto-generates a fsproj if missing, runs dotnet build -c Release, loads the DLL via an isolated AssemblyLoadContext, and reflects out a register : IPluginHost -> unit. Built-in plugin <list/install/remove/reload/validate> command; plugin commands merge into the prompt's surface; plugin keybindings (Ctrl+<char>, Alt+..., F1..F12) dispatch in editor focus. Five reference implementations under examples/: wordcount, journal, plus three TODO: finders (todo-count, todo-list, todo-next). 17 unit tests + one end-to-end build-and-load against the wordcount example. Author guide at docs/plugins.md; marketing/intro page at /plugins on the website.",
  ),
  shipped(
    "Color",
    "Unified Color = Default | Indexed of byte | Rgb of byte*byte*byte. New Color.fs with standard-16 + 26 curated cube picks named for bundled themes, Color.ofHex / tryOfHex, Color.tryOfName, Color.tryParse (hex first, then name), Color.toIndexed quantization. Renderer.sgrColor emits truecolor 38;2;r;g;b directly. Bundled themes rewritten using named statics; visual output pixel-identical. User-theme JSON now expects hex or named colors (breaking — no in-repo user themes use the old integer schema).",
  ),
  shipped(
    "Phase 21",
    "Repo hygiene: SECURITY.md, slim bug_report.md issue template, PULL_REQUEST_TEMPLATE.md.",
  ),
  shipped(
    "Phase 20",
    "CI hardening: Dependabot config (actions + nuget grouped); NuGet cache; concurrency: cancel-in-progress; ContinuousIntegrationBuild=true for deterministic builds. CodeQL deferred to GitHub default code-scanning (no F#-specific queries to gain from a YAML workflow).",
  ),
  shipped(
    "Phase 19",
    "Release automation: tag-triggered release.yml matrix-publishes 5 RIDs, attaches tar.xz/zip + SHA256 sidecars to a GitHub Release, renders + commits the Homebrew formula.",
  ),
  shipped(
    "Phase 18",
    "Central Package Management: Directory.Packages.props at the repo root; Fedit.Tests.fsproj no longer carries versions.",
  ),
  shipped(
    "Phase 17",
    ".NET 10 LTS upgrade: global.json → 10.0.100, both fsproj → net10.0, FSharp.Core 10.0.100, FsCheck.Xunit out of RC. Release workflow's setup-dotnet pin bumped accordingly.",
  ),
  shipped(
    "Phase 16",
    "Buffer.ensureViewport simplified via a shared slideViewport helper. Lines→Offsets cache and delta-based undo deferred — each is a multi-day refactor better done in a dedicated session.",
  ),
  shipped(
    "Phase 15",
    'Unicode │ sidebar separator (U+2502); opt-in Nerd Font file/folder glyphs via "icons": "nerd" (default "off", no first-run glyph breakage).',
  ),
  shipped(
    "Phase 14",
    "Polish: tab width is config-driven (tabWidth, default 4); Recent persists at quit instead of every file-open; theme preview is now derived in View (not stored on PromptState); orphan Workspace.metadata removed.",
  ),
  shipped(
    "Phase 13",
    "Workspace ByPath flat map + pre-sorted children (held Down no longer reads cold on large trees); loadConfig / loadUserThemes return errors that fold into the startup notification instead of silently swallowing.",
  ),
  shipped(
    "Phase 12",
    "Async follow-ups: EditTick-guarded markSaved (no false-clean on concurrent edits); serialized config writes via a single task chain; RunSearch effect with cancellation token (search is no longer synchronous inside update).",
  ),
  shipped(
    "Phase 11",
    "Renderer diff: Renderer.render takes previous: Screen voption and emits cursor jumps + SGR only for changed cells. Style tracked across rows. Typing-quiet frames go from ~30 KB to <100 bytes of ANSI.",
  ),
  shipped(
    "Phase 10",
    "Module splits: BufferRef = ById int | ByName string typed payload for SwitchBuffer; Config.fs (ConfigIO module) carved out of Runtime.fs. The CommandBar / Search split was subsumed by the prompt unification.",
  ),
  shipped(
    "Phase 9",
    "Quick wins: edit primitives compute Lines once per keystroke via finalizeEdit; runEditor cursor motion collapsed onto move / extend / pageJump helpers; jsonEscape was already removed during the config-tunables work.",
  ),
  shipped(
    "Phase 8",
    "Tier 3 binary smoke: --help and --version short-circuits added to Program.fs; 3 smoke tests spawn fedit via dotnet run --no-build. Explicit FSharp.Core package reference so the DLL ships in bin/ for non-SDK invocations.",
  ),
  shipped(
    "Phase 7",
    "Tier 2 snapshot tests: Snapshot.fs projector + 8 scenarios covering focus / prompt mode / sidebar separator / resize. Inline goldens, no Verify dependency.",
  ),
  shipped(
    "Prompt",
    "Unified CommandBarState + SearchState into one PromptState with prefix dispatch — modes derive from the first character (: command/goto, / search, @ buffers, empty file picker). Ctrl+P opens in command mode, Ctrl+O file picker, Ctrl+F search. Ctrl+B is a three-state sidebar toggle (Zed-style). Sidebar gets VS Code / Finder-style type-ahead. Status bar gets 1ch padding; legend strip removed; :help retired. Prompt.fs module + design/modality-explorer.html capture the design path.",
  ),
  shipped(
    "Macros",
    "Record and replay keystroke macros (keybindings phase 4). Ctrl+Shift+M toggles recording into register a, Ctrl+Shift+R replays it, Ctrl+Shift+. repeats the last macro. Recording captures chords (not actions), so replay re-runs live keymap resolution and reassembles sequences; injected keys are bracketed by MacroReplayStart/End markers so a replay is never self-recorded. Status bar shows REC @a while recording. Triggers are modifier chords — bare Char stays text input. In-memory for the session; keybinds-file persistence, stop-on-no-op, and nested replay are deferred.",
  ),
  shipped(
    "Keys",
    "Keybindings: structured Chord/KeyStroke key model decoding the full key universe (Ctrl+Shift, F-keys, Ctrl+arrows); a data-driven keymap (Keymap.fs) with compiled-in defaults overlaid by a user ~/.config/fedit/keybinds file (contexts, multi-key sequences, unbind, run-plugin: bindings, :keybind introspection + reload). Reachable for the first time: Ctrl+O file picker, Ctrl+arrow word motion. Behavior change: plugin keybindings no longer shadow built-in or user keybindings — the user keymap takes precedence.",
  ),
  shipped(
    "Docs",
    "Root just format/lint now runs prettier on **/*.md alongside fantomas. README install section leads with Homebrew; build-from-source demoted. CLAUDE.md added for agent onboarding.",
  ),
  shipped(
    "Dist",
    "Release pipeline: just release <version> tags + pushes. .github/workflows/release.yml cross-builds 5 RIDs (mac arm64/x64 on macos-14, linux arm64/x64, win-x64), publishes .tar.xz/.zip + SHA256 to GitHub Release, renders scripts/fedit.rb.tmpl and auto-commits the updated formula to HelgeSverre/homebrew-tap. Install via brew install helgesverre/tap/fedit.",
  ),
  shipped(
    "Site",
    "website/ Astro 5 + bun: / (features, install, commands, themes, architecture) and /brand (mark, palette, typography, voice). Hero with typing-style SVG mascot, CTA → smooth scroll to install. just website::{dev,build,check,lint,format}.",
  ),
  shipped(
    "Brand",
    "brand/ system: caret SVG mark, Phosphor Green #00B86B accent, Departure Mono (brand) + JetBrains Mono (code), 5 voice rules, do-not gallery. Seven themes (green default, blue, orange, cyan, teal, yellow, red); purple/magenta dropped per brand bans.",
  ),
  shipped(
    "UX",
    "Command Bar & Dock: Vertical completion navigation, virtual scrolling, dimmed details, slim dock (hidden by default), :help toggle.",
  ),
  shipped(
    "Phase 6",
    ".NET conventions: global.json, Directory.Build.props, .editorconfig, publish settings in fsproj, src/ + tests/ restructure, Fedit.slnx, OS-matrix CI, repo hygiene (8/8).",
  ),
  shipped(
    "Phase 5",
    "Performance: P1 line cache, P2 async + cancellation I/O, P3 struct types, P4 undo cap (200), P5 idiom cleanups.",
  ),
  shipped(
    "Phase 4",
    "DX: install recipe, Tier 1 tests (63 passing), CI on {ubuntu, macos, windows}, format/lint, crash handler, --log flag, dotnet watch docs.",
  ),
  shipped(
    "Phase 3",
    "UX: find-in-buffer, open recent, confirm-quit, buffer picker, line-ending indicator, word motion, selection + clipboard, file watcher, user themes (11/11).",
  ),
  shipped(
    "Phase 2",
    "Module reorganization — Program.fs is an entry-point shell; 13 numbered files under namespace Fedit.",
  ),
  shipped(
    "Phase 1",
    "Architecture findings F1–F7 (gutter width, view-string placement, workspace metadata, Buffer.serialize, Renderer wrapper, collapseSelected, scan errors).",
  ),
  shipped(
    "Phase 0",
    ":theme command, 8 bundled palettes, live preview, persistence to ~/.config/fedit/config.json (9/9).",
  ),
];
