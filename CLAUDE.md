# fedit — agent guide

A small terminal text editor written in F#. Pure-data MVU/Elmish loop.
Read [`README.md`](README.md) for the user-facing intro; this file is
for agents (and humans skimming the project conventions).

## Architecture in one minute

```
KeyPressed / Resize → Msg → Editor.update (pure) → (Model', [Effect])
                                                     ↓
                                            runEffect (impure I/O)
                                                     ↓
                                                    Msg → loop
                       Model → Layout.render (pure) → Screen → Renderer (ANSI)
```

- **Model** is pure data (workspace tree, buffers, cursors, focus, theme, panels).
- **Editor.update** is the only place state transitions live. Returns `(Model', Effect list)`.
- **runEffect** is the only impure path — file I/O, clipboard, config writes. Effects post results as `Msg` into a `ConcurrentQueue` drained each tick.
- **Buffers** use a piece table (`PieceTable.fs`); each buffer owns its undo/redo stack.
- **Themes** own the full chrome surface — accent plus an explicit fg/bg per region (editor, gutter, prompt, dock, status, selection, active line). Bundled dark themes set `Default` backgrounds so they keep terminal-default chrome; a light theme supplies real backgrounds.

Source file order (`<Compile>` in `src/Fedit/Fedit.fsproj` is canonical):
`Primitives → Keys → Events → TerminalCapabilities → MouseProtocol → ImageProtocol → KittyImage → PieceTable → Buffer → Workspace → Screen → Color → Themes → Highlight → Commands → Actions → Keymap → Plugins → PluginWire → PluginProtocol → PluginHostClient → PickerTypes → PromptTypes → Model → Config → Pickers → KeymapIO → MacroIO → Prompt → Dock → Editor → Status → Renderer → Input → View → Terminal → Runtime → Cli → Cli/Commands/* → Program`.

`Primitives.fs` also holds `Paths` (`norm`/`parent`): fedit uses a **canonical
forward-slash path model** — normalize any path crossing an OS boundary
(tree scan, file open, workspace root) with `Paths.norm`. .NET's file APIs
accept `/` on Windows, so normalized paths still do real I/O. Don't introduce
`Path.GetDirectoryName`/`GetFullPath` on compared/displayed paths (they emit
`\` / drive-anchor on Windows) — use `Paths.parent` and combine+normalize.

`Dock.fs` owns the dock/editor layout geometry (`Dock.metrics`) shared by
`Editor` (mouse hit-testing) and `View.Layout.render` (painting) — change
layout arithmetic there, never in just one consumer.

## Building & testing

The repo ships a pinned `.dotnet` SDK (10.0.x). Recipes prepend it to `PATH` — never `dotnet` directly outside a recipe; use `just` or invoke
the wrapper script `./fedit`.

```
just check        # lint + build + test — pre-commit gate
just dev .        # dotnet watch on src/Fedit
just run .        # one-shot run
just test         # xUnit + FsCheck (Tier 1)
just bench        # BenchmarkDotNet micro suite (Release, ~4 min; filterable)
just bench-manual # frame-pipeline + tree-sitter parse harness (~1-2 min)
just format       # fantomas + oxfmt on **/*.md
just lint         # check-only of the same
```

Website lives under `website/` (Astro 6 + bun) — see `just website::dev|build|check|lint|format`.

## Brand & themes

Source of truth: [`brand/`](brand/). One symbol (caret), one workhorse
mono (Departure for brand, JetBrains Mono for code), one accent
(`#00B86B`), 13 selectable themes. Brand bans purple/magenta and AI-slop
patterns (Inter, gradients, glassmorphism, bento, centered hero — see
`brand/USAGE.md`).

Themes are implemented in `src/Fedit/Themes.fs`; spec mirrors live in
`brand/themes/*.json`. Adding a new theme = add a record to `Themes.all`
and a matching JSON doc; don't reintroduce banned colors.

Voice rules ([`brand/voice.md`](brand/voice.md)) apply to README, CLI
help, error messages, **commit messages**, and release notes. No emoji,
no marketing adjectives, lead with the verb.

## Release process

```
just release 0.1.0    # tags vX.Y.Z, pushes, triggers CI
```

`.github/workflows/release.yml` builds two flavors across 5 RIDs: the
**default NativeAOT** archives (`fedit-<triple>`, ~7 MB, ~10 ms first
paint — what Homebrew and the installers pull) and the **opt-in R2R
fallback** (`fedit-r2r-<triple>`, self-contained ReadyToRun). NativeAOT
can't cross-compile across OS (and Linux not across arch), so the AOT job
uses native-arch runners (`linux-arm64` on a native ARM runner; `osx-x64`
cross-built from the arm64 macOS runner via the universal toolchain). It
uploads archives + SHA256 sidecars to the GitHub Release, renders
[`scripts/fedit.rb.tmpl`](scripts/fedit.rb.tmpl) via
[`scripts/render-formula.sh`](scripts/render-formula.sh) (which reads the
`fedit-<triple>.sha256` AOT sidecars), and commits the formula to
`HelgeSverre/homebrew-tap`. Requires `HOMEBREW_TAP_TOKEN` (fine-grained
PAT, `Contents: write` on the tap).

Local dry-run of the formula renderer: `just release-formula-preview`.

## Conventions

- **One accent per surface** (web viewport, TUI screen, ~12-char CLI span). Cursor + active mode indicator may both be accent inside the TUI; that's the only exception.
- **Phosphor green fails 4.5:1 vs white** — text on accent backgrounds uses `neutral-900` (encoded in `palette.css` and `palette.fs`; don't override).
- **Mono everywhere** in the website (`.tui` class forces `font-feature-settings: "calt" 0, "liga" 0` to keep box-drawing aligned).
- **Focus rings** use `--accent-soft` (25% accent) at 3px; nav links don't animate underlines on focus (instant outline only — see `Header.astro` comment).
- **Don't bundle fonts into the running TUI** — defer to terminal config. Document JetBrains Mono as the recommendation in README.
- **`NO_COLOR` is intentionally unsupported** in the TUI — themes always render; color fidelity follows the terminal's detected `ColorSupport` (RGB → 256 → 16 → default) instead. (Decision from the theme-system scope cut; older brand docs claiming otherwise are stale.)
- **Don't `cd` in `just` recipes** — use the `{{dotnet}}` prefix; the recipe's working dir is the project root.

## Common gotchas

- `dotnet` outside `.dotnet/` may resolve a different system version and trip `global.json`'s pin to `10.0.100`. Use `just` or `./fedit`.
- Adding files to `src/Fedit/`? Update `<Compile Include="…">` in the fsproj AND commit the file. CI build will fail with `FS0225: Source file could not be found` if either is missed (this has burned us before).
- `prettier-plugin-astro` occasionally needs two passes to stabilize a file; if `just website::lint` flags a freshly-formatted file, run `just website::format` once more.
- `git diff --quiet -- <path>` against an untracked file returns 0 (no diff). When checking for staged formula changes in CI, always `git add` first then check `--cached`.
- macOS Intel runners (`macos-13`) in GitHub Actions can queue for 20+ minutes during peak. Cross-compile from `macos-14` instead — `.NET publish` cross-targets by RID without issue.

## Plugin API

`Fedit.PluginApi` (separate library, `src/Fedit.PluginApi/`) defines
the public contract: `IPluginHost`, `PluginCommand`, `PluginAction`,
`KeyChord`. **Plugins load out-of-process.** The editor (which may ship as
NativeAOT, with no JIT) never loads plugin assemblies itself — it spawns
`Fedit.PluginHost` (`src/Fedit.PluginHost/`, a JIT exe that links
`Plugins.fs`) and talks to it over newline-delimited JSON-RPC. The host runs
the discover → auto-generate-fsproj → `dotnet build -c Release` →
`AssemblyLoadContext` load pipeline (`Plugins.scanAndLoad`) and runs each
command's `Run` closure; only command specs and `PluginAction` lists cross
the wire (serialized reflection-free in `PluginWire`/`PluginProtocol`).
`PluginHostClient` owns the child process editor-side. Plugin commands merge
into the prompt; plugin keybindings dispatch in editor focus (plain `Char`
chords are reserved).

The MVU seam: `ScanPlugins` → `client.Scan` → `PluginsScanned`;
`RunPluginCommand` effect → `client.Invoke` → `PluginActionsReady` →
`applyPluginActions`. The editor's registry carries stub `Run` closures (the
real ones live only in the host).

When touching the plugin pipeline:

- **The host must ship beside the editor** — `PluginHostClient.defaultHostPath`
  looks next to the running binary, then a dev fallback into
  `src/Fedit.PluginHost/bin/<cfg>/net10.0`. `release.yml` and `just aot`
  publish it into the editor's dist; the Homebrew formula installs it. Without
  co-location, plugins silently fail to load.
- The plugin API's `Severity` shadows `Result.Error` in lexical scope —
  use explicit `Result.Error` in `Plugins.fs` / `PluginWire.fs` if you
  re-`open Fedit.PluginApi`.
- `Plugins.fs` depends only on `Fedit.PluginApi` + BCL, so the host links it
  directly. `Fedit.PluginApi.dll` ships beside the host; the auto-generated
  plugin fsproj's HintPath resolves to it.
- Reference implementations in [`examples/`](examples/) — copy any of
  them to `~/.config/fedit/plugins/` to test. End-to-end tests
  (`PluginsTests.fs`, `PluginHostTests.fs`) build + load `wordcount` through
  the host on every `just test`.
- Full author guide in [`docs/plugins.md`](docs/plugins.md).

## Useful docs

- [`README.md`](README.md) — user-facing
- [`CHANGELOG.md`](CHANGELOG.md) — shipped phases
- [`TODO.md`](TODO.md) — active work
- [`docs/plugins.md`](docs/plugins.md) — plugin author guide
- [`brand/USAGE.md`](brand/USAGE.md) — brand do/don't
- [`brand/voice.md`](brand/voice.md) — copy rules
- [`brand/themes/README.md`](brand/themes/README.md) — theme schema
- [`docs/superpowers/`](docs/superpowers/) — forward-looking research + plans (theme system, website style sweep, etc.); shipped plans/specs/research are archived under [`docs/archived/`](docs/archived/)
