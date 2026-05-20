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
- **Themes** are pure accent palettes (5 ANSI int slots) — chrome stays constant across themes.

Source file order (`<Compile>` in `src/Fedit/Fedit.fsproj` is canonical):
`Primitives → PieceTable → Buffer → Workspace → Themes → Commands → Cli → Prompt → Model → Editor → Screen → Renderer → Input → View → Runtime → Program`.

## Building & testing

The repo ships a pinned `.dotnet` SDK (9.0.x). Recipes prepend it to `PATH` — never `dotnet` directly outside a recipe; use `just` or invoke
the wrapper script `./fedit`.

```
just check        # lint + build + test — pre-commit gate
just dev .        # dotnet watch on src/Fedit
just run .        # one-shot run
just test         # xUnit + FsCheck (Tier 1)
just format       # fantomas + prettier on **/*.md
just lint         # check-only of the same
```

Website lives under `website/` (Astro 5 + bun) — see `just website::dev|build|check|lint|format`.

## Brand & themes

Source of truth: [`brand/`](brand/). One symbol (caret), one workhorse
mono (Departure for brand, JetBrains Mono for code), one accent
(`#00B86B`), 7 selectable themes. Brand bans purple/magenta and AI-slop
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

`.github/workflows/release.yml` cross-compiles 5 RIDs, uploads archives
and SHA256 sidecars to GitHub Release, renders
[`scripts/fedit.rb.tmpl`](scripts/fedit.rb.tmpl) via
[`scripts/render-formula.sh`](scripts/render-formula.sh), and commits
the formula to `HelgeSverre/homebrew-tap`. Requires `HOMEBREW_TAP_TOKEN`
(fine-grained PAT, `Contents: write` on the tap).

Local dry-run of the formula renderer: `just release-formula-preview`.

## Conventions

- **One accent per surface** (web viewport, TUI screen, ~12-char CLI span). Cursor + active mode indicator may both be accent inside the TUI; that's the only exception.
- **Phosphor green fails 4.5:1 vs white** — text on accent backgrounds uses `neutral-900` (encoded in `palette.css` and `palette.fs`; don't override).
- **Mono everywhere** in the website (`.tui` class forces `font-feature-settings: "calt" 0, "liga" 0` to keep box-drawing aligned).
- **Focus rings** use `--accent-soft` (25% accent) at 3px; nav links don't animate underlines on focus (instant outline only — see `Header.astro` comment).
- **Don't bundle fonts into the running TUI** — defer to terminal config. Document JetBrains Mono as the recommendation in README.
- **`NO_COLOR=1` must work** — themes are bypassed, neutrals + typography carry hierarchy.
- **Don't `cd` in `just` recipes** — use the `{{dotnet}}` prefix; the recipe's working dir is the project root.

## Common gotchas

- `dotnet` outside `.dotnet/` resolves the system version (often 10.x) and trips `global.json`'s pin to `9.0.312`. Use `just` or `./fedit`.
- Adding files to `src/Fedit/`? Update `<Compile Include="…">` in the fsproj AND commit the file. CI build will fail with `FS0225: Source file could not be found` if either is missed (this has burned us before).
- `prettier-plugin-astro` occasionally needs two passes to stabilize a file; if `lint` flags a freshly-formatted file, run `format` once more.
- `git diff --quiet -- <path>` against an untracked file returns 0 (no diff). When checking for staged formula changes in CI, always `git add` first then check `--cached`.
- macOS Intel runners (`macos-13`) in GitHub Actions can queue for 20+ minutes during peak. Cross-compile from `macos-14` instead — `.NET publish` cross-targets by RID without issue.

## Plugin API

`Fedit.PluginApi` (separate library, `src/Fedit.PluginApi/`) defines
the public contract: `IPluginHost`, `PluginCommand`, `PluginAction`,
`KeyChord`. The host loads plugins from `~/.config/fedit/plugins/<name>/`
on startup via `Plugins.scanAndLoad` (in `src/Fedit/Plugins.fs`),
auto-generates a fsproj if the plugin folder has none, builds with
`dotnet build -c Release`, and loads each DLL into an isolated
`AssemblyLoadContext`. Plugin commands merge into the prompt; plugin
keybindings dispatch in editor focus (plain `Char` chords are reserved).

When touching the plugin pipeline:

- The plugin API's `Severity` shadows `Result.Error` in lexical scope —
  use explicit `Result.Error` in `Plugins.fs` if you re-`open
Fedit.PluginApi`.
- `AppContext.BaseDirectory` is the host's running-binary directory.
  `Fedit.PluginApi.dll` ships alongside via ProjectReference, so the
  auto-generated fsproj's HintPath resolves naturally.
- Reference implementations in [`examples/`](examples/) — copy any of
  them to `~/.config/fedit/plugins/` to test. The end-to-end test
  `PluginsTests.fs` does this against `wordcount` on every `just test`.
- Full author guide in [`docs/plugins.md`](docs/plugins.md).

## Useful docs

- [`README.md`](README.md) — user-facing
- [`CHANGELOG.md`](CHANGELOG.md) — shipped phases
- [`TODO.md`](TODO.md) — active work
- [`docs/plugins.md`](docs/plugins.md) — plugin author guide
- [`brand/USAGE.md`](brand/USAGE.md) — brand do/don't
- [`brand/voice.md`](brand/voice.md) — copy rules
- [`brand/themes/README.md`](brand/themes/README.md) — theme schema
- [`docs/superpowers/`](docs/superpowers/) — forward-looking research + plans (syntax highlighting, etc.)
