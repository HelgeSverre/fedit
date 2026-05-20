# fedit roadmap

Active work and future ideas. Shipped phases (0–6) live in
[`CHANGELOG.md`](CHANGELOG.md).

## Status

| Phase                                             | State   | Hook                                                                                                   |
| ------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------------ |
| **Phase 7 — Tier 2 frame snapshots**              | Shipped | In-house `Snapshot.fs` projector (style markers + cursor footer); 8 scenario tests covering focus/prompt/sidebar/resize. |
| **Phase 8 — Tier 3 binary smoke**                 | Shipped | `dotnet run` from xunit covers `--help` / `-h` / `--version` short-circuits. Interactive scenarios deferred. |
| **Phase 9 — Quick wins**                          | Shipped | Buffer double-computeLines fixed; `jsonEscape` removed earlier (config DOM); motion helper landed.     |
| **Phase 10 — Module splits**                      | Shipped | `BufferRef` DU for `SwitchBuffer` (10.1); `Config.fs` split out of `Runtime.fs` (10.3). 10.2 prompt split already subsumed by `Prompt.fs` from the Phase 1 unification. |
| **Phase 11 — Renderer diff**                      | Shipped | `Renderer.render` now takes `previous: Screen voption` and emits cursor jumps + SGR only for changed cells. `pad`/`crop` follow-up deferred. |
| **Phase 12 — Async follow-ups**                   | Shipped | EditTick-guarded `markSaved`; serialized config writes via task chain; `RunSearch` effect with cancellation. |
| **Phase 13 — Workspace caching & startup errors** | Shipped | `WorkspaceState.ByPath` map + pre-sorted children; `loadConfig`/`loadUserThemes` return errors folded into the startup notification. |
| **Phase 14 — Polish**                             | Shipped | Theme preview is now derived in View; Recent persists at quit; tab width configurable; metadata removed (unused). |
| **Phase 15 — Borders and file-tree icons**        | Shipped | Sidebar separator is now `│` (U+2502). `icons` config field enables Nerd Font file/folder glyphs (opt-in, default off). |
| **Phase 16 — Buffer internals refactor**          | Pending | Simplify `ensureViewport`; replace `Lines` cache with `Offsets : int[]`; delta-based undo.             |
| **Phase 17 — .NET 10 LTS upgrade**                | Pending | Bump SDK to `10.0.x`, TFM to `net10.0`, refresh test packages and `FsCheck.Xunit` out of RC.           |
| **Phase 18 — Central Package Management**         | Shipped | `Directory.Packages.props` at repo root; `Fedit.Tests.fsproj` no longer carries versions.              |
| **Phase 19 — Release automation**                 | Shipped | `release.yml` ships matrix publish (5 RIDs), tar.xz/zip + SHA256 sidecars, GitHub Release, homebrew tap update. |
| **Phase 20 — CI hardening**                       | Shipped | Dependabot config; NuGet cache step; concurrency cancel; ContinuousIntegrationBuild env + det. props. CodeQL → repo Settings (default scan). |
| **Phase 21 — Repo hygiene**                       | Shipped | Badges already present; added `SECURITY.md`, bug-report issue template, PR template. FUNDING/CODEOWNERS deferred. |

Recently landed (this session):

- **Command bar completions UX:** Vertical navigation via Up/Down arrows,
  virtual scrolling (viewport) for long completion lists, and a dimmed visual
  style for details (e.g., file paths) to reduce noise.
- **Slim dock bar:** The dock panel is now hidden by default (`NoDock`) and
  collapses to 0 height. It only appears for completions, active commands, or
  when explicitly toggled via the new `:help` command.
- **Config tunables:** `~/.config/fedit/config.json` now carries
  `completionLimit`, `sidebarIndent`, `sidebarWidth`, `dockHeight`, and
  `wordMotion` alongside `theme` / `recent`. `saveConfig` round-trips
  through a `JsonObject` DOM so user-added unknown keys survive. Each int
  is clamped to a sane range. See README → Configuration.
- **`:LINE` / `:LINE:COL` jump:** Numeric command-bar input now jumps the
  cursor to an absolute 1-based position (`:42`, `:100:6`). Malformed forms
  (`:0`, `:42:`, `:1:2:3`) produce `Invalid` messages instead of falling
  through to "unknown command".
- **Phase 9 quick wins:** `Buffer.replaceRange` and the six other edit
  paths (`backspace`, `deleteForward`, `backspaceWord`, `deleteForwardWord`,
  `unindent`, `deleteSelection`) used to compute `Lines` twice per
  keystroke — once via `withDocument` for cursor math, once via
  `changeDocument` for the final state. Replaced both with a single
  `finalizeEdit` helper that reuses the already-computed `Lines`. Dropped
  the now-orphaned `changeDocument` and `pushUndo`. `runEditor`'s cursor-
  motion branches collapsed onto two helpers (`move` / `extend`) plus a
  `pageJump` for page motion. (`jsonEscape` was already removed during
  the config-tunables work.)

---

## Phase 7 — Tier 2 frame snapshots

**Goal.** Catch the bugs Tier 1 (pure model tests) can't see: gutter-width
drift, off-by-one in viewport math, status-line truncation, command-bar
cursor position, focus-target coloring, selection highlight ranges,
search highlight overlap. The rendered `Cell[,]` grid is the only data
structure that contains all of those at once.

### Why this shape

fedit's architecture lines up exactly with the testing pattern the
Elm / Bubble Tea / Textual communities have converged on:

- `Editor.update : Msg -> Model -> Model * Effect list` is pure.
- `Layout.render : Model -> Screen` is pure (`Screen.Cells : Cell[,]`).
- `Effect` is a closed sum, asserted on directly — no mocking required.
- The runtime loop (`Runtime.run`) is the only impure boundary, and
  it's ~50 lines of input decoding + ANSI emission that change rarely.

That means **a snapshot of the rendered grid is e2e** for everything
except the runtime loop. No virtual terminal, PTY, or VT500 emulator
needed to catch layout, cursor, viewport, status-line, or command-bar
regressions.

### Project layout addition

```
tests/Fedit.Tests/
  Snapshot.fs                       # new — Cell[,] -> string projector
  SnapshotTests.fs                  # new — Verify.Xunit scenarios
  Snapshots/                        # new — .verified.txt goldens
    SnapshotTests.cold_start.verified.txt
    SnapshotTests.opened_file.verified.txt
    ...
```

Add to `tests/Fedit.Tests/Fedit.Tests.fsproj`:

```xml
<PackageReference Include="Verify.Xunit" Version="28.*" />
```

### The `Snapshot.render` projector

```fsharp
module Snapshot

let private styleMarker (style: Style) =
    // Compact marker so style changes are visible in diffs without
    // dominating them. Example: "[31/24 B]" for fg=31, bg=24, bold.
    ...

let render (screen: Screen) : string =
    let sb = StringBuilder()
    sb.AppendLine($"=== {screen.Width}x{screen.Height} ===") |> ignore
    for row in 0 .. screen.Height - 1 do
        let mutable lastStyle = None
        for col in 0 .. screen.Width - 1 do
            let cell = screen.Cells[row, col]
            if Some cell.Style <> lastStyle then
                sb.Append(styleMarker cell.Style) |> ignore
                lastStyle <- Some cell.Style
            sb.Append(cell.Glyph) |> ignore
        sb.AppendLine() |> ignore
    // Append cursor position footer
    sb.ToString()
```

### Stability requirements

Get these wrong and snapshots churn:

- **Pin terminal size per test** (e.g., `Size = { Width = 80; Height = 24 }`).
- **Normalize trailing whitespace** (rstrip per line) — otherwise
  `PadRight` inserts trailing spaces that diff noisily.
- **Include cursor `{Left, Top, Visible}`** in the snapshot footer.
- **Style markers must be deterministic** — sort fields, don't rely on
  record default formatting.

### Scenarios to cover

Each is `init → fold msgs → render → snapshot`:

1. Cold start with empty workspace.
2. Opened file, editor focus, cursor at line 3 col 5.
3. Sidebar focused, file tree expanded one level.
4. Command bar active with `:o` typed, completions visible.
5. Command bar active with `:theme yel` typed (covers theme preview).
6. Dirty buffer in status line.
7. Buffer scrolled horizontally and vertically.
8. Notification banner showing each `Severity` (Info / Warning / Error).
9. Search active with multiple matches, second match highlighted.
10. Selection spanning multiple lines.

### Implementation checklist

- [ ] Add `Verify.Xunit` package reference to `Fedit.Tests.fsproj`.
- [ ] Build `Snapshot.fs` with `styleMarker` + `render` helpers.
- [ ] Write 8–10 baseline scenario tests in `SnapshotTests.fs`.
- [ ] Run, inspect `*.received.txt` files, accept via `dotnet verify
accept` or rename to `*.verified.txt`.
- [ ] Confirm `dotnet test` runs them as part of the normal suite —
      no new wiring required.

### Why not Model assertions instead?

Tier 1 already does that. Tier 2's job is catching the bugs that
pass Tier 1 but render wrong — gutter width drift (F1 in CHANGELOG
findings), off-by-one in viewport math, status line truncation,
command-bar cursor position. The rendered grid is the only data
structure that contains all of those at once.

### Rejected alternatives

- **Pty.Net** — works, but you'd own a VT emulator yourself.
- **XtermSharp** — unmaintained.
- **vtnet** — unmaintained.
- **expect/pexpect** — race-prone against repaints; ANSI-blind.
- **tmux send-keys + capture-pane** — robust elsewhere, but adds tmux
  as a CI dependency and the rendered frame is what we'd assert on
  anyway — Verify.Xunit gives us that in-process.

The .NET TUI testing ecosystem is genuinely thin. The architectural
purity of `update` + `render` lets us route around it.

---

## Phase 8 — Tier 3 binary smoke

**Goal.** Prove the actual `fedit` executable launches and quits
cleanly. Catches "binary doesn't launch" / "binary doesn't shut down"
regressions that Tier 1 + Tier 2 can't — they exercise the pure parts.
This tier crosses the `Runtime.run` boundary that Tiers 1 and 2 stop
at.

### Approach: `Process.Start` exit-code checks

Decision locked in: start with `Process.Start` (zero new tooling, runs
inside the same `dotnet test` invocation). Promote to charmbracelet/vhs
later if/when a README demo GIF is wanted — at that point the same
`.tape` files double as test scripts and documentation artifacts.

```fsharp
[<Fact>]
let ``binary launches and exits cleanly`` () =
    use proc = Process.Start(...)
    proc.StandardInput.Write("")  // Ctrl+Q
    proc.StandardInput.Close()
    proc.WaitForExit(5000) |> ignore
    proc.ExitCode |> shouldEqual 0
```

### Scenarios (cap at 5)

1. Cold start → quit cleanly with `Ctrl+Q`.
2. Open a known file via `Ctrl+P open <path>` → quit cleanly.
3. Theme switch via `Ctrl+P theme green` → quit cleanly.
4. Save a scratch buffer via `Ctrl+P writeas <tmp>` → assert file
   exists with expected content → quit cleanly.
5. Crash handler: invoke with a deliberately bad workspace path → assert
   stderr contains `"fedit: unrecoverable error"` and exit code 1.

### Implementation checklist

- [ ] Add `BinarySmokeTests.fs` to `tests/Fedit.Tests/`.
- [ ] Helper that locates the freshly built `fedit` binary in
      `src/Fedit/bin/Debug/<TFM>/` (read TFM from the project to stay
      stable across Phase 17's `net9.0 → net10.0` bump) and spawns it
      with stdin/stdout redirected.
- [ ] 3–5 scenarios from the list above.
- [ ] Mark tests `[<Trait("Category", "slow")>]` if they take >500ms
      so the inner-loop `just test` stays snappy (run them only in CI).
- [ ] No new CI wiring — same `dotnet test Fedit.slnx` job picks them
      up.

### Why not vhs now

`charmbracelet/vhs` would give us nicer assertions (real PTY, real
ANSI, text-frame goldens) and double-duty as the README demo GIF
generator. But it adds `vhs` + `ttyd` to the CI environment and a
separate workflow job. Defer until either (a) the demo GIF is wanted,
or (b) Process.Start's exit-code-only granularity proves insufficient.

---

## Phase 9 — Quick wins

Three self-contained fixes. Each is a small diff and can land
independently. Order them quick-win first so the early commits warm up
the rest of the workflow.

### 9.1 — `Buffer.replaceRange` computes `Lines` twice per keystroke

**Where:** `src/Fedit/Buffer.fs` — `replaceRange`, `backspace`,
`deleteForward`, `backspaceWord`, `deleteForwardWord`, `unindent`,
`deleteSelection`.

**Why.** P1 (CHANGELOG Phase 5) memoizes `Lines : string[]` on
`BufferState` so the renderer no longer re-`Split`s on every frame.
But the per-edit code path still runs `computeLines` _twice_ per
keystroke:

```fsharp
let private replaceRange startIndex count replacement buffer =
    let deleted  = PieceTable.deleteRange startIndex count buffer.Document
    let inserted = PieceTable.insert startIndex replacement deleted
    let nextCursor =
        indexToPosition (startIndex + replacement.Length) (buffer |> withDocument inserted)  // #1
    changeDocument inserted nextCursor buffer                                                 // #2
```

`withDocument` runs `computeLines` so `indexToPosition` has a `Lines`
array to walk. `changeDocument` then runs `computeLines` _again_ on
the same document. For a 1 MB file that's ~2 MB of string allocation
per keypress, immediately GC'd.

**Action.** Compute lines once, derive the cursor against the
already-updated buffer, then patch in undo state:

```fsharp
let private replaceRange startIndex count replacement buffer =
    let deleted    = PieceTable.deleteRange startIndex count buffer.Document
    let inserted   = PieceTable.insert startIndex replacement deleted
    let withDoc    = buffer |> withDocument inserted       // computeLines once
    let nextCursor = indexToPosition (startIndex + replacement.Length) withDoc
    { withDoc with
        Undo            = snapshot buffer :: buffer.Undo |> List.truncate maxUndoDepth
        Redo            = []
        Cursor          = nextCursor
        PreferredColumn = None
        Dirty           = true }
```

Apply the same pattern to `backspace`, `deleteForward`,
`backspaceWord`, `deleteForwardWord`, `unindent`, `deleteSelection`.
One `computeLines` per edit is enough.

### 9.2 — `jsonEscape` round-trips invalid JSON

**Where:** `src/Fedit/Runtime.fs` — `jsonEscape`, `saveConfig`.

**Why.** Hand-rolled escaper handles only `\\` and `\"`. A workspace
path with a newline, tab, or control character produces invalid JSON.
The read side uses `System.Text.Json.JsonDocument` properly, so the
write/read asymmetry is the bug — a corrupted config can't be parsed
back.

**Action.** Replace with `JsonSerializer.Serialize`. ~10 lines:

```fsharp
let private saveConfig (themeName: string) (recent: string list) =
    let directory = configDirectory ()
    Directory.CreateDirectory directory |> ignore
    let payload = {| theme = themeName; recent = recent |}
    let json =
        System.Text.Json.JsonSerializer.Serialize(
            payload,
            System.Text.Json.JsonSerializerOptions(WriteIndented = true))
    File.WriteAllText(configPath (), json, utf8WithoutBom)
```

Drop `jsonEscape` entirely.

### 9.3 — Centralize the selection-clearing convention in motions

**Where:** `src/Fedit/Editor.fs` — `runEditor`.

**Why.** Every motion key is hand-written
`Buffer.clearSelection >> Buffer.moveX`. `ShiftX` keys deliberately
don't clear. The pattern is correct but the convention is enforced by
author memory — adding a new motion key risks forgetting to clear.

**Action.** One helper:

```fsharp
let private motion clear transform =
    let combined =
        if clear then Buffer.clearSelection >> transform
        else Buffer.extendSelectionToCursor >> transform
    updateActiveBuffer combined

| Left       -> motion true  Buffer.moveLeft  model, []
| ShiftLeft  -> motion false Buffer.moveLeft  model, []
```

Roughly halves `runEditor`.

---

## Phase 10 — Module splits

Pre-emptive cleanup before the next feature lands on top of
`Editor.fs` or `Runtime.fs` and makes the god-module problem expensive
to undo. Land in the listed order — 10.1 enables 10.2's typed pattern
matches; 10.2 sets the file structure that 10.3 mirrors.

### 10.1 — Resolve string-typed command payloads at the parse boundary

**Where:** `src/Fedit/Commands.fs`, `src/Fedit/Editor.fs`
(`executeCommand`).

**Why.** Six commands carry strings (`Open`, `WriteAs`, `Theme`,
`Recent`, `SwitchBuffer`). Validation is deferred to `executeCommand`,
which means:

- `Theme` is validated twice — once in the constructor, once in
  `executeCommand` against `Themes.tryFindIn model.UserThemes`.
- `SwitchBuffer of string` is interpreted as int-id OR name OR file
  path inside `executeCommand`. The parser never narrowed it.

**Action.** Push resolution to the parse layer:

```fsharp
| Theme of Theme
| SwitchBuffer of BufferRef

and BufferRef = ById of int | ByName of string
```

`executeCommand` becomes pattern-matchy and dumb; no double
validation. `Open` / `WriteAs` / `Recent` stay strings because they're
filesystem paths resolved against the workspace root at execution
time.

### 10.2 — Split `Editor.fs` (god module, 757 lines)

**Why.** `Editor.fs` handles command-bar state, theme-preview logic,
command execution, search state, three focus-specific key
dispatchers, the global-shortcut table, and the top-level `update`.
Five+ responsibilities; violates the one-sentence test.

**Action.** Two new files (not five). The only real interaction slices
that have grown subsystem-shaped are the command bar and search.
Editor and sidebar key handling stay in `Editor.fs` alongside the
orchestrator — pre-emptively splitting `runEditor` / `runSidebar` into
a `FocusKeys.fs` would shuffle files without making anything easier
to find.

| Where                   | Functions                                                                                                                                                                                                                                   | Budget |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -----: |
| `Model.fs` (extend)     | + `Model.activeBuffer`, `Model.notify`, `Model.updateActiveBuffer` — the three shared helpers currently private in Editor. They're operations on the model.                                                                                 |    +30 |
| `Workspace.fs` (extend) | + `Workspace.resolvePath`, `Workspace.files` — path utilities about the workspace, not the editor.                                                                                                                                          |    +15 |
| `CommandBar.fs` (new)   | `emptyCommandBar`, `themeFromApplyText`, `updatePreview`, `refreshCommandBar`, `openBar`, `closeBar`, `insertText`, `replaceText`, `deleteBackward/Forward`, `pushHistory`, `executeCommand`, `saveActive`, `switchBuffer`, `runCommandBar` |    280 |
| `Search.fs` (new)       | `openSearch`, `closeSearch`, `updateQuery`, `moveMatch`, `runSearch`                                                                                                                                                                        |     80 |
| `Editor.fs` (shrunk)    | `init`, `normalizeNewlines`, `runGlobal`, `runEditor`, `runSidebar`, top-level `update`, external `Msg` handlers                                                                                                                            |    330 |

Compile order in `Fedit.fsproj`:

```xml
<Compile Include="Model.fs" />          <!-- types + shared model ops -->
<Compile Include="CommandBar.fs" />     <!-- uses Model.* -->
<Compile Include="Search.fs" />         <!-- uses Model.* -->
<Compile Include="Editor.fs" />         <!-- uses CommandBar, Search, Model -->
```

`Workspace.fs` stays in its current slot (earlier than `Model.fs`).

The shared helpers move to `Model`:

```fsharp
[<RequireQualifiedAccess>]
module Model =
    let activeBuffer m =
        m.Editors.Buffers[m.Editors.ActiveBufferId]

    let notify notification m =
        { m with Notification = notification }

    let updateActiveBuffer transform m =
        let transformed = activeBuffer m |> transform
        let sidebarOffset =
            if m.Panels.SidebarVisible then m.Panels.SidebarWidth + 1 else 0
        let viewportWidth =
            max 1 (m.Terminal.Width - sidebarOffset - Buffer.gutterWidth transformed)
        let viewportHeight = max 1 (m.Terminal.Height - m.Panels.DockHeight - 2)
        let updated = transformed |> Buffer.ensureViewport viewportHeight viewportWidth
        { m with
            Editors =
                { m.Editors with
                    Buffers = m.Editors.Buffers |> Map.add updated.Id updated } }
```

`runGlobal` becomes a named function inside `Editor.fs` and supersedes
the M4 "extract global-shortcut table" item from IMPROVEMENTS — it's
the same refactor:

```fsharp
let private runGlobal key model =
    match key with
    | Ctrl 'q' -> Some (handleQuit model)
    | Ctrl 'p' -> Some (CommandBar.openBar "" { model with Notification = None }, [])
    | Ctrl 'f' -> Some (Search.openSearch { model with Notification = None }, [])
    | Ctrl 'b' -> Some ({ model with Focus = Sidebar;    Notification = None }, [])
    | Ctrl 'e' -> Some ({ model with Focus = FocusTarget.Editor; Notification = None }, [])
    | Ctrl 's' -> Some (CommandBar.saveActive None { model with Notification = None })
    | Ctrl 'r' -> Some ({ model with Notification = None }, [ ScanWorkspace model.Workspace.RootPath ])
    | Ctrl 'z' -> Some (Model.updateActiveBuffer Buffer.undo      { model with Notification = None }, [])
    | Ctrl 'y' -> Some (Model.updateActiveBuffer Buffer.redo      { model with Notification = None }, [])
    | Ctrl 'a' -> Some (Model.updateActiveBuffer Buffer.selectAll { model with Notification = None }, [])
    | Ctrl 'c' -> Some (handleCopy model)
    | Ctrl 'x' -> Some (handleCut model)
    | Ctrl 'v' -> Some ({ model with Notification = None }, [ ClipboardPaste ])
    | _ -> None

// In update:
| KeyPressed key ->
    let model =
        if key = Ctrl 'q' then model
        else { model with QuitArmed = false }
    match runGlobal key model with
    | Some result -> result
    | None ->
        let cleared = { model with Notification = None }
        match model.Focus with
        | Sidebar    -> runSidebar             key cleared
        | Editor     -> runEditor              key cleared
        | CommandBar -> CommandBar.runCommandBar key cleared
        | Search     -> Search.runSearch       key cleared
```

Bonus: the shortcut table becomes listable for the help dock.

**Anti-patterns to avoid during this split:**

- Don't introduce `CommandBarMsg` / `SearchMsg` sub-message
  hierarchies. Keep `Msg` flat — the MVU shape is fine.
- Don't make a separate `CommandBarState.fs` for the type definition;
  it lives in `Model.fs` with the other state slices.
- Don't split by abstract concern (`Keymap.fs`, `Reducer.fs`,
  `CommandExecutor.fs`). That's how a 3000-line project becomes a
  scavenger hunt.
- If a helper is used by only one slice, it stays in that slice.
- **Don't create an `EditorCore.fs` (or any `*Core.fs`) for "shared
  helpers".** If helpers feel shared, they're either operations on
  `Model` and belong on `Model`, operations on the workspace and
  belong on `Workspace`, or only-one-caller and don't need to move at
  all. A `*Core.fs` file is a junk-drawer waiting to happen.
- **Don't pre-emptively split `runEditor` / `runSidebar` into a
  `FocusKeys.fs`.** Move them only if one grows past ~150 lines or
  sprouts its own subsystem (e.g., column selection, macro recording).

**Pre-refactor test guardrails** — add these _before_ moving code if
they don't already exist:

- Command bar opens on `Ctrl+P`, closes on `Escape`.
- `Enter` on a valid command emits the expected `Effect` + model
  change.
- Command-bar history `Up`/`Down` cycles.
- Search `Backspace` on empty query closes search.
- Sidebar `Enter` on a file emits `LoadFile`.
- `Ctrl+S` on a scratch buffer opens command bar with `writeas `.

### 10.3 — Split `Runtime.fs` (298 lines, four concerns)

**Why.** Mixes config I/O, clipboard subprocess management, workspace
scanning, and the main loop. None of them can be unit-tested without
dragging the others in.

**Action.**

| New file       | Functions to move                                                                                  |
| -------------- | -------------------------------------------------------------------------------------------------- |
| `Config.fs`    | `configDirectory`, `configPath`, `loadConfig`, `saveConfig` (post-9.2, no `jsonEscape` to migrate) |
| `Clipboard.fs` | `isMac`, `clipboardCopy`, `clipboardPaste`, `startProcessOrFail`                                   |
| `FileScan.fs`  | `makeNode`, `shouldSkip`, `scanNode`, `loadUserThemes`                                             |
| `Runtime.fs`   | `runEffect`, `run`, `consoleSize`, `dispatch`, watcher wiring                                      |

Drops `Runtime.fs` to ~80 lines of orchestration.

---

## Phase 11 — Renderer diff

**Where:** `src/Fedit/Renderer.fs` (`render`), `src/Fedit/Runtime.fs`
(the `needsRender` toggle).

**Why.** `needsRender <- true` fires for _any_ `Msg` — every
keystroke, every dequeued effect result, every resize check.
`Renderer.render` then walks every `(row, col)` cell, emits a fresh
`[{row+1};1H` cursor-position per row, and resets style with
`[0m` at the end of every row (which forces the next row's first
cell to re-emit its SGR). On a 200×60 terminal that's ~30 KB of ANSI
per frame, ~1 MB/s at typing speed. On slow SSH or in a high-DPI
terminal emulator this is the perceived input lag, and on a CPU
profile it dominates everything else.

**Action.** Diff against the previous screen. Keep
`mutable previousFrame : Screen option = None` in `Runtime.run`, pass
it into `Renderer.render`, and skip cells whose `Style` and `Glyph`
match the previous frame. Track `currentStyle` _across_ rows so
unchanged styles don't re-emit when crossing a row boundary, and emit
a fresh `CSI row;col H` only when we'd otherwise jump:

```fsharp
let render (writer: TextWriter) (previous: Screen option) (next: Screen) =
    let builder = StringBuilder()
    let mutable currentStyle : Style voption = ValueNone
    let mutable lastRow, lastCol = -2, -2

    let sameAsPrev row col =
        match previous with
        | Some p when p.Height = next.Height && p.Width = next.Width ->
            p.Cells[row, col] = next.Cells[row, col]
        | _ -> false

    for row in 0 .. next.Height - 1 do
        for col in 0 .. next.Width - 1 do
            if not (sameAsPrev row col) then
                let cell = next.Cells[row, col]
                if row <> lastRow || col <> lastCol + 1 then
                    builder.Append($"[{row + 1};{col + 1}H") |> ignore
                if currentStyle <> ValueSome cell.Style then
                    builder.Append(sgr cell.Style) |> ignore
                    currentStyle <- ValueSome cell.Style
                builder.Append(cell.Glyph) |> ignore
                lastRow <- row
                lastCol <- col
    …
```

`Cell` is already a struct (P3, shipped), so `sameAsPrev` is a single
struct comparison — no boxing.

**Bonus:** when only the cursor moved (no cells changed), emit only
the cursor-move CSI and skip the cell loop entirely. Holding
`Left`/`Right` in a single line becomes one or two bytes per frame.

### Follow-up — drop `pad`/`crop` allocations once diffing lands

**Where:** `src/Fedit/View.fs`.

`pad` and `crop` each allocate a fresh `string` per call (`PadRight`,
`Substring`). The editor pane calls them once per visible row;
sidebar and dock add more. Once cell-level diffing exists, this
allocation is the next visible item in any allocation profile. Fix:
`Screen.writeText` takes a `ReadOnlySpan<char>` and writes
truncate-or-pad logic directly into `Cells`, no intermediate strings:

```fsharp
let writeText x y style maxWidth (text: ReadOnlySpan<char>) screen =
    if y < 0 || y >= screen.Height || maxWidth <= 0 then () else
    let len = min maxWidth text.Length
    for i in 0 .. len - 1 do
        setCell (x + i) y style text[i] screen
    for i in len .. maxWidth - 1 do
        setCell (x + i) y style ' ' screen     // implicit pad-right
```

Callers pass `rows[lineIndex].AsSpan(buffer.ViewportLeft, …)`. Drop
`pad` and `crop` entirely.

---

## Phase 12 — Async follow-ups

The P2 work (CHANGELOG Phase 5) made every `Effect` run on the thread
pool with `CancellationTokenSource`-based "last writer wins" semantics
for `ScanWorkspace` and `LoadFile`. That introduced two real bugs and
left one expensive operation still on the UI thread.

### 12.1 — Preserve dirty state after async saves

**Where:** `src/Fedit/Editor.fs` `saveActiveBuffer`, `Editor.update`
handling `BufferSaved`, `src/Fedit/Model.fs` `Effect.SaveBuffer`.

**Why.** `SaveBuffer` captures a serialized content snapshot, then
writes it on a background task. While that task runs, the user can
keep editing the same buffer. When the later `BufferSaved` message
arrives, `Editor.update` calls `Buffer.markSaved` for the current
buffer _without_ proving the current document still matches the
snapshot that was written. That clears `Dirty` for edits that never
reached disk — the UI claims the buffer is clean while the file is
stale.

**Action.** Carry a saved revision / content identity with
`SaveBuffer` and only clear dirty state when the active buffer still
matches it. If the buffer changed during the write, keep it dirty and
only update the saved path / notification.

### 12.2 — Serialize config saves

**Where:** `src/Fedit/Runtime.fs` `runEffect` handling `SaveConfig`.

**Why.** `SaveConfig` now starts an independent `Task.Run` for each
config write. Two quick config-changing actions (theme change +
recent-file update) can finish out of order. If the older task writes
last, `~/.config/fedit/config.json` persists stale `themeName` or
`recent` data for the next launch. The pre-P2 synchronous path
preserved write order by construction.

**Action.** Funnel config writes through one ordered worker, or
attach a monotonic config-save version and drop stale completions.
Invariant to preserve: an older config snapshot must never overwrite
a newer one.

### 12.3 — Search-as-effect

**Where:** `src/Fedit/Editor.fs` `runSearch` / `updateSearchQuery`,
`src/Fedit/Buffer.fs` `findAll`.

**Why.** Every character typed into the find bar runs
`Buffer.findAll` synchronously inside `Editor.update`. `findAll` calls
`text buffer` (full `PieceTable.toString`) and then loops
`String.IndexOf` over the whole document. For a 10 MB file, typing
"hello" allocates and scans 50 MB of string in the pure-update layer
before the renderer ever wakes up. `update` is supposed to be cheap
enough to replay deterministically — this finding makes typing-in-search
non-deterministic in latency.

**Action.** Move the work into the interpreter as a cancellable
effect:

```fsharp
// New effect
| FindInBuffer of bufferId: int * query: string

// New Msg
| FindCompleted of bufferId: int * query: string * matches: int list

// Editor.update — record query, request search, don't run it
| Search.UpdateQuery query ->
    let model' = { model with Search = Some { Query = query; Matches = []; Current = 0 } }
    model', [ CancelEffect (EffectId "search")
              StartEffect (EffectId "search", FindInBuffer (activeId, query)) ]

| FindCompleted (bufId, query, matches)
        when model.Search |> Option.exists (fun s -> s.Query = query)
             && bufId = model.Editors.ActiveBufferId ->
    let model' = { model with Search = Some { … with Matches = matches; Current = 0 } }
    match matches with
    | [] -> model', []
    | first :: _ -> Model.updateActiveBuffer (Buffer.moveToOffset first) model', []

| FindCompleted _ -> model, []   // stale or buffer changed, drop
```

`Buffer.findAll` itself stays where it is; only the _invocation_ moves
into the interpreter so the pure loop stays cheap. Cancellation by
`EffectId` drops stale results automatically when the user is still
typing.

---

## Phase 13 — Workspace caching & startup errors

### 13.1 — Cache flattened workspace tree (M5)

**Where:** `src/Fedit/Workspace.fs` `findNodeByPath`, `visibleEntries`;
call sites in `Editor.runSidebar` and `Workspace.metadata` /
`expandSelected` / `tryCollapseSelected` / `activateSelected`.

**Why.** `findNodeByPath` walks the whole tree with `List.tryPick`
and is called by four `Workspace.*` helpers — sometimes more than
once per sidebar keypress. `visibleEntries` re-flattens the entire
tree (allocating fresh `WorkspaceEntry` records and re-sorting
children) on every call. On a workspace with thousands of files,
holding `Down` in the sidebar burns CPU.

**Action.** Cache a flat `Map<string, FileNode>` in `WorkspaceState`,
populated once in `setTree`. Pre-sort children there too so
`visibleEntries` doesn't sort on every keypress:

```fsharp
type WorkspaceState =
    { RootPath: string
      Tree: FileNode option
      ByPath: Map<string, FileNode>     // built once in setTree
      Expanded: Set<string>
      SelectedPath: string option }

let setTree (tree: FileNode) workspace =
    let rec collect acc node =
        let acc = Map.add node.Path node acc
        node.Children |> List.fold collect acc
    let rec preSort node =
        { node with Children = node.Children |> List.map preSort |> sortChildren }
    let sortedTree = preSort tree
    { workspace with
        Tree     = Some sortedTree
        ByPath   = collect Map.empty sortedTree
        Expanded =
            if sortedTree.IsDirectory then Set.add sortedTree.Path workspace.Expanded
            else workspace.Expanded }
    |> ensureSelected
```

`findNodeByPath` becomes `Map.tryFind path workspace.ByPath`. The
remaining cost is `visibleEntries`'s flatten, bounded by what the user
has expanded — acceptable.

### 13.2 — Surface startup config / theme load errors (M6)

**Where:** `src/Fedit/Runtime.fs` `loadConfig`, `loadUserThemes`, the
re-resolve theme block.

**Why.** Each wraps its body in `try … with _ -> None` / `[]`. A
malformed `config.json` or a typo in a user theme file disappears
silently; the user sees nothing change and has no clue why. F7
(CHANGELOG findings) addressed the same anti-pattern for
`scanNode` — extend the convention to the startup loaders.

**Action.** Return `Result` and fold the errors into the initial
`Notification`:

```fsharp
let private loadConfig () : Result<Theme option * string list, string> =
    let path = configPath ()
    if not (File.Exists path) then Ok (None, [])
    else
        try
            let json = File.ReadAllText path
            use doc = JsonDocument.Parse json
            …
            Ok (theme, recent)
        with ex ->
            Error $"config.json: {ex.Message}"

// In Runtime.run, fold load errors into initial notification
let initialNotice =
    [ configError; themesError ]
    |> List.choose id
    |> function
       | [] -> Notification.info "Ctrl+P commands  …"
       | errs -> Notification.warning (String.concat "; " errs)
```

Don't print to stderr — the alt-screen buffer hides it.

---

## Phase 14 — Polish

Opportunistic items. Pick up during related work.

### 14.1 — Theme preview leaks into `CommandBar` state (M1)

**Where:** `src/Fedit/Model.fs` (`PreviewTheme : Theme option` on
`CommandBarState`), `src/Fedit/Editor.fs` `themeFromApplyText`
(hard-codes the `"theme "` prefix).

**Why.** Preview is derived from `Parsed` + selected completion — it
isn't independent state. And the editor knows the command's text
shape, which is a leak in the wrong direction.

**Action.** Either generalize (each command may return a preview) or
derive `effectiveTheme` at render time from `CommandBar.Parsed` + the
selected completion. The second option removes a field.

### 14.2 — `Recent` saves on every file open (M2)

**Where:** `src/Fedit/Editor.fs` `FileOpened` handler.

**Why.** Every `FileOpened` emits `SaveConfig`. Under the FS watcher,
rapid external changes could cause save churn.

**Action.** Persist `Recent` only on `Quit`, or debounce in `Runtime`.

### 14.3 — Name the workspace-metadata anonymous record (M3)

**Where:** `src/Fedit/Workspace.fs` `metadata`; consumed by
`View.workspaceMetadataLines`.

**Why.** Anonymous records are fine inside one file but fight
tooltips, tests, and refactors at boundaries.

**Action.** Declare `WorkspaceMetadata` in `Workspace.fs` and return
that instead of `{| Path; IsDirectory; ChildCount |}`.

### 14.4 — Configurable tab width

**Where:** `src/Fedit/Buffer.fs` `tabText`,
`~/.config/fedit/config.json`.

**Why.** `tabText = "    "` is hardcoded. A user wanting 2-space
indent has nowhere to set it.

**Action.** Add a `tabWidth: int` field to the config schema, default 4. `Runtime.loadConfig` reads it; `Editor.init` passes it into the
model; `Buffer.indent` / `Buffer.unindent` use `String.replicate
model.TabWidth " "` instead of the constant. Out of scope:
real-`\t` mode (would need a save-time roundtrip story).

### Nice-to-haves

- **`View.fs` `digits = gutterWidth - 2`** reverse-engineers
  `Buffer.gutterWidth`'s formula. If gutter formatting ever changes,
  this breaks silently. Have `Buffer` expose both `gutterWidth` and
  `lineNumberDigits`.
- **`Themes.fs`** — currently flat list + `tryFind`. Resist
  abstraction until a real second use case appears.
- **No `interface` keyword in the codebase.** Introduce when a real
  second implementation appears (e.g., `MockFileSystem` for scanner
  tests), not before.

---

## Phase 15 — Borders and file-tree icons

Two independent visual upgrades sourced from a survey of mature TUIs
(ratatui, lipgloss, helix, zellij, yazi, nvim-web-devicons). 15.1 is
risk-free and worth landing immediately; 15.2 is a small subsystem
that must ship behind an opt-in config key.

### 15.1 — Replace `'|'` sidebar separator with `│` (U+2502)

**Where:** `View.fs:281`
(`Screen.drawVerticalLine sidebarWidth 0 mainHeight chrome '|' current`)

**Why:** The literal ASCII pipe is what every TUI looks like before
someone notices Unicode box-drawing exists. `│` (BOX DRAWINGS LIGHT
VERTICAL, **U+2502**) is plain Unicode since 1.0 — present in every
monospace terminal font ever shipped (Consolas, Menlo, Cascadia,
DejaVu). Not a Nerd Font glyph, no detection or fallback required.

Every mature TUI uses it:

- ratatui `PLAIN` border set → `│`
- lipgloss `NormalBorder()` → `│`
- zellij `boundary_type::VERTICAL` → `│`
- helix picker → `│`

**Action:** One-character patch.

```fsharp
// View.fs:281
Screen.drawVerticalLine sidebarWidth 0 mainHeight chrome '│' current
```

**Optional in the same patch — extend `Screen` with a `Borders`
reference module** so future bordered components (popups, dock
divider, completion frame) all pull from one place:

```fsharp
// Screen.fs (or a new tiny Borders.fs)
[<RequireQualifiedAccess>]
module Borders =
    // Plain — matches ratatui PLAIN, lipgloss Normal, zellij default
    let vertical    = '│'  // U+2502
    let horizontal  = '─'  // U+2500
    let topLeft     = '┌'  // U+250C
    let topRight    = '┐'  // U+2510
    let bottomLeft  = '└'  // U+2514
    let bottomRight = '┘'  // U+2518
    let teeLeft     = '┤'  // U+2524
    let teeRight    = '├'  // U+251C
    let teeDown     = '┬'  // U+252C
    let teeUp       = '┴'  // U+2534
    let cross       = '┼'  // U+253C

    // Rounded — purely cosmetic alternative to plain corners
    let roundedTL   = '╭'  // U+256D
    let roundedTR   = '╮'  // U+256E
    let roundedBL   = '╰'  // U+2570
    let roundedBR   = '╯'  // U+256F
```

If we ever want a visible horizontal rule between dock and editor
body (currently the inverted status background does that job), `─`
is the answer; nothing to do today.

**Effort:** ~15 minutes including manual smoke + screenshot check
in `Terminal.app`, `iTerm2`, and one Linux terminal.

### 15.2 — Optional Nerd Font file-tree icons (opt-in config flag)

**Where:** `View.fs:118-128` (`renderSidebar` inlines
`marker = "[+] " | "[-] " | "    "`), `Model.fs` (extend `Model` with
`Icons: IconMode`), `Runtime.fs:saveConfig` / `loadConfig` (extend
config schema), `Commands.fs` (new `:icons` command).

**Why:** Nerd Fonts are PUA glyphs (U+E000–U+F8FF, U+F0000–U+FFFFF)
that show file-type and folder-state icons in monospace. They make
sidebar trees scannable at a glance — `.fs`, `.md`, `.json`, `.toml`
each gets a colored glyph instead of competing for column space with
the filename. **But** the user must have a Nerd Font installed in
their terminal, and there is no reliable way to detect this.

**Universal pattern across mature projects:** icons are user-opt-in,
default off, no auto-detection. yazi does this via `theme.toml`;
nvim-tree assumes the user opted in by installing the plugin. lazygit,
gitui, helix, and ratatui all ship **no icons at all**. fedit can
sit between those two: keep the current ASCII default, add a `nerd`
mode for users who want it.

The canonical mapping table is `nvim-web-devicons` — yazi ships it
as its default. Relevant entries for fedit:

| Ext              | Glyph | Code Point | Nerd Font name        |
| ---------------- | ----- | ---------- | --------------------- |
| `fs`/`fsi`/`fsx` | ``    | U+E7A7     | nf-dev-fsharp         |
| `md`             | ``    | U+F48A     | nf-oct-markdown       |
| `json`           | ``    | U+E60B     | nf-seti-json          |
| `toml`           | ``    | U+E6B2     | nf-seti-config        |
| `yaml`/`yml`     | ``    | U+E6A8     | nf-seti-yaml          |
| `sh`             | ``    | U+F489     | nf-oct-terminal       |
| `txt`            | ``    | U+F15C     | nf-fa-file_text       |
| folder (closed)  | ``    | U+E5FF     | nf-custom-folder      |
| folder (open)    | ``    | U+E5FE     | nf-custom-folder_open |
| default file     | ``    | U+F15B     | nf-fa-file            |

**Action — three pieces.**

**1. Add `IconMode` and thread it through `Model` + config:**

```fsharp
// Primitives.fs (or new Icons.fs at the same layer)
type IconMode =
    | IconsOff      // current "[+] / [-] /     " (default; no regression)
    | IconsAscii    // "v / > /   " — slightly less noisy than [+]/[-]
    | IconsNerd     // PUA glyphs; requires Nerd Font in the terminal

// Model.fs — add Icons : IconMode to Model record

// Runtime.fs — extend config.json schema:
//   { "theme": "cyan", "icons": "off" | "ascii" | "nerd", "recent": [...] }
//   parse with the same pattern as theme; default to IconsOff on missing
//   or invalid value.
```

**2. Add a small icon module.** Mirror nvim-web-devicons' extension
table. Keep the table small at first; grow on demand:

```fsharp
// Icons.fs — new module, lives after Primitives.fs and Workspace.fs
[<RequireQualifiedAccess>]
module Icons =
    // Nerd Font v3 code points. Verified against nerdfonts.com/cheat-sheet.
    // Each glyph is 1 cell wide; pair with a trailing space.
    let private nerdFolderClosed = "\uE5FF "   // nf-custom-folder
    let private nerdFolderOpen   = "\uE5FE "   // nf-custom-folder_open
    let private nerdFileDefault  = "\uF15B "   // nf-fa-file

    let private nerdByExt =
        Map.ofList [
            "fs",       "\uE7A7 "; "fsi",  "\uE7A7 "
            "fsx",      "\uE7A7 "; "fsproj","\uE7A7 "
            "md",       "\uF48A "; "markdown","\uF48A "
            "json",     "\uE60B "; "toml", "\uE6B2 "
            "yaml",     "\uE6A8 "; "yml",  "\uE6A8 "
            "txt",      "\uF15C "; "sh",   "\uF489 "
            "rs",       "\uE7A8 "; "cs",   "\uF81A "
            "py",       "\uE73C "; "js",   "\uE74E "
            "ts",       "\uE628 "; "html", "\uE736 "
            "css",      "\uE749 "; "lock", "\uF023 "
        ]

    let entryIcon mode (entry: WorkspaceEntry) =
        match mode with
        | IconsOff ->
            if entry.IsDirectory then
                if entry.IsExpanded then "[-] " else "[+] "
            else
                "    "
        | IconsAscii ->
            if entry.IsDirectory then
                if entry.IsExpanded then "v " else "> "
            else
                "  "
        | IconsNerd ->
            if entry.IsDirectory then
                if entry.IsExpanded then nerdFolderOpen else nerdFolderClosed
            else
                let ext =
                    Path.GetExtension entry.Name
                    |> Option.ofObj
                    |> Option.map (fun s -> s.TrimStart('.').ToLowerInvariant())
                    |> Option.defaultValue ""
                Map.tryFind ext nerdByExt |> Option.defaultValue nerdFileDefault
```

**3. Wire it into `View.renderSidebar`:**

```fsharp
// View.fs:118-127 — replace the inline marker block with:
let prefix = Icons.entryIcon model.Icons entry
let indentation = String.replicate entry.Depth "  "
let text = $"{indentation}{prefix}{entry.Name}"
```

**Also add a `:icons` command** to flip modes at runtime, mirroring
the shape of `:theme`. New entry in `Commands.specs`:

```fsharp
{ Name = "icons"
  Usage = "icons <off|ascii|nerd>"
  Summary = "Sidebar icon mode. Nerd requires a Nerd Font in the terminal."
  Constructor =
    fun argument ->
        match argument.Trim().ToLowerInvariant() with
        | "off"   -> Ready (Icons IconsOff)
        | "ascii" -> Ready (Icons IconsAscii)
        | "nerd"  -> Ready (Icons IconsNerd)
        | ""      -> Pending "Icon mode required: off | ascii | nerd."
        | other   -> Invalid $"Unknown icon mode '{other}'." }
```

Add `Icons of IconMode` to the `Command` DU and handle it in
`executeCommand` — set `model.Icons` and emit `SaveConfig` with the
new field included.

**Code-point caveats** (these bite people):

- **Nerd Fonts v2 → v3 (2023) relocated many glyphs.** The table
  above uses v3 code points — what every current Nerd Fonts release
  ships. Users on very old patched fonts will see tofu (`▯`).
  Document "Nerd Fonts v3+ required" in the README's `:icons nerd`
  section.
- **Some Nerd Font glyphs are double-wide** in certain fonts
  (Cascadia PL is the worst offender). Our 1-cell `Cell[,]` grid
  assumes 1-wide monospace everywhere. Recommend specific safe
  fonts in docs: JetBrainsMono Nerd Font, FiraCode Nerd Font,
  Iosevka Nerd Font (all render Nerd Font glyphs as 1 cell wide).
- **Verify each code point against the [Nerd Fonts cheat
  sheet](https://www.nerdfonts.com/cheat-sheet)** before relying
  on a specific U+XXXX in this table — the glyphs above are
  illustrative and based on v3.

**Detection heuristics — don't bother.** No reliable terminal
capability query for "has Nerd Font" exists. Env vars like
`$KITTY_WINDOW_ID` or `$WEZTERM_EXECUTABLE` tell you the terminal
emulator, not the font. The honest answer is what yazi and lazygit
do: explicit config key, sensible default (`off`).

**Why this stays opt-in, not auto-on:** the immediate downside of
shipping `IconsNerd` as default is that on any terminal without a
Nerd Font (TTY, basic SSH, default Terminal.app font), the user
sees `▯` tofu rectangles everywhere in the sidebar. ASCII fallback
default + opt-in upgrade = no regression for anyone, real win for
opted-in users.

**Effort:** ~half-day. ~100–150 LOC across `Primitives.fs`,
`Model.fs`, `Icons.fs` (new), `Commands.fs`, `View.fs`, `Runtime.fs`
(config schema). Test coverage: one parametric test per
`(IconMode, IsDirectory, IsExpanded, ext)` combination through
`Icons.entryIcon`; verify config round-trip in
`Runtime.saveConfig`/`loadConfig`.

---

## Phase 16 — Buffer internals refactor

Three independent changes inside `src/Fedit/Buffer.fs`, promoted out
of "Considered, not pursued" after explicit decision. 16.1 is a small
aesthetic refactor; 16.2 and 16.3 are real shape changes worth
landing as separate commits with test coverage between them. Land
16.1 first as a warm-up, then 16.2 (which 16.3 builds on for its
derived-state recompute), then 16.3.

### 16.1 — Simplify `Buffer.ensureViewport`

**Where:** `src/Fedit/Buffer.fs` `ensureViewport`.

**Why.** The current implementation computes `maxTop`, `maxLeft`,
`nextTop`, `nextLeft` independently with clamped arithmetic. Works
correctly and each clamp documents its constraint, but the four-step
arithmetic is harder to extend than necessary.

**Action.** Rewrite to derive a single "target scroll offset" per
axis from the cursor + viewport dims, then clamp once. Confirm
equivalence with a property test holding cursor / viewport / content
invariants over generated buffers.

Pre-refactor test guardrail: cursor-at-top, cursor-at-bottom,
cursor-past-line-end, single-line buffer, viewport-larger-than-buffer.

### 16.2 — Replace `Lines : string[]` cache with `Offsets : int[]`

**Where:** `src/Fedit/Buffer.fs` — `BufferState`, `computeLines`,
`rawLines`, `lines`, `line`, `lineCount`, `clamp`,
`positionToIndex`, `indexToPosition`, `findAll`; `src/Fedit/View.fs`
rendering path.

**Why.** Phase 9.1 eliminates the per-edit double-compute symptom,
but the underlying cache still materializes the file content twice
on every edit (piece table + `string[]`). On large files this
doubles RAM cost and puts pressure on GC. Offsets-only — `Offsets
: int[]` of newline positions, slicing the piece table on demand —
removes the duplication and is the better shape for incremental
syntax highlighting (TreeSitter, per the existing `c400dee` commit),
folding, and large-file streaming.

**Action.**

- Replace `Lines : string[]` with `Offsets : int[]` on `BufferState`.
  `Offsets[i]` = the absolute index of the start of line `i`.
- `computeOffsets : PieceTable -> int[]` scans the piece-table
  string once for `\n` positions. Cheaper than building a `string[]`
  because it doesn't allocate per-line substrings.
- `lineCount buffer = buffer.Offsets.Length`.
- `line lineIdx buffer` and `lines buffer` slice on demand:
  `PieceTable.substring start (endExclusive - start) buffer.Document`.
  Add `PieceTable.substring` if it doesn't exist (a private walk
  that builds only the requested range).
- `positionToIndex` becomes `buffer.Offsets[line] + column`.
- `indexToPosition` binary-searches `Offsets` (was a linear walk).
- The renderer's per-row read becomes `line buffer rowIndex` — one
  allocation per visible row, not the whole file.

**Verify.** Tier 1 tests cover all reader functions; they should
pass unchanged. Add a property test: editing then reading a line
produces the expected substring; `lineCount` matches the count of
`\n` in `toString`; `positionToIndex >> indexToPosition` is
identity.

Sequencing: land _after_ Phase 9.1 so the perf delta from this work
is attributable to the shape change, not the call-site fix.

### 16.3 — Delta/patch undo

**Where:** `src/Fedit/Buffer.fs` — `BufferRevision`, `pushUndo`,
`undo`, `redo`, `changeDocument`, all edit primitives.

**Why.** Snapshot+cap is bounded but per-keystroke each revision
still records a full `Pieces` list. Composition lets many
keystrokes merge into one revision (typing "hello" is one undo
step, not five). Deltas also serialize cleanly for the
session-persistent-undo direction in Open questions — snapshots
don't.

**Action.**

- ```fsharp
  type Delta =
      | Insert of pos: int * text: string
      | Delete of pos: int * removed: string  // keep removed text so undo can re-insert
  ```
- `BufferRevision` becomes `{ Delta: Delta; Cursor: Position;
PreferredColumn: int option; Dirty: bool }`.
- `pushUndo` accepts a `Delta` arg from the edit primitive that
  produced it. `replaceRange` knows the deleted range + inserted
  text — emit `Delete` of removed content followed by `Insert`, or
  model `Replace` directly.
- `Buffer.undo` applies the inverse delta to `Document` and rebuilds
  derived state (`Offsets` post 16.2); `redo` re-applies the
  forward delta.
- **Composition.** Consecutive `Insert`s where the second starts at
  the end of the first merge into one revision. Same for
  consecutive `Delete`s at the same position. Time-bounded: break
  the group if more than 500ms passes between edits, so undo
  doesn't collapse a minute of typing into one step.
- Cap stays at 200 _revisions_; each revision is now a small record
  not a full snapshot.

**Verify.** Tier 1 tests for undo/redo become the regression net.
Add coverage for composition: "typing 5 chars produces 1 revision";
"5 chars then backspace then 2 chars produces 3 revisions";
"undo + redo round-trips the document".

Optional follow-up: serialize the undo stack with the buffer when
session persistence (Open questions) lands. Deltas serialize as
JSON; snapshots don't without re-implementing `PieceTable`
serialization.

---

## Phase 17 — .NET 10 LTS upgrade

**Goal.** Move off the .NET 9 STS train (Microsoft support ends 2026-05,
i.e. now) onto the .NET 10 LTS train. Refresh test packages at the same
time so the bump lands as one coherent commit, not a slow drift.

### Where

- `global.json` — SDK version.
- `Directory.Build.props` — `LangVersion` already `latest`, fine.
- `src/Fedit/Fedit.fsproj` — `<TargetFramework>` and any `bin/Debug/net9.0/`
  references elsewhere (TODO.md Phase 8 helper path).
- `tests/Fedit.Tests/Fedit.Tests.fsproj` — TFM and `<PackageReference>`s.
- `.config/dotnet-tools.json` — Fantomas.
- `.github/workflows/ci.yml` — pin via `global-json-file`, no version change
  needed here.

### Why

- **.NET 9 is STS.** 18-month support window from 2024-11 → 2026-05. Staying
  on `net9.0` past that means no security patches.
- **.NET 10 is LTS.** Three-year support, same single-file-publish story,
  same `PublishTrimmed=false` knob fedit already uses.
- **`FsCheck.Xunit` 3.0.0-rc3** has been on RC since the project started.
  3.x is stable now — drop the `-rc3`.
- `xunit` 2.9.x and `Microsoft.NET.Test.Sdk` have had multiple stable
  patch releases since the pins.

### Action

1. `global.json` → `"version": "10.0.100"` (or the current 10.0.x patch),
   keep `rollForward: latestFeature`.
2. Both `.fsproj` files → `<TargetFramework>net10.0</TargetFramework>`.
3. `tests/Fedit.Tests/Fedit.Tests.fsproj` — bump every PackageReference to
   the current stable. Notably drop `-rc3` from `FsCheck.Xunit`.
4. `dotnet tool update fantomas` (regenerates `.config/dotnet-tools.json`).
5. Update Phase 8's `src/Fedit/bin/Debug/net9.0/` reference in this file
   to `net10.0/` (or make the test helper read the TFM from the project).
6. `just check` locally on all three OSes the matrix covers (or rely on
   CI). Land in one commit so a bisect doesn't straddle the version bump.

### Why not net9.0 forever

The single-file publish story is unchanged across LTS bumps. The cost
of staying on STS is one rolling upgrade per year _plus_ a security gap
between EOL and the next bump. .NET 10 buys ~3 years of breathing room.

### Why not xUnit v3 in this phase

xUnit v3 is a real migration (new runner, new test discovery, FsUnit
compatibility surface), worth its own focused commit. Keep this phase
mechanical — TFM + patch bumps only.

---

## Phase 18 — Central Package Management

**Where:** new `Directory.Packages.props` at the repo root;
`tests/Fedit.Tests/Fedit.Tests.fsproj`.

**Why.** All package versions currently live inline in
`Fedit.Tests.fsproj`. There's only one consumer today, so this isn't
urgent — but the **moment a second project (snapshot tests, smoke
tests, a future `Fedit.Cli` for piping) appears**, versions drift in
two places and stop matching. CPM is the modern .NET default since 2022
and removes the drift class entirely. It also makes the test-dep set
discoverable at a glance without opening the `.fsproj`.

**Action.**

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.NET.Test.Sdk"        Version="…" />
    <PackageVersion Include="xunit"                         Version="…" />
    <PackageVersion Include="xunit.runner.visualstudio"     Version="…" />
    <PackageVersion Include="FsUnit.xUnit"                  Version="…" />
    <PackageVersion Include="FsCheck.Xunit"                 Version="…" />
  </ItemGroup>
</Project>
```

`Fedit.Tests.fsproj` keeps `<PackageReference Include="…" />` with no
`Version=` attribute. Done.

**Anti-patterns to avoid.**

- Don't `<GlobalPackageReference>` anything yet. It applies to _every_
  project including `Fedit.fsproj` (which has no packages), and the
  failure mode (silent transitive surprises) is worse than the saving.
- Don't add a `nuget.config` with custom feeds. Default `nuget.org` is
  fine; an explicit `nuget.config` is only worth adding when an internal
  feed or a feed-pinning policy actually appears.

---

## Phase 19 — Release automation

**Goal.** Pushing a `vX.Y.Z` tag produces a GitHub Release with a
`fedit` binary per platform. Today `just install` is local-only and
the Open questions list has named this gap.

**Where:** new `.github/workflows/release.yml`;
`src/Fedit/Fedit.fsproj` already declares the RID list.

### Workflow shape

```yaml
name: release
on:
    push:
        tags: ["v*.*.*"]

permissions:
    contents: write # required to attach assets to the Release

jobs:
    publish:
        strategy:
            fail-fast: false
            matrix:
                include:
                    - { os: macos-latest, rid: osx-arm64, ext: "" }
                    - { os: macos-13, rid: osx-x64, ext: "" }
                    - { os: ubuntu-latest, rid: linux-x64, ext: "" }
                    - { os: ubuntu-24.04-arm, rid: linux-arm64, ext: "" }
                    - { os: windows-latest, rid: win-x64, ext: ".exe" }
        runs-on: ${{ matrix.os }}
        steps:
            - uses: actions/checkout@v4
            - uses: actions/setup-dotnet@v4
              with: { global-json-file: global.json }
            - run: dotnet publish src/Fedit/Fedit.fsproj
                  -c Release -r ${{ matrix.rid }}
                  -o dist/${{ matrix.rid }} --nologo
            - name: Package
              shell: bash
              run: |
                  VER="${GITHUB_REF_NAME#v}"
                  BIN="dist/${{ matrix.rid }}/fedit${{ matrix.ext }}"
                  NAME="fedit-${VER}-${{ matrix.rid }}"
                  mkdir -p out
                  if [[ "${{ matrix.os }}" == windows-* ]]; then
                    7z a "out/${NAME}.zip" "$BIN" README.md LICENSE
                  else
                    tar -czf "out/${NAME}.tar.gz" -C "dist/${{ matrix.rid }}" fedit -C "$GITHUB_WORKSPACE" README.md LICENSE
                  fi
                  (cd out && shasum -a 256 * > "${NAME}.sha256")
            - uses: actions/upload-artifact@v4
              with:
                  name: fedit-${{ matrix.rid }}
                  path: out/*

    release:
        needs: publish
        runs-on: ubuntu-latest
        steps:
            - uses: actions/download-artifact@v4
              with: { merge-multiple: true, path: out }
            - uses: softprops/action-gh-release@v2
              with:
                  files: out/*
                  generate_release_notes: true
                  fail_on_unmatched_files: true
```

### Why this shape

- **Tag-triggered, not branch-triggered.** Releases should be explicit.
  `v1.2.3` is the source of truth; the workflow only reacts.
- **`fail-fast: false`** — one RID failing shouldn't kill the others.
  Partial releases are still useful to debug.
- **Per-RID job, not cross-publish from one OS.** `osx-arm64` published
  from Linux is technically possible but loses code-signing options and
  any future Mac-specific notarization story.
- **SHA256 sidecars** per archive. Standard expectation for anyone
  installing without a package manager.
- **`softprops/action-gh-release@v2`** is the de-facto community standard;
  switching later is one line. Avoid `actions/create-release` (archived).
- **`generate_release_notes: true`** uses GitHub's autogenerated diff
  body — superseded easily when a real `CHANGELOG.md` excerpt is wanted.

### Implementation checklist

- [ ] Add `.github/workflows/release.yml` from the template above.
- [ ] First release: tag `v0.1.0` (or whatever the project decides),
      verify the matrix completes, inspect attached assets.
- [ ] Update `README.md` install section: prefer the GitHub Release
      tarball over `just install` for non-contributors.
- [ ] Cross out the "Release automation" line under `Open questions`
      once this lands.

### Rejected alternatives

- **GoReleaser / cargo-dist clones.** Both exist; .NET doesn't have a
  comparably mature equivalent for single-file binaries, and the YAML
  above is short enough that a tool isn't earning its keep.
- **Homebrew tap.** Fine eventually, but tagged GitHub Releases are the
  precondition. Defer.
- **Code-signing macOS / Authenticode Windows.** Real work
  (certificates, secret storage). Out of scope for v1 of the release
  pipeline. Document the unsigned binary in README.

---

## Phase 20 — CI hardening

Five small wins for `.github/workflows/ci.yml` plus one new tiny file.
Each is independent; land opportunistically.

### 20.1 — Dependabot for GitHub Actions and NuGet

**Where:** new `.github/dependabot.yml`.

**Why.** Action versions silently rot (`actions/checkout@v3` is a year
out of date now), and the test packages were last touched at project
start. Dependabot opens one PR per outdated dep, weekly, and the format
check + matrix build in CI gates them.

```yaml
version: 2
updates:
    - package-ecosystem: github-actions
      directory: /
      schedule: { interval: weekly }
      groups:
          actions:
              patterns: ["*"]
    - package-ecosystem: nuget
      directory: /
      schedule: { interval: weekly }
      open-pull-requests-limit: 5
      groups:
          test-deps:
              patterns:
                  - "xunit*"
                  - "FsUnit*"
                  - "FsCheck*"
                  - "Microsoft.NET.Test.Sdk"
```

Group test deps so the inbox doesn't fill with five separate PRs every
time one of them ships a patch.

### 20.2 — Cache NuGet between runs

Add to both jobs (or factor a `setup` step):

```yaml
- uses: actions/cache@v4
  with:
      path: ~/.nuget/packages
      key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.fsproj','**/Directory.Packages.props','global.json') }}
      restore-keys: ${{ runner.os }}-nuget-
```

Shaves 20–40s off each matrix leg.

### 20.3 — Cancel superseded runs

Top of `ci.yml`:

```yaml
concurrency:
    group: ${{ github.workflow }}-${{ github.ref }}
    cancel-in-progress: true
```

Force-pushing during review doesn't burn Actions minutes for the stale
SHA.

### 20.4 — Deterministic / CI-flagged builds

In `Directory.Build.props`, add a CI-only conditional:

```xml
<PropertyGroup Condition="'$(ContinuousIntegrationBuild)' == 'true'">
  <Deterministic>true</Deterministic>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>
</PropertyGroup>
```

And export the env var in CI:

```yaml
env:
    DOTNET_NOLOGO: true
    DOTNET_CLI_TELEMETRY_OPTOUT: true
    ContinuousIntegrationBuild: true
```

Free reproducibility win; pairs naturally with Phase 19 (release
artifacts should be deterministic).

### 20.5 — CodeQL (or default code scanning)

**Decision.** F# isn't a first-class CodeQL language (no F#-specific
queries), so the value-per-minute is lower than for C#. **Skip the
explicit `codeql.yml` workflow** and instead enable GitHub's _default_
code scanning setup from the repository Security tab — it picks up
JavaScript/YAML/Actions workflow scanning, which is what would
realistically find a misconfigured release pipeline.

Mark this item _resolved by repo settings_ rather than adding a YAML
file that scans nothing useful.

### Implementation checklist

- [ ] 20.1 — Add `.github/dependabot.yml`.
- [ ] 20.2 — Add NuGet cache step to `ci.yml`.
- [ ] 20.3 — Add `concurrency` block to `ci.yml`.
- [ ] 20.4 — Add `ContinuousIntegrationBuild` conditional to
      `Directory.Build.props` + env vars in both workflows.
- [ ] 20.5 — Enable GitHub default code scanning in repo Settings →
      Security; no workflow YAML needed.

---

## Phase 21 — Repo hygiene

Small surface-area items expected on a published .NET tool. None of
them block work; pick up alongside the next docs pass.

- **CI status badge in `README.md`.**
  `![ci](https://github.com/HelgeSverre/fedit/actions/workflows/ci.yml/badge.svg)`
  plus a license badge. Sit them under the title — the README opens
  with `# fedit` followed by prose, so the badges replace nothing.
- **`SECURITY.md`.** A 10-line file: supported versions table (whatever
  the latest tagged release is), private disclosure email
  (`helge.sverre@gmail.com` already public on this repo). GitHub picks
  it up automatically and links it from the Security tab.
- **`.github/ISSUE_TEMPLATE/bug_report.md`** — short form: version,
  OS + terminal, reproduction. No 12-field template; the project is
  small and a heavy template suppresses real reports.
- **`.github/PULL_REQUEST_TEMPLATE.md`** — three checkboxes: `just
check` passes, screenshot for UI changes, `CHANGELOG.md` entry.
- **`FUNDING.yml`** — skip unless Helge wants Sponsors visible; an
  empty Sponsor button is worse than no button.
- **`CODEOWNERS`** — skip while the repo is single-maintainer. Add when
  a second committer appears, not before.

### Anti-patterns to avoid

- Don't add a 200-line `CONTRIBUTING.md` for a project this size. The
  README already documents `just check`; a separate file fragments docs.
- Don't add `.github/workflows/stale.yml` (auto-close stale issues).
  Aggressive on a small project; corrosive on community trust.
- Don't add a Discord/community link until there's a community to link
  to.

---

## Test suite maintenance

Applies to Tier 1 (shipped) and Phases 7 + 8:

- **No flaky tests, ever.** A test that fails intermittently gets
  deleted, not retried.
- **Snapshot diffs are reviewed, not blindly accepted.** Treat a
  `.verified.txt` change like a schema change.
- **Inner-loop tests run in <2s.** If `dotnet test` takes longer,
  split slow tests behind `[<Trait("Category", "slow")>]` and skip
  them in `just test` (run them only in CI).
- **Tier 3 stays small.** If smoke tests start churning, the answer is
  fewer scenarios, not more retries.

---

## Considered, not pursued (revisit later)

Items where shipped code already addresses the same concern with a
different approach. Each is parked with a revisit trigger; the
WIP-suggested redesign might still be the better long-term answer
once the trigger appears.

(The previous siblings — delta undo, `ensureViewport` simplification,
and the `Lines → Offsets` redesign — were promoted to Phase 16
after explicit decision.)

### Selection state shape — record vs anchor + cursor

**Shipped:** `BufferState.Selection : int option` (anchor) + `Cursor`
(other endpoint). `Buffer.selectionRange` derives `(min, max)`. Edits
clear `Selection` via `clearSelection` in motion handlers and replace
it via `deleteSelection`.

**Suggested (WIP):** `{ Start: int; End: int }` record on
`BufferState`, with explicit recalculation on every document
mutation.

**Trade-off:** the shipped design represents both endpoints already;
the WIP version makes them explicit but duplicates information.
Desynchronization is currently prevented by clearing on motion-edits.

**Revisit when:** a use case appears where the selection should
outlive the cursor (persistent highlights after a non-selecting
motion, "find all" mode that highlights without moving the cursor).

---

## Open questions

- Should buffers persist across runs (session file: which buffers were
  open, cursor positions, viewport scroll)? Or is the workspace tree
  enough?
- Is multi-cursor in scope long-term? It changes
  `BufferState.Cursor : Position` into `Cursors : Position list` and
  ripples through every motion + edit primitive.
- Plugin / scripting surface — stays out of scope unless someone asks.
- Release automation: see **Phase 19** (promoted out of Open questions
  into a concrete workflow shape).

---

## Deferred — Unified prompt / modal redesign

Captured from a design session 2026-05-20. Not in any phase yet; pending
a decision driven by the comparison page at
[`design/modality-explorer.html`](design/modality-explorer.html).

- **The lying glyph.** `View.fs:369,372` glues `:` and `/` onto the
  rendered prompt; neither prefix is in `CommandBar.Text` or
  `Search.Query`. Users can't backspace through them, can't type the
  other prefix to switch modes, and the two prompts have asymmetric
  backspace-on-empty behaviour (search closes; command bar no-ops).
- **Direction A — Unified Quick Open** (VS Code / Sublime model).
  One prompt with prefix dispatch: `(empty)` = file picker, `>` =
  commands, `/` = search, `:` = goto line, `!` / `!!` = shell, `@` =
  buffers. Phased: honest-prefix → unified `Prompt` state → full
  vocabulary.
- **Direction B — Modal redesign.** Helix-lite with Esc-driven
  prompt entry. Subsumes Direction A's prefix dispatch as a side-
  effect of unification.
- **Shell integration ladder.** `!cmd` async to dock (MVP-1, ~2d);
  `!!cmd` to read-only output buffer (MVP-2, +1–2d); SGR colour
  rendering (MVP-3, +2–3d); interactive PTY buffer (Full, 2–4 weeks).
  See the explorer for per-rung demos and the existing-pattern
  references in `Runtime.fs:76-109` (Process.Start) and
  `Runtime.fs:275-348` (async effect plumbing).
- **Recommendation in the explorer:** Helix-lite + Esc-as-universal-
  back + ship MVP-1 → 2 → 3 (defer Full). Ship the prompt
  unification first so both modality and shell features land on top
  of a consistent prompt.
