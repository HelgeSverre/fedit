# fedit roadmap

Active work and future ideas. Shipped phases (0–6) live in
[`CHANGELOG.md`](CHANGELOG.md).

## Status

| Phase                                                  | State   | Hook                                                                            |
| ------------------------------------------------------ | ------- | ------------------------------------------------------------------------------- |
| **Phase 7 — Tier 2 frame snapshots**                   | Pending | Verify.Xunit + `Snapshot.fs` projector + ~8–10 baseline scenarios.              |
| **Phase 8 — Tier 3 binary smoke**                      | Pending | `Process.Start` exit-code checks, 3–5 scenarios, no external tooling.           |
| **Phase 9 — Quick wins**                               | Pending | Buffer double-computeLines, `jsonEscape` round-trip, motion helper.             |
| **Phase 10 — Module splits**                           | Pending | Typed command payloads, `Editor.fs` split, `Runtime.fs` split.                  |
| **Phase 11 — Renderer diff**                           | Pending | Cell-level diff against previous frame; drop `pad`/`crop` allocations.          |
| **Phase 12 — Async follow-ups**                        | Pending | Dirty-state race after save, config-save ordering, search-as-effect.            |
| **Phase 13 — Workspace caching & startup errors**      | Pending | Flat `Map<string, FileNode>` cache; surface load errors instead of swallowing.  |
| **Phase 14 — Polish**                                  | Pending | Theme-preview placement, Recent debounce, named metadata record, tab width.    |

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
      `src/Fedit/bin/Debug/net9.0/` and spawns it with stdin/stdout
      redirected.
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
But the per-edit code path still runs `computeLines` *twice* per
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
array to walk. `changeDocument` then runs `computeLines` *again* on
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

| Where                   | Functions                                                                                                                                                                              | Budget |
| ----------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -----: |
| `Model.fs` (extend)     | + `Model.activeBuffer`, `Model.notify`, `Model.updateActiveBuffer` — the three shared helpers currently private in Editor. They're operations on the model.                            |    +30 |
| `Workspace.fs` (extend) | + `Workspace.resolvePath`, `Workspace.files` — path utilities about the workspace, not the editor.                                                                                     |    +15 |
| `CommandBar.fs` (new)   | `emptyCommandBar`, `themeFromApplyText`, `updatePreview`, `refreshCommandBar`, `openBar`, `closeBar`, `insertText`, `replaceText`, `deleteBackward/Forward`, `pushHistory`, `executeCommand`, `saveActive`, `switchBuffer`, `runCommandBar` |    280 |
| `Search.fs` (new)       | `openSearch`, `closeSearch`, `updateQuery`, `moveMatch`, `runSearch`                                                                                                                   |     80 |
| `Editor.fs` (shrunk)    | `init`, `normalizeNewlines`, `runGlobal`, `runEditor`, `runSidebar`, top-level `update`, external `Msg` handlers                                                                       |    330 |

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

**Pre-refactor test guardrails** — add these *before* moving code if
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

**Why.** `needsRender <- true` fires for *any* `Msg` — every
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
match the previous frame. Track `currentStyle` *across* rows so
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
buffer *without* proving the current document still matches the
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

`Buffer.findAll` itself stays where it is; only the *invocation* moves
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

**Action.** Add a `tabWidth: int` field to the config schema, default
4. `Runtime.loadConfig` reads it; `Editor.init` passes it into the
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

### Delta / patch undo vs snapshot + cap

**Shipped:** `BufferRevision` stores a full `PieceTable` snapshot per
undo step, capped at 200. The piece table shares `Original` / `Added`
strings by reference, so per-revision cost is only the `Pieces` list.

**Suggested (WIP):** store only `(startIndex, count, replacementText)`
deltas.

**Trade-off:** real win on multi-megabyte files with deep histories;
significant complexity on redo / composition (consecutive deletes
should merge to one delta, etc.). The shipped design's memory is
bounded by both the 200-cap and the string-sharing — not unbounded.

**Revisit when:** memory pressure shows up in a real profile, or a
"persistent undo across sessions" feature is wanted (deltas serialize
better than snapshots).

### `Buffer.ensureViewport` simplification

**Shipped:** `Buffer.ensureViewport` computes `maxTop` / `maxLeft` /
`nextTop` / `nextLeft` independently with clamped arithmetic.

**Suggested (WIP):** derive a single "target scroll offset" from
cursor + viewport dims.

**Trade-off:** same behavior, fewer lines. No bug observed in the
current form; the verbosity is the documentation of intent.

**Revisit when:** a viewport edge-case bug appears (e.g., scrolling
at the document tail, soft-wrap, virtual lines), or the function
needs extension and the current arithmetic gets in the way.

### `Lines : string[]` cache vs offsets-only

**Shipped:** `BufferState.Lines : string[]` materialized on every
edit (CHANGELOG P5/P1).

**Suggested (WIP #1):** derive `Lines` lazily.

**Suggested (IMPROVEMENTS P7 deeper question):** replace
`Lines : string[]` with `Offsets : int[]` of newline positions and
slice the piece table on demand.

**Trade-off:** the offsets-only design is the better long-term win
(less RAM, no duplicate file content). Phase 9.1 (the per-edit
double-compute fix) keeps the current cache shape but eliminates the
symptom that motivated the redesign.

**Revisit when:** memory shows up in a profile on large files, or the
next feature wants offsets directly (folding, large-file streaming,
incremental syntax highlighting).

---

## Open questions

- Should buffers persist across runs (session file: which buffers were
  open, cursor positions, viewport scroll)? Or is the workspace tree
  enough?
- Is multi-cursor in scope long-term? It changes
  `BufferState.Cursor : Position` into `Cursors : Position list` and
  ripples through every motion + edit primitive.
- Plugin / scripting surface — stays out of scope unless someone asks.
- Release automation: a `release.yml` triggered on tag pushes that
  runs `dotnet publish` per RID and uploads to a GitHub Release.
  Currently `just install` is local-only.
