# fedit roadmap

Active work and future ideas. Shipped phases (0–6) live in
[`CHANGELOG.md`](CHANGELOG.md).

## Status

| Phase                                | State                                                                          |
| ------------------------------------ | ------------------------------------------------------------------------------ |
| **Phase 7 — Tier 2 frame snapshots** | Pending. Verify.Xunit + `Snapshot.fs` projector + ~8–10 baseline scenarios.    |
| **Phase 8 — Tier 3 binary smoke**    | Pending. `Process.Start` exit-code checks, 3–5 scenarios, no external tooling. |

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

### The Snapshot.render projector

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

## Open questions

- Should buffers persist across runs (session file: which buffers were
  open, cursor positions, viewport scroll)? Or is the workspace tree
  enough?
- Is multi-cursor in scope long-term? It changes `BufferState.Cursor :
  Position` into `Cursors : Position list` and ripples through every
  motion + edit primitive.
- Plugin / scripting surface — stays out of scope unless someone asks.
- Release automation: a `release.yml` triggered on tag pushes that
  runs `dotnet publish` per RID and uploads to a GitHub Release.
  Currently `just install` is local-only.
