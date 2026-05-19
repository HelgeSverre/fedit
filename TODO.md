# fedit roadmap

Living plan for cleanup, structural work, and feature growth. Items are
ordered by phase — earlier phases unblock later ones.

## Architecture review findings

All six items are FRICTION severity — no structural breaks, but each one
either hides a latent bug or makes future change more expensive than it
needs to be.

| # | Where | Finding |
|---|-------|---------|
| F1 | `Program.fs:809`, `Program.fs:1364` | Viewport gutter width hardcoded `8` in `Editor.updateActiveBuffer`; `Layout.renderEditor` computes it as `digits + 2`. They will diverge for buffers with few or many lines and produce wrong horizontal scroll. |
| F2 | `Program.fs:1134-1159` | `Editor.statusLine` and `Editor.dockPanel` build view strings inside the update module. They belong in the view layer. |
| F3 | `Program.fs:627-640` | `Workspace.metadata` returns user-facing strings like `"Ctrl+B tree"`. UI strings leaking into the workspace data module. |
| F4 | `Program.fs:907` | Newline conversion (`text.Replace("\n", buffer.Newline)`) lives in `Editor.saveActiveBuffer`. `Buffer` knows the original line ending but doesn't expose a `serialize`. Caller has to remember the convention. |
| F5 | `Program.fs:1186`, `Program.fs:1252` | `Renderer` is a single-field wrapper over `TextWriter`. Adds indirection without reducing complexity for the caller. |
| F6 | `Program.fs:602-610` | `Workspace.collapseSelected` also reparents selection when the entry isn't expandable. Behavior invisible from the name. |

---

## Phase 0 — Theming (next)

Goal: let the user swap fedit's blue/cyan accent for another color
family — green, yellow, purple, etc. — through a `:theme` command with
tab completion and live preview, without giving up the well-tested
grayscale chrome the editor uses today.

### What "accent" means today

`Layout` hardcodes its colors at `Program.fs:1320-1330`. The chromatic
slots — the only ones that should change with a theme — are:

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
- [ ] Replace the `Layout` module-level color constants with values
      derived from a `Theme` parameter (or read from `model.Theme`).
      Grayscale slots stay as module-level constants.
- [ ] Add `Theme of string` to the `Command` discriminated union and a
      `Spec` entry in `Commands.specs` with usage `theme <name>` and a
      validator that rejects unknown names with
      `Invalid "unknown theme '<name>'"`.
- [ ] Extend `Commands.completions` so `theme ` completes against the
      bundled theme names. Reuse the existing `CompletionItem` flow
      (same as `open`).
- [ ] Add `Theme : Theme` to `Model` (or wherever theming lives after
      the Phase 2 reshuffle).
- [ ] **Live preview.** Add `PreviewTheme : Theme option` to
      `CommandBarState`. `Layout.render` resolves the active theme as
      `model.CommandBar.PreviewTheme |> Option.defaultValue model.Theme`.
      Update preview whenever the command bar text or selected
      completion changes:
      - If `Parsed = Ready (Theme name)` and `Themes.tryFind name` is
        `Some`, use that.
      - Else if the selected completion's `ApplyText` parses to a
        known theme, use that.
      - Else `None`.
      Wipe `PreviewTheme` on close, Escape, or successful commit.
- [ ] On `Enter` with `Ready (Theme name)`, set `model.Theme` and
      persist `{"theme": "<name>"}` to `~/.config/fedit/config.json`.
- [ ] On startup, load `~/.config/fedit/config.json` if it exists; map
      `theme` to a `Themes.tryFind` and fall back silently to `cyan`
      when missing or unknown.
- [ ] `:theme` with no argument should resolve to `Pending "Theme name
      required."` so the dock shows the help line and the completion
      list shows every theme — turning the command bar itself into the
      picker, no new UI required.

### UX notes

- Live preview will recolor the dock panel and command bar background
  themselves as the user Tabs through choices. That's a feature, not
  a bug — it lets the user see the picker in the theme they're about
  to commit.
- Themes are *not* listed in the `help` dock today; once Phase 0
  lands, add a short note to `Commands.helpLines` like
  `"theme <name>  Switch accent color (cyan|teal|green|…)"` so the
  list of themes is discoverable from `:help`.

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

- [ ] **F1** Extract `gutterWidth : BufferState -> int` (e.g., as
      `Buffer.gutterWidth` or `Layout.gutterWidth`) and call it from
      both `Editor.updateActiveBuffer` and `Layout.renderEditor`. Drop
      the magic `8`.
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
- [ ] **F7** Surface workspace-scan errors. `Runtime.scanNode`
      (`Program.fs:1456-1481`) wraps each child iteration in
      `try ... with _ -> ()`, so permission-denied folders or unreadable
      entries silently disappear from the tree. At minimum, count
      skipped entries and attach the count to the `WorkspaceLoaded`
      notification (e.g., `"Indexed foo (3 entries skipped)"`). Better:
      return a `FileNode` plus a `string list` of skip reasons so the
      caller can surface them.

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
| 5 | `Commands.fs` | `Command`, `ParsedCommand`, `CommandContext`, `Commands` module |
| 6 | `Model.fs` | `EditorsState`, `CommandBarState`, `PanelsState`, `Model`, `Msg`, `Effect` |
| 7 | `Editor.fs` | `Editor` module — pure `update`, `init`, helpers. **No** `statusLine`/`dockPanel`. |
| 8 | `Screen.fs` | `Color`, `Style`, `Cell`, `Cursor`, `Screen`, `Style` + `Screen` modules |
| 9 | `View.fs` | `Layout.render`, `statusLine`, `dockPanel`, workspace-row formatting (formerly in Editor + Workspace.metadata) |
| 10 | `Renderer.fs` | `Renderer` module (or just `Ansi` functions if F5 deletes the wrapper) |
| 11 | `Input.fs` | `Input.tryMap` |
| 12 | `Runtime.fs` | Effect interpreter, main loop |
| 13 | `Program.fs` | `[<EntryPoint>]` only — argv parsing + `Runtime.run` |

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

- [ ] **Install recipe.** ✅ Landed in `justfile` (`just install`,
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
      `Buffer.rawLines` (`Program.fs:276`) calls `PieceTable.toString`
      and `Split('\n')` every time, and is called transitively by
      `clamp`, `positionToIndex`, `indexToPosition`, `line`,
      `lineCount`, and the renderer's per-frame `Buffer.lines` at
      `Program.fs:1362`. Net effect: every keystroke rebuilds the
      entire document string several times, which defeats the whole
      point of using a piece table. Options: (a) memoize `rawLines` on
      `BufferState` and invalidate inside `changeDocument`; (b) store
      a `LineOffsets : int[]` array on `BufferState` that holds the
      byte index of each `\n`, recomputed only on edit. (b) is faster
      but more work. Either way, the public `Buffer.lines`/`line`/
      `lineCount` API stays the same.

- [ ] **P2 — Async + cancellation for file I/O.**
      `Runtime.runEffect` (`Program.fs:1483`) runs synchronously on
      the UI thread: `File.ReadAllText`, `File.WriteAllText`, and
      `scanNode`'s recursive directory walk all block input. Large
      files or large workspaces freeze the editor. Structural change:
      `Effect` becomes async-returning (e.g., `Task<Msg>` or
      `CancellationToken -> Task<Msg>`), `dispatch` learns to await
      and re-enter, and the main loop tracks in-flight effects with a
      `CancellationTokenSource` so a second `ScanWorkspace` cancels
      the first. Touches `Runtime.dispatch`, the main `while` loop,
      and every `runEffect` branch. Not a quick fix.

- [ ] **P3 — `[<Struct>]` on small value types.**
      These types are small, value-equality already, and copied
      heavily on hot paths. Adding `[<Struct>]` avoids one heap
      allocation per instance and improves cache behavior:
      - `Size` (`Program.fs:6`) — 2 ints, compared with `<>` every
        loop iteration at `Program.fs:1521`.
      - `Position` (`Program.fs:10`) — 2 ints, copied on every cursor
        motion.
      - `Piece` (`Program.fs:80`) — 3 small fields, allocated by every
        piece-table insert/delete.
      - `Cell` (`Program.fs:1171`) — held in `Cell[,]` for the whole
        screen; struct version avoids one heap object per character.
      - `Style` (`Program.fs:1165`) — compared per-cell in
        `Renderer.render` at `Program.fs:1267`.
      - `Cursor` (`Program.fs:1175`).
      Verify: build still passes, and `record { x with Field = y }`
      copy-update syntax keeps working (it does on struct records).
      No API changes for callers.

- [ ] **P4 — Cap the undo stack.**
      `Buffer.pushUndo` (`Program.fs:241`) prepends to
      `buffer.Undo : BufferRevision list` without bound. A long
      editing session grows this list forever. The history list at
      `Program.fs:922` already uses `List.truncate 20` — apply the
      same pattern with a higher cap (suggest 200). Note that the
      piece table inside each revision shares `Original`/`Added`
      strings by reference, so memory cost is mostly the `Pieces`
      list, not the document text — but it still grows.

- [ ] **P5 — Minor idiom cleanups.**
      One-liners, no behavior change:
      - `Program.fs:925` `model.Editors.Buffers |> Map.keys |> Seq.toList |> List.sort`
        — `Map.keys` already returns sorted keys (F# `Map` is ordered).
        Drop the `|> List.sort`.
      - `Program.fs:907` `Buffer.text buffer |> fun text -> text.Replace("\n", buffer.Newline)`
        — once F4 lands as `Buffer.serialize`, this disappears
        entirely. Sequencing note: do F4 first.
      - `Program.fs:412` `Seq.takeWhile ((=) ' ')` — readable as-is;
        leave alone unless someone objects.

## Open questions

- Should buffers persist across runs (session file), or is the
  workspace tree enough?
- Is multi-cursor in scope long-term, or does that change the buffer
  model enough that we should decide before Phase 2 file splitting?
- Plugin / scripting surface — stays out of scope unless someone asks.
