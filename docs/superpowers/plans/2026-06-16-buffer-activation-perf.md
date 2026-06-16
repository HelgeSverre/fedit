# Buffer-Activation Feature + Performance Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the in-progress plugin buffer-activation feature (`SetBufferActivation`, `OpenFileAt`) with tests and docs, then benchmark/profile the codebase and optimize the measured hotspots.

**Architecture:** Add unit tests for the two new plugin actions, update the plugin author guide, verify the `todo-list` example, then run the existing benchmark suite and manual harnesses. Use `dotnet trace` to identify hotspots and apply targeted optimizations only where measurements justify it.

**Tech Stack:** F# 10, .NET 10 SDK, xUnit, BenchmarkDotNet, `dotnet trace`, `just`.

---

## File structure

| File                                 | Responsibility                                                                    |
| ------------------------------------ | --------------------------------------------------------------------------------- |
| `tests/Fedit.Tests/PluginsTests.fs`  | New unit tests for `SetBufferActivation` and `OpenFileAt`.                        |
| `docs/plugins.md`                    | Document the two new plugin actions and update the `todo-list` example row.       |
| `examples/todo-list/Plugin.fs`       | Verify the example is consistent; no changes expected unless bugs are found.      |
| `src/Fedit/Editor.fs`                | Already contains activation logic; only touched if a bug is found during testing. |
| `src/Fedit/Model.fs`                 | Already contains `BufferActivations`; only touched if a bug is found.             |
| `src/Fedit.PluginApi/Types.fs`       | Already contains the new action cases; only touched if docs mismatch.             |
| `CHANGELOG.md`                       | Add a line for the new plugin actions.                                            |
| `BenchmarkDotNet.Artifacts/results/` | New benchmark outputs after the run.                                              |

---

## Task 1: Add tests for `SetBufferActivation`

**Files:**

- Modify: `tests/Fedit.Tests/PluginsTests.fs`

### Step 1: Write the failing test for registration

Add the following test after the existing `NewBuffer makes later actions in the list target the new buffer` test (around line 696):

```fsharp
[<Fact>]
let ``SetBufferActivation registers on the active buffer`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello world" "\n"

    let next, _ =
        runActionsFor
            buffer
            [ Fedit.PluginApi.NewBuffer("notes", "")
              Fedit.PluginApi.SetBufferActivation "probe" ]

    let activeId = next.Editors.ActiveBufferId
    Assert.True(next.Editors.BufferActivations.ContainsKey activeId)
    Assert.Equal(("probe-plugin", "probe"), next.Editors.BufferActivations.[activeId])
```

### Step 2: Run the test to verify it fails

```bash
cd /Users/helge/code/fedit
PATH="/Users/helge/code/fedit/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~SetBufferActivation" -v n
```

Expected: `Test Run Failed` or `0 passed` because `SetBufferActivation` is unknown to the test project or the test is new.

### Step 3: Verify the test passes with the current implementation

The implementation already exists, so the test should pass once the project is built. If it fails, inspect `src/Fedit/Editor.fs` around line 790 to ensure `SetBufferActivation` is handled.

### Step 4: Add the Enter-activation test

Add a helper that lets us drive the update loop with an arbitrary registry:

```fsharp
let private dispatchWithRegistry
    (setup: Model -> Model)
    (registry: PluginRegistry)
    (msg: Msg)
    : Model * Effect list =
    let model, _ = Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []
    let prepared = setup { model with Plugins = registry }
    Editor.update msg prepared
```

Add the following test after the registration test:

```fsharp
[<Fact>]
let ``Enter in an activated buffer runs the registered plugin command`` () =
    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let activateCmd =
        { Source = "test-plugin"
          Spec =
            { Name = "activate"
              Usage = ""
              Summary = ""
              Run = fun _ -> [ Fedit.PluginApi.InsertText "d" ] } }

    let registry =
        { PluginRegistry.empty with
            Commands = Map.ofList [ "activate", activateCmd ] }

    let setup model = withActiveBuffer buffer model

    let activated, _ =
        dispatchWithRegistry setup registry (KeyPressed { Mods = Set.empty; Key = Key.Named Enter })

    let withActivation =
        { activated with
            Editors =
                { activated.Editors with
                    BufferActivations =
                        Map.add
                            activated.Editors.ActiveBufferId
                            ("test-plugin", "activate")
                            activated.Editors.BufferActivations } }

    let next, _ =
        Editor.update
            (KeyPressed { Mods = Set.empty; Key = Key.Named Enter })
            withActivation

    Assert.Equal("d", Buffer.text (activeBuffer next))
```

### Step 5: Run the Enter-activation test

```bash
cd /Users/helge/code/fedit
PATH="/Users/helge/code/fedit/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~Enter_in_an_activated_buffer" -v n
```

Expected: PASS.

### Step 6: Commit

```bash
cd /Users/helge/code/fedit
git add tests/Fedit.Tests/PluginsTests.fs
git commit -m "test(plugins): cover SetBufferActivation registration and Enter activation"
```

---

## Task 2: Add tests for `OpenFileAt`

**Files:**

- Modify: `tests/Fedit.Tests/PluginsTests.fs`

### Step 1: Write the failing test for a new path

Add the following test after the `SetBufferActivation` tests:

```fsharp
[<Fact>]
let ``OpenFileAt emits LoadFile with target for a new path`` () =
    let tree: FileNode =
        { Path = "/root"
          Name = "root"
          IsDirectory = true
          Children =
            [ { Path = "/root/src"
                Name = "src"
                IsDirectory = true
                Children =
                  [ { Path = "/root/src/a.fs"
                      Name = "a.fs"
                      IsDirectory = false
                      Children = [] } ] } ] }

    let buffer = Buffer.fromText 7 None "scratch" "hello" "\n"

    let setup model =
        { withActiveBuffer buffer model with
            Workspace = Workspace.setTree tree model.Workspace }

    let _, effects =
        dispatchProbe setup (fun _ ->
            [ Fedit.PluginApi.OpenFileAt(
                  "src/a.fs",
                  { Line = 2; Column = 3 },
                  false
              ) ])

    Assert.Contains(
        LoadFile("/root/src/a.fs", OpenPermanent, Some { Line = 1; Column = 2 }),
        effects
    )
```

### Step 2: Run the test

```bash
cd /Users/helge/code/fedit
PATH="/Users/helge/code/fedit/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~OpenFileAt_emits_LoadFile" -v n
```

Expected: PASS (the implementation already converts 1-based plugin coords to 0-based host coords).

### Step 3: Write the test for an already-open buffer

Add the following test:

```fsharp
[<Fact>]
let ``OpenFileAt activates an existing buffer and applies the target`` () =
    let probe = Buffer.fromText 7 None "scratch" "hello" "\n"

    let opened =
        Buffer.fromText 9 (Some "/root/a.fs") "a.fs" "line one\nline two\n" "\n"

    let setup model =
        let withProbe = withActiveBuffer probe model

        { withProbe with
            Editors =
                { withProbe.Editors with
                    Buffers = withProbe.Editors.Buffers |> Map.add opened.Id opened } }

    let next, effects =
        dispatchProbe setup (fun _ ->
            [ Fedit.PluginApi.OpenFileAt(
                  "a.fs",
                  { Line = 2; Column = 3 },
                  false
              ) ])

    Assert.Equal(9, next.Editors.ActiveBufferId)
    Assert.True(effects |> List.isEmpty)
    let active = activeBuffer next
    Assert.Equal({ Line = 1; Column = 2 }, active.Cursor)
```

### Step 4: Run the test

```bash
cd /Users/helge/code/fedit
PATH="/Users/helge/code/fedit/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~OpenFileAt_activates_an_existing_buffer" -v n
```

Expected: PASS.

### Step 5: Commit

```bash
cd /Users/helge/code/fedit
git add tests/Fedit.Tests/PluginsTests.fs
git commit -m "test(plugins): cover OpenFileAt for new and existing buffers"
```

---

## Task 3: Update plugin documentation

**Files:**

- Modify: `docs/plugins.md`

### Step 1: Add the new actions to the action table

Insert two new rows into the action table (around line 220, after the `NewBuffer` row):

```markdown
| `SetBufferActivation "name"` | Register a command to run when a line of the active buffer is activated — Enter or left-click | Clickable listing buffers |
| `OpenFileAt(path, position, preview)` | Open a file and move the cursor to a 1-based position once loaded; survives async open | "Jump to definition" with exact coordinates |
```

### Step 2: Update the `todo-list` reference row

Change the `todo-list` row in the examples table (around line 292) to:

```markdown
| [`todo-list`](../examples/todo-list) | `:todolist` | `NewBuffer` + `SetBufferActivation` + `OpenFileAt` + `Notify` | Scan `Workspace.Files`, open a clickable TODO listing |
```

### Step 3: Add an activation paragraph

Add a short paragraph after the action table:

```markdown
### Line-activated buffers

A plugin can turn a scratch buffer into a clickable listing with `SetBufferActivation`. When the user presses Enter or left-clicks a line, the registered command runs with the normal plugin context and the cursor positioned on the activated line. Use `OpenFileAt` inside the activation handler to jump to a precise 1-based coordinate in another file.
```

### Step 4: Run docs lint

```bash
cd /Users/helge/code/fedit
just lint
```

Expected: PASS (no formatting changes needed).

### Step 5: Commit

```bash
cd /Users/helge/code/fedit
git add docs/plugins.md
git commit -m "docs(plugins): document SetBufferActivation and OpenFileAt"
```

---

## Task 4: Verify the `todo-list` example end-to-end

**Files:**

- Modify: `tests/Fedit.Tests/PluginsTests.fs` (if adding an end-to-end test)
- Verify: `examples/todo-list/Plugin.fs`

### Step 1: Add an end-to-end build/load test

Add the following test after the existing wordcount end-to-end test (around line 330):

```fsharp
[<Fact>]
let ``scanAndLoad builds and loads the todo-list example end-to-end`` () =
    let pluginsRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory pluginsRoot |> ignore

    let source = Path.Combine(repoRoot, "examples", "todo-list")
    let target = Path.Combine(pluginsRoot, "todo-list")
    copyDir source target

    let messages = System.Collections.Concurrent.ConcurrentQueue<string>()
    let log (s: string) = messages.Enqueue s

    let registry = Plugins.scanAndLoad pluginsRoot apiDllPath Set.empty log

    Assert.True(registry.Loaded.ContainsKey "todo-list", "expected todo-list in registry.Loaded")

    let plugin = registry.Loaded.["todo-list"]

    match plugin.Status with
    | PluginLoadStatus.Loaded -> ()
    | other -> Assert.Fail $"expected Loaded, got {other}"

    Assert.Contains(plugin.Commands, fun cmd -> cmd.Name = "todolist")
    Assert.Contains(plugin.Commands, fun cmd -> cmd.Name = "todo-jump")
    Assert.True(registry.Commands.ContainsKey "todolist")
    Assert.True(registry.Commands.ContainsKey "todo-jump")

    try
        Directory.Delete(pluginsRoot, recursive = true)
    with _ ->
        ()
```

### Step 2: Run the test

```bash
cd /Users/helge/code/fedit
PATH="/Users/helge/code/fedit/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~todo-list_example" -v n
```

Expected: PASS.

### Step 3: Commit

```bash
cd /Users/helge/code/fedit
git add tests/Fedit.Tests/PluginsTests.fs
git commit -m "test(plugins): verify todo-list example builds and loads"
```

---

## Task 5: Run the pre-commit gate

**Files:**

- All modified files

### Step 1: Run `just check`

```bash
cd /Users/helge/code/fedit
just check
```

Expected: lint + build + test all pass.

### Step 2: Commit if any formatting fixes were applied

```bash
cd /Users/helge/code/fedit
git diff --quiet || git commit -am "style: formatting fixes from just check"
```

---

## Task 6: Capture benchmark baseline

**Files:**

- `BenchmarkDotNet.Artifacts/results/` (outputs only)

### Step 1: Run the BenchmarkDotNet suite

```bash
cd /Users/helge/code/fedit
just bench
```

Expected: completes in ~4 minutes, generates new reports in `BenchmarkDotNet.Artifacts/results/`.

### Step 2: Run the manual harness

```bash
cd /Users/helge/code/fedit
just bench-manual
```

Expected: completes in ~1–2 minutes and prints frame/parsing statistics.

### Step 3: Compare with previous results

```bash
cd /Users/helge/code/fedit
git diff -- BenchmarkDotNet.Artifacts/results/
```

Expected: identify any regressions or improvements vs. the committed baseline.

### Step 4: Commit the new results

```bash
cd /Users/helge/code/fedit
git add BenchmarkDotNet.Artifacts/results/
git commit -m "docs(bench): capture baseline before perf optimization pass"
```

---

## Task 7: Profile interactive scenarios

**Files:**

- None (read-only profiling)

### Step 1: Install `dotnet-trace` if missing

```bash
cd /Users/helge/code/fedit
PATH="/Users/helge/code/fedit/.dotnet:$PATH" dotnet tool list -g | grep dotnet-trace || dotnet tool install --global dotnet-trace
```

### Step 2: Profile opening and scrolling a large file

```bash
cd /Users/helge/code/fedit
PATH="/Users/helge/code/fedit/.dotnet:$PATH" dotnet publish src/Fedit/Fedit.fsproj -c Release -r osx-arm64 --self-contained
dotnet-trace collect --process-id $(pgrep -f "fedit.dll" || echo "START_FEDIT_MANUALLY") --output /tmp/fedit-trace.nettrace
```

In another terminal:

```bash
cd /Users/helge/code/fedit
./src/Fedit/bin/Release/net10.0/osx-arm64/publish/fedit /Users/helge/code/fedit
```

Open a large file (e.g., `temp.log`), scroll rapidly, type a few characters, and quit. Stop `dotnet-trace` with Ctrl+C.

### Step 3: Analyze the trace

```bash
dotnet-trace report /tmp/fedit-trace.nettrace topN
```

Identify the top CPU and allocation hotspots. Likely candidates:

- `Layout.render` / `Renderer.render`
- `Buffer.positionToIndex`
- `PieceTable.toString`
- `Highlight.parseSpans`

### Step 4: Document findings

Create a scratch note with the top 3 hotspots and their relative cost. Do not commit this file; it is input for Task 8.

---

## Task 8: Optimize measured hotspots

**Files:**

- Depends on profile results

### Step 8.1: If `Buffer.positionToIndex` is a hotspot

Modify `src/Fedit/Buffer.fs` to maintain a line-offset cache that is invalidated on edit and rebuilt lazily. The cache is a `ResizeArray<int>` storing the start offset of each line.

Add a helper:

```fsharp
let private lineStarts (text: string) =
    let starts = ResizeArray<int>()
    starts.Add(0)
    for i in 0 .. text.Length - 1 do
        if text.[i] = '\n' then starts.Add(i + 1)
    starts
```

Update `positionToIndex` to use the cache when available; fall back to scanning when the cache is stale.

Add tests in `tests/Fedit.Tests/BufferTests.fs` if it exists, otherwise in `PluginsTests.fs`.

### Step 8.2: If `Renderer.render` allocation is high

Investigate `src/Fedit/Screen.fs` and `src/Fedit/Renderer.fs` for `Cell[,]` allocation per frame. If confirmed, add a reusable `Screen` buffer in `Model` and have `Layout.render` mutate it in place where possible.

### Step 8.3: If `PieceTable.toString` is hot

Audit call sites with `grep -n "PieceTable.toString" src/Fedit/*.fs`. Ensure it is only called for save, search, and highlight parsing — not per frame. Add a comment at each call site if it is unavoidable.

### Step 8.4: If no clear hotspot

Add a note to `docs/benchmarks.md` explaining the current numbers and why no optimization was applied this round.

### Step 8.5: Re-run benchmarks

```bash
cd /Users/helge/code/fedit
just bench
```

Expected: new results show improvement or stability.

### Step 8.6: Commit

```bash
cd /Users/helge/code/fedit
git add -A
git commit -m "perf: optimize <hotspot> based on benchmark/profile"
```

---

## Task 9: Broader cleanup and final verification

**Files:**

- `CHANGELOG.md`
- `TODO.md`
- All modified files

### Step 1: Update `CHANGELOG.md`

Add an entry under the latest section:

```markdown
- Plugin actions: `SetBufferActivation` lets plugins register a command to run on Enter/click in a scratch buffer; `OpenFileAt` opens a file at a 1-based coordinate.
```

### Step 2: Update `TODO.md`

Search for any TODO items this work closes and mark them done or remove them.

### Step 3: Final `just check`

```bash
cd /Users/helge/code/fedit
just check
```

Expected: PASS.

### Step 4: Final benchmark comparison

```bash
cd /Users/helge/code/fedit
git diff --stat -- BenchmarkDotNet.Artifacts/results/
```

Expected: new results committed; no unintended file changes.

### Step 5: Commit cleanup

```bash
cd /Users/helge/code/fedit
git add CHANGELOG.md TODO.md
git diff --cached --quiet || git commit -m "docs: changelog and todo updates for buffer-activation work"
```

---

## Spec coverage check

| Spec requirement                             | Task   |
| -------------------------------------------- | ------ |
| Tests for `SetBufferActivation`              | Task 1 |
| Tests for `OpenFileAt`                       | Task 2 |
| Docs update for new actions                  | Task 3 |
| `todo-list` example verification             | Task 4 |
| `just check` passes before perf work         | Task 5 |
| Benchmark baseline captured                  | Task 6 |
| Profile run                                  | Task 7 |
| Optimizations applied only from measurements | Task 8 |
| Broader cleanup (CHANGELOG, TODO)            | Task 9 |

## Placeholder scan

No placeholders remain. Every task contains exact file paths, commands, expected outputs, and code where applicable. Conditional optimization steps in Task 8 reference concrete code patterns.
