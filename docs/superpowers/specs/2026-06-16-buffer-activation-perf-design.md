# Design: finish buffer-activation feature + performance pass

## Goal

Complete the in-progress plugin buffer-activation feature (`SetBufferActivation`,
`OpenFileAt`) and run a focused performance review on the current codebase.

## Context

The working tree contains uncommitted changes that add two new plugin actions:

- `SetBufferActivation commandName` — register a plugin command to run when a
  line of the active buffer is activated (Enter or left-click).
- `OpenFileAt(path, position, preview)` — open a file and jump the cursor to a
  1-based position, surviving the async load.

These are already wired through `Editor.fs`, `Model.fs`, `Runtime.fs`, and the
`todo-list` example plugin. The code compiles and the test suite passes in Debug,
but the feature lacks tests and documentation.

## Approach

Sequential pipeline:

1. Finish the feature (tests + docs + example verification).
2. Run benchmarks and profile.
3. Optimize whatever the measurements flag.
4. Do any broader cleanup that surfaces during the work.

No speculative optimization — we measure first, then fix.

## 1. Finish the feature

### Tests

Add tests in `tests/Fedit.Tests/PluginsTests.fs` under the existing
"Plugin actions (API v1.1)" section:

- `SetBufferActivation` registers on the active buffer and can be queried
  indirectly via an activation-triggered effect.
- `OpenFileAt` on a new path emits `LoadFile(path, OpenPermanent, Some target)`.
- `OpenFileAt` on an already-open buffer activates it and applies the target.
- Enter in a buffer with an activation runs the registered plugin command
  instead of inserting a newline.

### Documentation

Update `docs/plugins.md`:

- Add `SetBufferActivation` and `OpenFileAt` to the action table.
- Update the `todo-list` reference row to mention click-to-jump behavior.
- Add a short paragraph explaining that activation commands receive the normal
  plugin context with the cursor on the activated line.

### Examples

- Verify `examples/todo-list` builds and loads cleanly.
- Confirm the emitted `path:line:col` format is consistent with the jump parser.

### Cleanup

- Remove leftover phase comments already partially cleaned in the diff.
- Verify the `Fedit.PluginApi` version bump to `1.2.0` is intentional and
  consistent with `apiVersion` in the docs.
- Run `just check` before moving on.

## 2. Benchmark and profile

### Benchmarks

Run the existing harnesses:

- `just bench` — BenchmarkDotNet micro suite (Release, ~4 min).
- `just bench-manual` — frame-pipeline + tree-sitter parse harness (~1–2 min).

Compare outputs with the existing
`BenchmarkDotNet.Artifacts/results/` files to detect regressions or wins.

### Profiling

Profile interactive editor scenarios with large files:

- `dotnet trace` while opening a large file, scrolling, and typing rapidly.
- Focus on the frame loop (`Layout.render`, `Renderer.render`), buffer
  indexing (`Buffer.positionToIndex`), and syntax highlighting (`Highlight.parseSpans`).

## 3. Optimize

Address only what the benchmarks/profile identify. Likely candidates based on
existing numbers:

- `RendererBenchmarks.FullRepaint` allocates ~1.5 MB for a 250×70 screen;
  investigate `Screen`/`Cell` reuse or diff cost.
- `BufferBenchmarks.PositionToIndex` scales linearly with document size;
  consider a line-offset cache or faster indexing.
- `PieceTableBenchmarks.ToStringWhole` materializes the whole document;
  ensure it is only called when necessary.

## 4. Broader cleanup

- Run `just lint` and fix formatting issues.
- Update `CHANGELOG.md` for the new plugin actions if user-facing.
- Review `TODO.md` for items this work closes.

## Success criteria

- `just check` passes (lint + build + test).
- New tests cover `SetBufferActivation` and `OpenFileAt`.
- `docs/plugins.md` documents both new actions.
- Benchmarks run successfully and any optimization has before/after numbers.
- No regressions in existing benchmarks.

## Scope cuts / risks

- No new plugin actions beyond `SetBufferActivation` and `OpenFileAt`.
- No speculative optimization.
- The uncommitted code is the baseline; we preserve it, not rewrite it.
