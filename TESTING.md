# fedit testing strategy

Living plan for a complete automated test suite. **Scheduled to land
after Phase 2 (module reorganization)**, so tests can be written
against the split files instead of needing to be rewritten when
`Program.fs` is broken up. Other phases (3, 5) can land before or
after the test suite.

This document captures the decision and rationale up front so a future
session can implement it without re-deriving the design.

## TL;DR

Three-tier pyramid, weighted to the bottom two:

| Tier | What | Tools | Volume |
|------|------|-------|--------|
| **1** | Pure model tests against `update`, `PieceTable`, `Buffer`, `Commands.parse`, `Workspace` | xUnit + FsUnit.xUnit + FsCheck | ~50–150 tests |
| **2** | Frame snapshots: fold `Msg` list through `update` → `Layout.render` → stringified `Cell[,]` → golden file | Verify.Xunit | ~20–40 scenarios |
| **3** | Binary smoke: prove the actual `fedit` executable launches and quits cleanly | charmbracelet/vhs OR `Process.Start` exit-code checks | 3–5 cases |

All three run via `dotnet test` (+ `vhs` invocation for tier 3) on
GitHub Actions, one job, ubuntu-latest.

## Why this shape

fedit's architecture was — accidentally or not — built into the exact
shape that the Elm/Bubble Tea/Textual communities have already converged
on for testing TUIs:

- `Editor.update : Msg -> Model -> Model * Effect list` is pure.
- `Layout.render : Model -> Screen` is pure (`Screen.Cells : Cell[,]`).
- `Effect` is a closed sum (`ScanWorkspace | LoadFile | SaveBuffer`),
  asserted on directly — no mocking required.
- The runtime loop (`Runtime.run`) is the only impure boundary, and
  it's ~50 lines of input decoding + ANSI emission that change rarely.

That means **a snapshot of the rendered grid is e2e** for everything
except the runtime loop. We don't need a virtual terminal, a PTY, or a
VT500 emulator to catch layout, cursor, viewport, status-line, or
command-bar regressions.

This was confirmed by an internal multi-agent consensus analysis
(documented at the bottom of this file). 5/5 agents converged on
"tiers 1+2 with xUnit + Verify.Xunit"; the only real split was the
shape of tier 3.

## Tier 1 — Pure model tests

**Project layout.** Add a sibling project:

```
fedit.sln               # new — wraps both projects
fedit/                  # existing source
  fedit.fsproj
  ...
fedit.Tests/
  fedit.Tests.fsproj
  PieceTableTests.fs
  BufferTests.fs
  CommandsTests.fs
  WorkspaceTests.fs
  UpdateTests.fs        # Msg-sequence tests on Editor.update
  Properties.fs         # FsCheck invariants
```

**Package versions (current as of writing — verify before adoption):**

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.*" />
  <PackageReference Include="xunit" Version="2.9.*" />
  <PackageReference Include="xunit.runner.visualstudio" Version="2.8.*" />
  <PackageReference Include="FsUnit.xUnit" Version="6.*" />
  <PackageReference Include="FsCheck.Xunit" Version="3.*" />
</ItemGroup>
```

**Coverage targets.**

- `PieceTable`: insert/delete/length/toString round-trip; insert at
  index 0, end, mid-piece; delete spanning multiple pieces.
- `Buffer`: cursor motion (left/right/up/down/home/end with preferred
  column), undo/redo, indent/unindent, save → markSaved clears dirty.
- `Commands.parse`: every spec round-trips; prefix matching produces
  `Pending`; unknown commands produce `Invalid`.
- `Workspace`: tree flattening with selection + expansion, navigation
  through `moveSelection`, `expandSelected`, `collapseSelected`.
- `Editor.update`: scripted `Msg` sequences (open file → edit → save)
  produce expected `Model` *and* expected `Effect list`.

**Property tests (FsCheck).** These catch what scripted tests don't:

- `PieceTable`: `insert i s (insert i "" t) = insert i s t` (empty
  insert is identity); `length (insert i s t) = length t + s.Length`;
  `toString (insert (length t) s t) = toString t + s`.
- `Buffer`: cursor never lands outside the document; `undo ∘ insertText
  = identity` when starting from a clean state.
- `Commands.parse`: `parse (spec.Usage) ≠ Invalid` for every spec.

## Tier 2 — Frame snapshot tests

**Tool: Verify.Xunit 28.x.** Workflow: each test produces a
`<TestName>.received.txt`; on first run or intentional change, accept
via `dotnet verify accept` or the IDE plugin → `.verified.txt` is
committed alongside the test source.

**The load-bearing helper.** A `Cell[,] -> string` projector lives in
`fedit.Tests/Snapshot.fs`:

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
    // Optional: append cursor position line
    sb.ToString()
```

**Stability requirements** — get these wrong and snapshots churn:

- Pin terminal size per test (e.g., `Size = { Width = 80; Height = 24 }`).
- Normalize trailing whitespace (rstrip per line) — otherwise PadRight
  inserts trailing spaces that diff noisily.
- Include cursor `{Left,Top,Visible}` in the snapshot footer.
- Style markers must be deterministic — sort fields, don't rely on
  record default formatting.

**Scenarios to cover.** Each is `init → fold msgs → render → snapshot`:

- Cold start with empty workspace.
- Opened file, editor focus, cursor at line 3 col 5.
- Sidebar focused, file tree expanded one level.
- Command bar active with `:o` typed, completions visible.
- Command bar active with `:theme yel` typed (covers Phase 0 themes).
- Dirty buffer in status line.
- Buffer scrolled horizontally and vertically.
- Notification banner showing each `Severity`.

**Why not `Model` assertions instead?** Tier 1 already does that.
Tier 2's job is catching the bugs that pass tier 1 but render wrong —
gutter width drift (F1 in TODO), off-by-one in viewport math, status
line truncation, command-bar cursor position. The rendered grid is
the only data structure that contains all of those at once.

## Tier 3 — Binary smoke

**Open question — pick one before implementing.** Both options are
documented because the consensus didn't resolve this.

### Option A: charmbracelet/vhs (richer, dual-purpose)

`tests/e2e/*.tape` files driven by `vhs` (`brew install vhs`, or
`charmbracelet/vhs-action@v2` in CI). VHS spawns the binary in a real
PTY (via ttyd), sends scripted keystrokes, and emits text frames and
optional GIFs.

```
# tests/e2e/cold-start.tape
Output tests/e2e/cold-start.txt
Set Shell "bash"
Set TypingSpeed 50ms
Type "fedit ."
Enter
Sleep 500ms
Type ":q"
Enter
```

CI: separate `vhs-smoke` GitHub Actions job, runs on PRs to `main`
only, not on every push. Failures don't block — they file an issue.

**Dual purpose:** the same `.tape` files generate the README demo
GIF and release-notes screencaps. One artifact, three uses (test,
demo, docs). This is the main reason to prefer vhs over option B.

**Scenarios (cap at 5):**
1. Cold start → quit cleanly.
2. Open file via `Ctrl+P :o README.md` → type a line → save → quit.
3. Sidebar navigation: `Ctrl+B`, arrow keys, Enter to open.
4. Command bar completions: `Ctrl+P` then `wri<Tab>`.
5. Theme switch (post Phase 0).

### Option B: `Process.Start` exit-code checks (boring, bulletproof)

```fsharp
[<Fact>]
let ``binary launches and exits cleanly`` () =
    use proc = Process.Start(...)
    proc.StandardInput.Write("q")
    proc.StandardInput.Close()
    proc.WaitForExit(5000) |> ignore
    proc.ExitCode |> shouldEqual 0
```

No external dependencies, runs inside `dotnet test`. Catches "binary
doesn't launch" / "binary doesn't shut down" but nothing more.

### Recommendation

**Start with B** during the initial test rollout (zero new tooling,
runs in the same `dotnet test` invocation). **Promote to A** when
the README demo GIF is wanted anyway — at that point vhs pays for
itself twice.

## What we explicitly rejected

- **Pty.Net** — works, but you'd own a VT emulator yourself.
- **XtermSharp** — unmaintained.
- **vtnet** — unmaintained.
- **expect/pexpect** — race-prone against repaints; ANSI-blind.
- **tmux send-keys + capture-pane** — robust elsewhere, but adds
  tmux as a CI dependency and the rendered frame is what we'd assert
  on anyway — Verify.Xunit gives us that in-process.

The .NET TUI testing ecosystem is genuinely thin. The architectural
purity of `update` + `render` lets us route around it.

## CI integration

```yaml
# .github/workflows/ci.yml (sketch)
name: ci
on: [push, pull_request]
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet test --configuration Release
  vhs:
    # only if Tier 3 Option A is chosen
    runs-on: ubuntu-latest
    needs: test
    if: github.event_name == 'pull_request' && github.base_ref == 'main'
    steps:
      - uses: actions/checkout@v4
      - uses: charmbracelet/vhs-action@v2
      - run: |
          for tape in tests/e2e/*.tape; do
            vhs "$tape"
          done
          # diff against tests/e2e/*.txt golden files
```

Add `just test` (runs `dotnet test`) and, if Option A: `just test-e2e`
(runs the vhs tapes).

## Implementation order

When this lands, the suggested sequence is:

1. **Create `fedit.Tests` project + `fedit.sln`.** Wire `just test`.
2. **Tier 1 first.** PieceTable, Buffer, Commands.parse, Workspace.
   These are the lowest-risk and surface bugs from Phase 5 work
   (line-offset caching especially needs Buffer tests as a safety net).
3. **Add FsCheck properties for PieceTable.** Cheap once xUnit is wired.
4. **Tier 2 second.** Build `Snapshot.render`, then 8–10 starter
   scenarios. Accept the initial `.verified.txt` baselines.
5. **Add Editor.update Msg-sequence tests** in tier 1. These benefit
   from existing snapshot fixtures (reuse the `Msg list`s).
6. **Wire GitHub Actions** — single `dotnet test` job.
7. **Decide on tier 3 form**, implement the chosen option.

Total estimated effort: 1–2 focused days for tiers 1+2 plus CI, +
0.5 day for whichever tier 3 path.

## Maintenance discipline

To prevent the suite from being abandoned (see pre-mortem analysis
in TODO.md spirit):

- **No flaky tests, ever.** A test that fails intermittently gets
  deleted, not retried.
- **Snapshot diffs are reviewed, not blindly accepted.** Treat a
  `.verified.txt` change like a schema change.
- **Inner-loop tests run in <2s.** If `dotnet test` takes longer,
  split slow tests behind `[<Trait("Category", "slow")>]` and skip
  them in `just test` (run them only in CI).
- **Tier 3 stays small.** If vhs tapes start churning, the answer is
  fewer tapes, not more retries.

## Appendix: consensus analysis summary

This strategy was derived from a 5-agent consensus analysis (lenses:
pragmatist, minimalist, skeptic, pre-mortem, archaeologist). Key
findings:

- **5/5 agreed:** xUnit + FsUnit + Verify.Xunit, sibling `fedit.Tests`
  project, reject all unmaintained .NET PTY libraries, CI via
  `dotnet test` on ubuntu-latest.
- **4/5 agreed:** Keep tier 1 separate from tier 2 (Minimalist
  dissented, arguing snapshots subsume model assertions — technically
  correct but loses debugging granularity).
- **Real split on tier 3:** vhs (3/5) vs. `Process.Start` smoke
  (1/5) vs. nothing (1/5). Resolution: start with `Process.Start`,
  upgrade to vhs when the README demo GIF is needed.
- **Best outlier idea:** vhs `.tape` files double as README demos
  and release artifacts — the strongest argument for the richer
  tier 3 path, since the cost is partially recouped by documentation.
- **Best outlier idea (runner-up):** FsCheck property tests for
  PieceTable invariants — folded into tier 1 above.
