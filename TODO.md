# fedit roadmap

Living plan for cleanup, structural work, and feature growth. Items are
ordered by phase — earlier phases unblock later ones.

## Status (2026-05-19)

| Area | State |
|------|-------|
| **Phase 0 — Theming** | ✅ 9/9. |
| **Phase 1 — F1–F7** | ✅ 7/7. |
| **Phase 2 — Module reorganization** | ✅ Done — `Program.fs` is now an entry-point shell; 13 numbered files under `namespace Fedit`. |
| **Phase 3 — UX** | ✅ 11/11. User themes from JSON shipped as Tier-B-equivalent; Mouse is a closed decision (rationale in the Phase 3 section). |
| **Phase 4 — DX** | ✅ Install / Format / Crash handler / Logging / `dotnet watch` docs / Tests / CI all done. |
| **Phase 5 — Performance** | ✅ P1, P2, P3, P4, P5 all done. |
| **Phase 6 — .NET conventions** | ✅ 8/8. |

Recently landed (this session):

- Theme record + 8 bundled palettes (`cyan` default, plus `teal`,
  `green`, `yellow`, `orange`, `red`, `magenta`, `purple`).
- `Buffer.gutterWidth` extracted; viewport math now consistent between
  `Editor.updateActiveBuffer` and `Layout.renderEditor` (F1 fixed).
- `Layout` split into fixed grayscale constants + theme-derived
  chromatic styles via `accentOf`/`statusOf`/`selectedOf`/
  `currentLineOf`, with `effectiveTheme` resolving preview-or-committed.
- `Model.Theme` field, threaded from `Editor.init rootPath size theme`.
- `Command.Theme of string` with validating constructor; `Commands`
  completions and parser updated.
- `CommandBar.PreviewTheme : Theme option` + `Editor.updatePreview`;
  Tab/ShiftTab cycling repaints the whole UI live.
- Persistence: `SaveConfig of themeName` effect + `ConfigSaved`
  Msg, write to `~/.config/fedit/config.json` via `Runtime.saveConfig`
  on every commit; failures surface as a `Notification.warning`.
- Startup load: `Runtime.loadConfig` reads the file via
  `System.Text.Json.JsonDocument` before `Editor.init`, with silent
  fallback to `cyan` on any failure.
- TODO.md and TESTING.md self-consistency pass (findings table now
  includes F7, line refs converted to symbol refs, Phase 2 file table
  includes `Themes.fs`, dead bullets removed).

## Architecture review findings

All seven items are FRICTION severity — no structural breaks, but each
one either hides a latent bug or makes future change more expensive
than it needs to be. References are by symbol so they survive edits;
grep the symbol if a line is wanted.

| # | Where | Finding |
|---|-------|---------|
| F1 | `Editor.updateActiveBuffer`, `Layout.renderEditor` | Viewport gutter width hardcoded `8` in `updateActiveBuffer`; `renderEditor` computes it as `digits + 2`. They will diverge for buffers with few or many lines and produce wrong horizontal scroll. |
| F2 | `Editor.statusLine`, `Editor.dockPanel` | Build view strings inside the update module. They belong in the view layer. |
| F3 | `Workspace.metadata` | Returns user-facing strings like `"Ctrl+B tree"`. UI strings leaking into the workspace data module. |
| F4 | `Editor.saveActiveBuffer` | Newline conversion (`text.Replace("\n", buffer.Newline)`) lives here. `Buffer` knows the original line ending but doesn't expose a `serialize`. Caller has to remember the convention. |
| F5 | `type Renderer`, `module Renderer` | `Renderer` is a single-field wrapper over `TextWriter`. Adds indirection without reducing complexity for the caller. |
| F6 | `Workspace.collapseSelected` | Also reparents selection when the entry isn't expandable. Behavior invisible from the name. |
| F7 | `Runtime.scanNode` | Wraps each child iteration in `try ... with _ -> ()`, so permission-denied folders or unreadable entries silently disappear from the tree. No surfacing to the user. |

---

## Phase 0 — Theming (complete, 9/9) ✅

Goal: let the user swap fedit's blue/cyan accent for another color
family — green, yellow, purple, etc. — through a `:theme` command with
tab completion and live preview, without giving up the well-tested
grayscale chrome the editor uses today.

### What "accent" means today

`Layout` hardcodes its colors at the top of `module Layout` (the
`surface`, `chrome`, `accent`, `status`, `commandBar`, `selected`,
`lineNumber`, `currentLineNumber` `let private`s). The chromatic slots
— the only ones that should change with a theme — are:

| Slot | ANSI 256 | Role |
|------|---------:|------|
| `accent` | 81 (light cyan) | Dock/panel titles |
| `status` bg | 24 (deep blue) | Status bar background |
| `selected` bg | 31 (teal) | Selected row in tree and completions |
| `currentLineNumber` fg | 153 (pale blue) | Gutter highlight on cursor line |

The grayscale slots (`surface` 252, `chrome` 244, `lineNumber` 241,
command-bar fg/bg 230/237) carry no hue and should stay fixed across
themes. They already test well across light and dark terminal
backgrounds.

### Two possible scopes

**Tier A — accent-family swap (minimum viable, recommended first cut).**
A theme is a record of the four chromatic indices above (plus a
fallback `statusFg` for palettes where white-on-color reads badly, e.g.
yellow backgrounds). Ship a small bundled list, hand-picked from the
6×6×6 ANSI cube so every terminal renders them identically:

| Theme | accent | status bg | selected bg | current line | status fg |
|-------|-------:|----------:|------------:|-------------:|----------:|
| `cyan` (default) | 81 | 24 | 31 | 153 | 15 |
| `teal` | 80 | 23 | 30 | 159 | 15 |
| `green` | 82 | 22 | 28 | 157 | 15 |
| `yellow` | 220 | 100 | 178 | 229 | 0 |
| `orange` | 215 | 130 | 166 | 222 | 0 |
| `red` | 203 | 88 | 124 | 217 | 15 |
| `magenta` | 213 | 90 | 127 | 219 | 15 |
| `purple` | 141 | 54 | 92 | 183 | 15 |

Picked by hand, not derived — contrast on the status bar in particular
needs eyes, not math. The grayscale slots are *not* part of the
record.

**Tier B — full themes from YAML (later, optional).** Promote every
`Layout` color slot to a named field, ship bundled themes as embedded
resources, and let users drop additional `~/.config/fedit/themes/*.yaml`
files for things like `solarized-dark` or `gruvbox`. Only worth doing
once Tier A is in use and someone asks for it.

**Decision: ship Tier A first.** The full-palette story is real but
speculative; the accent swap matches the immediate ask.

### Implementation outline

- [x] Add a `Theme` record (Tier A fields) and a `Themes` module
      holding the bundled list plus `tryFind : string -> Theme option`
      and `all : Theme list`. The `cyan` theme is the default.
- [x] Replace the `Layout` module-level color constants with values
      derived from a `Theme` parameter (or read from `model.Theme`).
      Grayscale slots stay as module-level constants. **F1 folded in:**
      `Buffer.gutterWidth` extracted and called from both
      `Editor.updateActiveBuffer` and `Layout.renderEditor`.
- [x] Add `Theme of string` to the `Command` discriminated union and a
      `Spec` entry in `Commands.specs` with usage `theme <name>` and a
      validator that rejects unknown names with
      `Invalid "unknown theme '<name>'"`.
- [x] Extend `Commands.completions` so `theme ` completes against the
      bundled theme names. Reuse the existing `CompletionItem` flow
      (same as `open`).
- [x] Add `Theme : Theme` to `Model` (or wherever theming lives after
      the Phase 2 reshuffle). Defaults to `Themes.defaultTheme` in
      `Editor.init`.
- [x] **Live preview.** Added `PreviewTheme : Theme option` to
      `CommandBarState`. `Layout.effectiveTheme` resolves the visible
      theme as `model.CommandBar.PreviewTheme |> Option.defaultValue
      model.Theme`. Preview is recomputed in `Editor.updatePreview` on
      text changes (via `refreshCommandBar`) and on Tab/ShiftTab
      cycling. Selected completion wins; falls back to `Parsed =
      Ready (Theme name)`; `None` otherwise. Cleared in
      `closeCommandBar` (Escape, successful commit, dismissal).
- [x] On `Enter` with `Ready (Theme name)`, set `model.Theme` and
      persist `{"theme": "<name>"}` to `~/.config/fedit/config.json`.
      Implemented as a `SaveConfig of themeName: string` effect emitted
      from `executeCommand`; `Runtime.runEffect` writes the file and
      replies with a `ConfigSaved of Result<unit, string>` Msg. On
      failure, a `Notification.warning` is surfaced; success is silent
      (the "Theme: <name>" notification is already showing).
- [x] On startup, load `~/.config/fedit/config.json` if it exists; map
      `theme` to a `Themes.tryFind` and fall back silently to `cyan`
      when missing or unknown. `Runtime.loadConfig` reads the file via
      `System.Text.Json.JsonDocument` and feeds the result into
      `Editor.init rootPath size theme`. All failure modes (missing
      file, malformed JSON, unknown theme name, non-string value)
      fall through to `Themes.defaultTheme`.
- [x] `:theme` with no argument resolves to `Pending "Theme name
      required."` so the dock shows the help line and the completion
      list shows every theme — the command bar itself is the picker.
      (Fell out of the constructor; no extra code needed.)

### UX notes

- Live preview will recolor the dock panel and command bar background
  themselves as the user Tabs through choices. That's a feature, not
  a bug — it lets the user see the picker in the theme they're about
  to commit.
- `:help` already includes the `theme <name>` line — `Commands.helpLines`
  auto-derives from `Commands.specs`, so the spec entry I added makes
  this free. No further work needed.

### Open questions

- Should themes also restyle the cursor (DECSCUSR shape/color)?
  Probably not in Tier A; the terminal owns the cursor color.
- If a terminal is detected as 16-color only, fall back to the basic
  16 ANSI colors via a separate mapping? Currently fedit just emits
  256-color codes unconditionally. Defer until someone complains.
- Should the bundled list include a `mono` theme that drops all
  chromatic slots to grayscale? Easy add — useful for screenshots and
  for users with red/green color vision differences.

---

## Phase 1 — Localized fixes (no module moves)

The point of this phase is to land the smallest, cheapest fixes first so
the diff stays reviewable and the bigger reshuffle in Phase 2 doesn't
have to also encode bug fixes.

**F1 is intentionally absent from this list** — it's folded into the
Phase 0 Layout rewire (same function, same edit). **F2 and F3 were
originally tagged for Phase 2 but ended up landing in-place** because
the deferral cost (people reading "F2 ✓ done" vs. "F2 → Phase 2") was
higher than the avoided duplicate edit.

- [x] **F4** `Buffer.serialize` added; `Editor.saveActiveBuffer` uses it.
- [x] **F5** `type Renderer` deleted; `enter`/`leave`/`render` take a
      `TextWriter` directly.
- [x] **F6** `Workspace.collapseSelected` split into
      `tryCollapseSelected` (returns `Option`) + `selectParent`,
      composed in `runSidebar`.
- [x] **F7** `Runtime.scanNode` returns `FileNode * int` with skip
      count; surfaced as `"Indexed foo (3 skipped)"` in the
      `WorkspaceLoaded` notification.

## Phase 2 — Module reorganization ✅

Split landed. `Program.fs` is now the entry-point-only file; the rest
of the code lives in 13 numbered files under `namespace Fedit`:

| Order | File | Contents |
|------:|------|----------|
| 1 | `Primitives.fs` | `Size`, `Position`, `Severity`, `Notification`, `FocusTarget`, `KeyInput`, `CompletionKind`, `CompletionItem`, `DockPanel`, helpers |
| 2 | `PieceTable.fs` | `PieceSource`, `Piece`, `PieceTable` type + module |
| 3 | `Buffer.fs` | `BufferRevision`, `BufferState`, `Buffer` module (incl. new `serialize` and `gutterWidth`) |
| 4 | `Workspace.fs` | `FileNode`, `WorkspaceEntry`, `WorkspaceState`, `SidebarAction`, `Workspace` module — `metadata` returns structured data, not display strings |
| 5 | `Themes.fs` | `Theme` type, `Themes` module with bundled palette list |
| 6 | `Commands.fs` | `Command`, `ParsedCommand`, `CommandContext`, `Commands` module (incl. `Command.Theme of string` after Phase 0) |
| 7 | `Model.fs` | `EditorsState`, `CommandBarState`, `PanelsState`, `Model` (with `Theme : Theme`), `Msg`, `Effect` |
| 8 | `Editor.fs` | `Editor` module — pure `update`, `init`, helpers. **No** `statusLine`/`dockPanel`. |
| 9 | `Screen.fs` | `Color`, `Style`, `Cell`, `Cursor`, `Screen`, `Style` + `Screen` modules |
| 10 | `View.fs` | `Layout.render`, `statusLine`, `dockPanel`, workspace-row formatting (formerly in Editor + Workspace.metadata) |
| 11 | `Renderer.fs` | `Renderer` module (or just `Ansi` functions if F5 deletes the wrapper) |
| 12 | `Input.fs` | `Input.tryMap` |
| 13 | `Runtime.fs` | Effect interpreter, main loop |
| 14 | `Program.fs` | `[<EntryPoint>]` only — argv parsing + `Runtime.run` |

Update `fedit.fsproj` `<Compile Include="..." />` entries to match.

Verification: build + manual smoke test (`just run .`, open a file, edit,
save, quit) before merging the reorganization.

## Phase 3 — UI / UX

Pick from the list, don't try to land all at once. Each is a separate
slice of work.

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
      shows `{id} {name}` with the path as detail. Beats
      `:next`/`:prev` cycling once there are >2 buffers open.
- [x] **Line-ending indicator.** Status line shows `LF`/`CRLF`. (File
      encoding is implicitly UTF-8 without BOM.)
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
- [x] **Mouse support — closed decision: not implementing.**
      Correct handling requires replacing `Console.ReadKey` with a raw
      stdin byte reader so SGR mouse sequences can be parsed
      atomically. `Console.ReadKey` only recognizes well-known control
      sequences; enabling mouse mode without a custom parser would
      inject mouse-sequence chars into the buffer as text on every
      click. Keyboard navigation (`Ctrl+B` + arrows + Enter on the
      tree, `Ctrl+P` for the command picker, `Ctrl+F` for find)
      covers the same surface area. Revisit only with a real user
      request — at which point swap stdin to `System.IO.Pipelines`.
- [x] **User themes from JSON (Phase 0 Tier B equivalent).**
      Implemented as JSON files in `~/.config/fedit/themes/*.json`
      (zero new dependency vs. YAML). `Runtime.loadUserThemes` parses
      each file via `System.Text.Json.JsonDocument` and
      `Themes.merge` overlays them on top of the bundled list (user
      names override bundled on collision). Threaded through
      `Model.UserThemes`, `CommandContext.Themes`, and the live
      preview. The `theme` Spec constructor no longer validates
      statically — resolution moves to `executeCommand` so user
      themes work without recompiling.
- [x] **Status line polish.** `Ln x/N`, `LF`/`CRLF`, `buf {count}`.
      Dirty count surfaces via the `Ctrl+Q` confirm flow rather than
      cluttering the status line.

## Phase 4 — DX

- [x] **Install recipe.** Landed in `justfile` (`just install`,
      `just uninstall`). Defaults to `~/.local/bin` and produces a
      self-contained single-file binary so the result works on machines
      without .NET installed.
- [x] **Tests.** Sibling `fedit.Tests` project with Tier 1 coverage
      (PieceTable, Buffer, Commands.parse, Workspace, Editor.update).
      63 tests, 3 FsCheck properties on `PieceTable` invariants. Runs
      via `just test`. Tier 2 (Verify.Xunit snapshots) and Tier 3
      (binary smoke) tracked in `TESTING.md` as the next-tier
      expansion; the foundation here is enough to back the P2 async
      rewrite and catch regressions in `Buffer` / `Commands` / `update`.
- [x] **Format + lint.** `just format` / `just format-check` / `just
      check` recipes in `justfile`.
- [x] **CI.** GitHub Actions workflow at `.github/workflows/ci.yml`
      runs format-check + build + test on push and pull_request to
      main, ubuntu-latest, .NET 9.
- [x] **`dotnet watch` ergonomics.** Documented the limitation in the
      README's `just dev` section; not worth a debounce until someone
      complains.
- [x] **Logging escape hatch.** `--log <path>` flag implemented in
      `main`/`Runtime.run`; writes a UTC-timestamped trace of every
      `Msg` and `Effect` to the file.
- [x] **Crash handler.** `main` wraps `Runtime.run` in try/with; the
      Renderer's existing try/finally already restores the terminal,
      and the outer catch prints a clean error to stderr with exit
      code 1.

## Phase 5 — Performance & correctness

These items are independent of the UI/UX and DX phases. They came out
of an architectural review against modern .NET coding-standards
guidance — most translate cleanly to F# (immutability, pattern
matching, `Result<_,_>` are already idiomatic here), but the items
below are the ones with real bite. Ordered by impact, not difficulty.

- [x] **P1 — Cache derived line data on `BufferState`.**
      `Buffer.rawLines` calls `PieceTable.toString` and `Split('\n')`
      every time, and is called transitively by `clamp`,
      `positionToIndex`, `indexToPosition`, `line`, `lineCount`, and
      the renderer's per-frame `Buffer.lines` inside
      `Layout.renderEditor`. Net effect: every keystroke rebuilds the
      entire document string several times, which defeats the whole
      point of using a piece table. Options: (a) memoize `rawLines`
      on `BufferState` and invalidate inside `changeDocument`; (b)
      store a `LineOffsets : int[]` array on `BufferState` that holds
      the byte index of each `\n`, recomputed only on edit. (b) is
      faster but more work. Either way, the public `Buffer.lines`/
      `line`/`lineCount` API stays the same.

- [x] **P2 — Async + cancellation for file I/O.** Done. `Runtime.run`
      now fires every `Effect` through `Task.Run`, posting the result
      `Msg` to a `ConcurrentQueue` that the main loop drains every
      tick. `ScanWorkspace` and `LoadFile` each carry a single
      `CancellationTokenSource`; starting a new one cancels the
      previous (the prior task still completes, but its result Msg is
      dropped). Net effect: large workspaces and large files no
      longer freeze input. Verified with the 63-test xUnit suite +
      manual smoke.

- [x] **P3 — `[<Struct>]` on small value types.**
      These types are small, value-equality already, and copied
      heavily on hot paths. Adding `[<Struct>]` avoids one heap
      allocation per instance and improves cache behavior:
      - `Size` — 2 ints, compared with `<>` every loop iteration in
        `Runtime.run`.
      - `Position` — 2 ints, copied on every cursor motion.
      - `Piece` — 3 small fields, allocated by every piece-table
        insert/delete.
      - `Cell` — held in `Cell[,]` for the whole screen; struct
        version avoids one heap object per character.
      - `Style` — compared per-cell in `Renderer.render`.
      - `Cursor`.
      Verify: build still passes, and `record { x with Field = y }`
      copy-update syntax keeps working (it does on struct records).
      No API changes for callers.

- [x] **P4 — Cap the undo stack.** Capped at 200 entries.
      `Buffer.pushUndo` prepends to `buffer.Undo : BufferRevision
      list` without bound. A long editing session grows this list
      forever. `Editor.pushHistory` already uses `List.truncate 20`
      for command history — apply the same pattern with a higher cap
      (suggest 200). Note that the piece table inside each revision
      shares `Original`/`Added` strings by reference, so memory cost
      is mostly the `Pieces` list, not the document text — but it
      still grows.

- [x] **P5 — Minor idiom cleanups.** Redundant `|> List.sort` on
      `Map.keys` dropped.
      One-liners, no behavior change:
      - `Editor.switchBuffer`: `model.Editors.Buffers |> Map.keys |>
        Seq.toList |> List.sort` — `Map.keys` already returns sorted
        keys (F# `Map` is ordered). Drop the `|> List.sort`.
      - `Buffer.unindent`: `Seq.takeWhile ((=) ' ')` — readable as-is;
        leave alone unless someone objects.
      (The newline-replace one-liner that lived in
      `Editor.saveActiveBuffer` is covered by F4 and is not listed
      here.)

## Phase 6 — .NET conventions & repo hygiene

The codebase works, builds, ships. This phase is about meeting the
conventions an experienced .NET developer would expect when they clone
the repo — SDK pinning, project layout, shared MSBuild props, publish
settings, editor config, and CI breadth. None of these change runtime
behavior; they reduce "works on my machine" surprises and make the
project legible to the broader .NET ecosystem.

Items are ordered by ROI (impact ÷ disruption). C1–C3 are contained and
high-signal; C4–C6 touch repo structure and are best landed together;
C7–C8 are optional polish.

- [x] **C1 — Pin the SDK with `global.json`.**

      *Context:* `fedit.fsproj` pins `net9.0` (the *target framework*) but
      nothing pins the *SDK* used to build it. A contributor with .NET 10
      preview installed will silently build against that toolchain; a
      contributor with only .NET 8 will get a confusing restore error.
      The presence of a hand-maintained `.dotnet/` shim and the
      `PATH="$PWD/.dotnet:$PATH"` prefix in the `justfile` shows we
      already care about toolchain control — `global.json` is the
      standard way to express it.

      *Action:*
      - Add `global.json` at the repo root with `sdk.version` set to the
        current local `dotnet --version` (likely `9.0.x`) and
        `rollForward: latestFeature` so patch-level upgrades work
        transparently.
      - Mention it in the README's "Build from source" section.
      - Leave `.dotnet/` and the `PATH` prefix alone; they are
        complementary (one pins the version, the other lets a checkout
        run without a system-wide install).

- [x] **C2 — Add `Directory.Build.props` for shared MSBuild settings.**

      *Context:* Properties like `TreatWarningsAsErrors`, `LangVersion`,
      `Nullable`, and `InvariantGlobalization` belong on both projects
      (`fedit.fsproj` and `fedit.Tests/fedit.Tests.fsproj`). Duplicating
      them in two `.fsproj` files invites drift; `Directory.Build.props`
      at the repo root applies to every project beneath it.

      *Action:* Create `Directory.Build.props` with:
      - `<LangVersion>latest</LangVersion>`
      - `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
      - `<Nullable>enable</Nullable>` (F# honors it for C#-interop
        signatures and IDE tooling)
      - `<InvariantGlobalization>true</InvariantGlobalization>` — fedit
        does no culture-sensitive formatting; this trims published size
        and avoids ICU dependency drift.
      - `<DebugType>embedded</DebugType>` so single-file publish embeds
        PDBs (better stack traces on crash with no extra files).

      Then remove any of these now-duplicated lines from the two project
      files.

- [x] **C3 — Add `.editorconfig` so Fantomas style is explicit.**

      *Context:* Fantomas reads `.editorconfig` for `fsharp_*` style
      keys. Without one, formatting follows Fantomas defaults — which
      *currently* matches our source, but is implicit and will drift
      silently if Fantomas changes its defaults in a future release. CI
      runs `fantomas --check`, so a default-change would break main with
      no obvious cause.

      *Action:* Add `.editorconfig` at the repo root with at least:
      - `root = true`
      - `[*.fs]` block setting `indent_style = space`, `indent_size = 4`,
        `end_of_line = lf`, `insert_final_newline = true`,
        `trim_trailing_whitespace = true`.
      - Any `fsharp_*` keys we want to pin (look at current Fantomas
        output and lock in the values we already follow — e.g.
        `fsharp_max_value_binding_width`,
        `fsharp_multiline_bracket_style`).

      Run `dotnet fantomas .` once after adding to confirm no diff.

- [x] **C4 — Move publish settings from the `justfile` into the project file.**

      *Context:* `just install` passes `-p:PublishSingleFile=true
      --self-contained true` on the command line. Anyone running
      `dotnet publish` directly (the documented .NET workflow) gets a
      different artifact. The properties should live in `fedit.fsproj`
      so `dotnet publish -c Release` produces the right thing by
      default; the justfile recipe stays as a convenience wrapper.

      *Action:*
      - Add to `fedit.fsproj` (inside a new `<PropertyGroup>` guarded
        by `Condition="'$(Configuration)' == 'Release'"` so debug
        builds stay fast):
        - `<PublishSingleFile>true</PublishSingleFile>`
        - `<SelfContained>true</SelfContained>`
        - `<RuntimeIdentifiers>osx-arm64;osx-x64;linux-x64;win-x64</RuntimeIdentifiers>`
        - `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>`
      - Drop `-p:PublishSingleFile=true --self-contained true` from the
        `just install` recipe; keep `-c Release` and `-o bin/dist`.
      - Verify `just install` still produces an identical single-file
        binary.

- [x] **C5 — Restructure to `src/Fedit/` + `tests/Fedit.Tests/`.**

      *Context:* The convention in the .NET ecosystem is `src/` for
      application/library projects and `tests/` for test projects. Right
      now `fedit.fsproj` and its 14 `.fs` files sit at the repo root,
      mixed with README/LICENSE/TODO/justfile/`.config/`/`bin/`. A fresh
      contributor sees a wall of files at the root and has to mentally
      separate "code" from "repo metadata". The current layout also
      makes the test project's `..\fedit.fsproj` reference an asymmetric
      path.

      Project names should be PascalCase per .NET convention. The output
      binary stays lowercase via `AssemblyName`.

      *Action:*
      - `git mv` all 14 `.fs` files + `fedit.fsproj` into `src/Fedit/`
        and rename to `Fedit.fsproj`.
      - `git mv fedit.Tests` to `tests/Fedit.Tests/` and rename its
        project file to `Fedit.Tests.fsproj`.
      - Update the `ProjectReference` in the test project to the new
        path (`..\..\src\Fedit\Fedit.fsproj`).
      - Update `justfile` paths (`project := "src/Fedit/Fedit.fsproj"`,
        test path, install output).
      - Update `.github/workflows/ci.yml` paths.
      - Update README and the `fedit` shell shim if it hardcodes the
        path.
      - Confirm `AssemblyName` in the new project file is still `fedit`
        (lowercase) so the binary name doesn't change.

      Land this in one commit; mixed renames + content edits are hard
      to review.

- [x] **C6 — Add a `Fedit.slnx` solution file.**

      *Context:* `dotnet build` / `dotnet test` at the repo root with no
      solution file will fail or pick the wrong project. JetBrains
      Rider, VS Code's C# Dev Kit, and Visual Studio all expect a
      solution. `.slnx` is the new XML-based format that supersedes
      `.sln`, supported by SDK 9+.

      *Action:*
      - After C5 lands, run `dotnet sln Fedit.slnx add src/Fedit/Fedit.fsproj
        tests/Fedit.Tests/Fedit.Tests.fsproj` (or hand-write the small
        `.slnx`; it is only a few lines).
      - Verify `dotnet build` and `dotnet test` at the repo root with
        no arguments do the right thing.
      - Optionally update the `justfile` to point at `Fedit.slnx`
        instead of the project file for the `build` / `test` recipes,
        so `dotnet` picks up both projects.

- [x] **C7 — Expand CI to a `{ubuntu, macos, windows}` matrix.**

      *Context:* fedit is a TUI built on `System.Console`. Behavior
      around raw mode, key encoding, and ANSI escape handling differs
      meaningfully across platforms. CI currently runs on
      `ubuntu-latest` only. A trivial matrix build catches compile-time
      platform issues (P/Invoke signatures, path separators) cheaply,
      even without running the editor itself.

      *Action:*
      - In `.github/workflows/ci.yml`, change `runs-on: ubuntu-latest`
        to a `strategy.matrix.os: [ubuntu-latest, macos-latest,
        windows-latest]` and use `runs-on: ${{ matrix.os }}`.
      - Leave the format-check step pinned to `ubuntu-latest` only
        (line-ending differences will produce false positives on Windows
        otherwise).
      - Consider a separate `release.yml` triggered on tag pushes that
        runs `dotnet publish` per RID and uploads the binaries to a
        GitHub Release.

- [x] **C8 — Repo hygiene tidies.**

      *Context:* Small things that accumulated as the project grew —
      none individually worth a phase, collectively worth one commit.

      *Action items:*
      - Delete the empty `docs/` directory, or move `TESTING.md` and
        `TODO.md` into it (pick one — the root currently has both an
        empty `docs/` and three top-level `*.md` files, which is the
        worst of both worlds).
      - Replace the hand-rolled `.gitignore` with the output of
        `dotnet new gitignore` (covers `TestResults/`, `*.binlog`,
        `.vs/`, IDE-specific swap files, etc.).
      - Add `Description`, `Authors`, `RepositoryUrl`, and
        `PackageLicenseExpression` properties to `fedit.fsproj` even
        though we don't pack — they show up in `dotnet --info`,
        crash-dump metadata, and `dotnet list package`, and are the
        first place a curious developer looks.
      - Document the `fedit` shell shim in the README's install
        section. It exists, it works, and right now nobody knows.

## Open questions

- Should buffers persist across runs (session file), or is the
  workspace tree enough?
- Is multi-cursor in scope long-term, or does that change the buffer
  model enough that we should decide before Phase 2 file splitting?
- Plugin / scripting surface — stays out of scope unless someone asks.
