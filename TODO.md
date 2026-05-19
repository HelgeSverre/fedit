# fedit roadmap

Living plan for cleanup, structural work, and feature growth. Items are
ordered by phase — earlier phases unblock later ones.

## Status (2026-05-19)

| Area | State |
|------|-------|
| **Phase 0 — Theming** | ✅ Complete (9/9). `:theme <name>` works end-to-end with live preview, persists to `~/.config/fedit/config.json`, restored on startup. |
| **F1** (gutter width) | ✅ Done — folded into Phase 0 Layout rewire. |
| **F2, F3** | Deferred to Phase 2 (rehoused during the file split). |
| **F4, F5, F6, F7** | Not started. Independent of Phase 0; safe to parallelize. |
| **Phase 2 — Module reorg** | Not started; Phase 0 no longer blocks it. |
| **Phase 3 — UX features** | Not started. |
| **Phase 4 — DX: Install recipe** | ✅ Done (`just install` / `just uninstall`). |
| **Phase 4 — DX: Format / format-check / check** | ✅ Done (fantomas recipes in `justfile`). |
| **Phase 4 — DX: Tests / CI / etc.** | Not started; tests scheduled for after Phase 2. |
| **Phase 5 — Performance** | Not started. |

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
Phase 0 Layout rewire (same function, same edit). **F2 and F3 are also
absent** — both are addressed during Phase 2's file split because the
module move rehouses them naturally; doing them now would force the
same code to be touched twice.

- [ ] **F4** Add `Buffer.serialize : BufferState -> string` that applies
      `Newline` conversion. Replace the inline `.Replace("\n", ...)` in
      `Editor.saveActiveBuffer`.
- [ ] **F5** Decide: delete `Renderer` and have `enter`/`leave`/`render`
      take a `TextWriter` directly, OR give it a real responsibility
      (last-style memoization across frames for delta writes). Default
      to delete unless rendering is about to grow.
- [ ] **F6** Rename `Workspace.collapseSelected` to something honest
      (`collapseOrAscend`) or split into `tryCollapseSelected` plus
      `selectParent`, called in sequence from `runSidebar`.
- [ ] **F7** Surface workspace-scan errors. `Runtime.scanNode` wraps
      each child iteration in `try ... with _ -> ()`, so
      permission-denied folders or unreadable entries silently
      disappear from the tree. At minimum, count skipped entries and
      attach the count to the `WorkspaceLoaded` notification (e.g.,
      `"Indexed foo (3 entries skipped)"`). Better: return a
      `FileNode` plus a `string list` of skip reasons so the caller
      can surface them.

## Phase 2 — Module reorganization

Split `Program.fs` (1549 lines) into one file per module. F# requires
files in dependency order; this list is that order. Each file should
stay small enough to navigate without folding.

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

- [ ] **Find in buffer.** `Ctrl+F` opens a single-line search input;
      `n`/`N` jump to next/previous match. Highlights stay until
      `Escape`.
- [ ] **Open recent.** Persist a small recent-files list in
      `~/.config/fedit/state.json`; expose via `:recent` command.
- [ ] **Confirm quit on dirty.** `Ctrl+Q` with any dirty buffer
      requires a confirmation prompt instead of silently exiting.
- [ ] **Buffer picker.** `Ctrl+B` (or a new shortcut) opens a dock list
      of open buffers with arrow-key navigation, beats `next`/`prev`.
- [ ] **Line-ending indicator.** Status line shows `LF`/`CRLF` and the
      file encoding (UTF-8 only today, but surfacing it makes the
      assumption explicit).
- [ ] **Word-wise motion.** `Alt+Left` / `Alt+Right` and word-aware
      `Backspace` (`Ctrl+Backspace`).
- [ ] **Selection + clipboard.** `Shift+Arrow` selects; `Ctrl+C` /
      `Ctrl+X` / `Ctrl+V` against the system clipboard via
      `pbcopy`/`pbpaste` on macOS, `xclip` on Linux.
- [ ] **Tree refresh on file events.** Watch the workspace with
      `FileSystemWatcher` so external edits show up without `Ctrl+R`.
- [ ] **Mouse support.** Optional — `[?1000h` for click-to-place
      cursor and click-to-select tree entries.
- [ ] **Full themes from YAML (Phase 0 Tier B).** Once accent-only
      theming has shipped, promote every `Layout` color slot to a
      named field and load themes from
      `~/.config/fedit/themes/*.yaml`. Enables full palette swaps
      (solarized, gruvbox, etc.) instead of just the accent family.
- [ ] **Status line polish.** Show buffer count, dirty count, and
      `% scrolled` instead of just `Ln/Col`.

## Phase 4 — DX

- [x] **Install recipe.** Landed in `justfile` (`just install`,
      `just uninstall`). Defaults to `~/.local/bin` and produces a
      self-contained single-file binary so the result works on machines
      without .NET installed.
- [ ] **Tests.** Add a sibling `fedit.Tests` project (xUnit + FsUnit)
      covering `PieceTable` (insert/delete/length/toString roundtrip),
      `Buffer` (cursor motion, undo/redo, indent/unindent), and
      `Commands.parse`. Wire into `just test`. **See `TESTING.md` for
      the full three-tier strategy (model + frame snapshot + binary
      smoke) — that's the scope this bullet expands to. TESTING.md
      should be implemented in one focused pass after Phase 2's module
      reorganization lands.**
- [ ] **Format + lint.** `dotnet fantomas .` recipe + a `just check`
      that runs format-verify and build.
- [ ] **CI.** GitHub Actions workflow that runs `just check test` on
      push.
- [ ] **`dotnet watch` ergonomics.** Today `just dev` rebuilds on every
      keystroke and tears down the alt-screen mid-frame. Either
      document the limitation or wire a debounce + clean shutdown.
- [ ] **Logging escape hatch.** A `--log path` flag that appends a
      structured trace of `Msg`/`Effect` for debugging without
      polluting the TUI.
- [ ] **Crash handler.** Wrap `Runtime.run` so an unhandled exception
      restores the terminal before printing the stack trace.

## Phase 5 — Performance & correctness

These items are independent of the UI/UX and DX phases. They came out
of an architectural review against modern .NET coding-standards
guidance — most translate cleanly to F# (immutability, pattern
matching, `Result<_,_>` are already idiomatic here), but the items
below are the ones with real bite. Ordered by impact, not difficulty.

- [ ] **P1 — Cache derived line data on `BufferState`.**
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

- [ ] **P2 — Async + cancellation for file I/O.**
      `Runtime.runEffect` runs synchronously on the UI thread:
      `File.ReadAllText`, `File.WriteAllText`, and `scanNode`'s
      recursive directory walk all block input. Large files or large
      workspaces freeze the editor. Structural change: `Effect`
      becomes async-returning (e.g., `Task<Msg>` or
      `CancellationToken -> Task<Msg>`), `dispatch` learns to await
      and re-enter, and the main loop tracks in-flight effects with a
      `CancellationTokenSource` so a second `ScanWorkspace` cancels
      the first. Touches `Runtime.dispatch`, the main `while` loop,
      and every `runEffect` branch. Not a quick fix.

- [ ] **P3 — `[<Struct>]` on small value types.**
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

- [ ] **P4 — Cap the undo stack.**
      `Buffer.pushUndo` prepends to `buffer.Undo : BufferRevision
      list` without bound. A long editing session grows this list
      forever. `Editor.pushHistory` already uses `List.truncate 20`
      for command history — apply the same pattern with a higher cap
      (suggest 200). Note that the piece table inside each revision
      shares `Original`/`Added` strings by reference, so memory cost
      is mostly the `Pieces` list, not the document text — but it
      still grows.

- [ ] **P5 — Minor idiom cleanups.**
      One-liners, no behavior change:
      - `Editor.switchBuffer`: `model.Editors.Buffers |> Map.keys |>
        Seq.toList |> List.sort` — `Map.keys` already returns sorted
        keys (F# `Map` is ordered). Drop the `|> List.sort`.
      - `Buffer.unindent`: `Seq.takeWhile ((=) ' ')` — readable as-is;
        leave alone unless someone objects.
      (The newline-replace one-liner that lived in
      `Editor.saveActiveBuffer` is covered by F4 and is not listed
      here.)

## Open questions

- Should buffers persist across runs (session file), or is the
  workspace tree enough?
- Is multi-cursor in scope long-term, or does that change the buffer
  model enough that we should decide before Phase 2 file splitting?
- Plugin / scripting surface — stays out of scope unless someone asks.
