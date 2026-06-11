# Benchmarks

Measure before optimizing. The suite in `benchmarks/Fedit.Benchmarks/` pins
the editor's hot paths so piece-table, line-cache, highlight, and renderer
changes are judged by numbers, not guesses.

## Run

    just bench                  # full BenchmarkDotNet micro suite, ~4 min
    just bench '*PieceTable*'   # one class, ~1 min (any BDN --filter glob)
    just bench-manual           # frame pipeline + tree-sitter parse, ~1-2 min
    just bench-manual frames    # frame pipeline only
    just bench-manual parse     # parse only

Both recipes build Release; BenchmarkDotNet refuses non-optimized assemblies
and the manual harness prints a warning. The benchmark project is not in
`Fedit.slnx` and not referenced by the tests, so `just check` is unaffected.

Ignore the `DEBUG` tag in BDN's host line: the F# compiler always emits a
`DebuggableAttribute`, even in Release, and BDN flags its presence rather
than checking `IsJITOptimizerDisabled`. Both assemblies are verified
optimized in Release (`IsJITOptimizerDisabled = false`).

Run on AC power with the machine otherwise idle. Jobs use BDN `ShortRun`
(3 warmup + 3 measured iterations) on an in-process toolchain: fast and
reliable with the pinned SDK and native grammar sidecars, at the cost of
process isolation. Treat results as relative comparisons on one machine,
not absolute truths; expect a few percent run-to-run noise.

## What each suite pins

| Suite                       | Pins                                                                                                                                                             |
| --------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PieceTableBenchmarks`      | insert/delete/toString/length on a fragmented table at 10k/100k/1M chars                                                                                         |
| `EditSessionBenchmarks`     | a typing burst; `BufferTyping` includes the per-keystroke `computeLines` rebuild (the line-cache splice target), `PieceTableTyping` isolates the add-buffer copy |
| `BufferBenchmarks`          | single-keystroke insert, index/position conversion, file-load cost                                                                                               |
| `HighlightLookupBenchmarks` | `Highlight.spanAt` and the renderer's per-row overlay loop                                                                                                       |
| `ColorBenchmarks`           | Rgb -> 256 quantization (renderer downgrade path)                                                                                                                |
| `RendererBenchmarks`        | screen diff: full repaint, one-cell, scroll at 80x24 and 250x70                                                                                                  |
| manual `frames`             | `Layout.render` + `Renderer.render` per-frame p50/p95, highlight on/off; includes today's `Status.render` format reparse                                         |
| manual `parse`              | `Highlight.parseSpans` per document size — the per-edit-tick cost of the current always-reparse design                                                           |

The corpus is deterministic (`Corpus.fs`): the same size always produces the
same text, so before/after runs compare identical work.

## Read the output

BDN: `Mean` is the headline; `Allocated` (from `[MemoryDiagnoser]`) is the
per-operation allocation — watch it as closely as time, GC pressure is frame
jitter. `Error`/`StdDev` are wide under ShortRun; rerun with
`--job medium` style filters only when a result looks implausible.

Manual harness: one row per scenario with mean/p50/p95/max milliseconds per
frame (or parse) and KB allocated per operation. Compare p95 against a
16.6 ms frame budget.

## Before/after workflow

1. On the baseline commit: `just bench '*Buffer*'` (or the relevant filter).
2. Save the report: copy
   `BenchmarkDotNet.Artifacts/results/*-report-github.md` somewhere outside
   the repo, or paste the table into the PR description.
3. Apply the change, rerun the same filter on the same machine, compare.

`BenchmarkDotNet.Artifacts/` is gitignored; nothing under it is meant to be
committed. Commit benchmark tables into PR descriptions or design docs when
they justify a change.

## Grammars

The fsharp grammar native is built locally (`just build-grammars`); without
it the manual harness falls back to a bundled grammar (javascript/json) and
says so. Fallback numbers are still valid for before/after comparison on the
same machine, but not comparable across grammars.

## Baseline — 2026-06-11

Apple M2 Max, macOS 15.6, .NET 10.0.0, ShortRun in-process. Commit
`c61357b`. Recorded to anchor the planned piece-table/line-cache work;
rerun the same filters on the same machine to compare.

### Edit path (the headline)

| Method           | Keystrokes | Mean       | Allocated |
| ---------------- | ---------- | ---------- | --------- |
| PieceTableTyping | 256        | 23.4 us    | 166 KB    |
| BufferTyping     | 256        | 45,214 us  | 166 MB    |
| PieceTableTyping | 1024       | 128.5 us   | 1.4 MB    |
| BufferTyping     | 1024       | 182,280 us | 684 MB    |

Buffer-level typing on a 100k-char buffer costs ~178 us and ~667 KB
allocated per keystroke — ~1,900x the piece-table op underneath it. The
gap is `computeLines` (full toString + Split per edit) plus the
add-buffer copy. This is the measured case for the incremental
line-cache splice and append-only add-buffer.

### PieceTable / Buffer micro

| Method          | 10k     | 100k     | 1M       |
| --------------- | ------- | -------- | -------- |
| InsertMiddle    | 11.6 us | 11.6 us  | 11.6 us  |
| DeleteMiddle    | 17.8 us | 17.5 us  | 17.4 us  |
| ToStringWhole   | 23.0 us | 106.2 us | 1,211 us |
| LengthWhole     | 0.94 us | 0.93 us  | 0.94 us  |
| PositionToIndex | 1.1 us  | 9.8 us   | 96.9 us  |
| IndexToPosition | 0.09 us | 0.77 us  | 10.8 us  |

Piece-table ops are size-independent (good); `PositionToIndex` scans
lines linearly and is on the motion/selection/mouse path — a line-offset
index would flatten it.

### Render / highlight / color

| Scenario                       | Value                        |
| ------------------------------ | ---------------------------- |
| Renderer DiffOneCell 80x24     | 37 us, ~1 KB alloc           |
| Renderer DiffOneCell 250x70    | 326 us, ~1 KB alloc          |
| Renderer FullRepaint 250x70    | 898 us                       |
| spanAt overlay row (200 cols)  | 2.0-2.9 us                   |
| Color QuantizeAccent (1 color) | 2.6 us, 15.6 KB alloc        |
| Frame p95, 250x70 highlight on | 2.1 ms (budget 16.6 ms)      |
| parseSpans fsharp              | ~11 ms + ~3 MB per 10k chars |

The render diff is healthy (near-zero allocation on small diffs). The
two cheap wins visible here: `Color.cubeRgb` re-allocates its
standard-16 table on every quantize (15.6 KB for one color — hoist to a
static), and `parseSpans` is linear at ~1 ms / 1k chars, which is the
measured case for the planned debounce + size cap + incremental reparse
(a 1M-char buffer pays ~1.1 s per keystroke today).

### After — 2026-06-11 perf round

Same machine, after the incremental line-cache splice, append-only add
buffer, palette hoist, and highlight debounce/size-cap landed:

| Bench                       | Baseline            | After             | Change      |
| --------------------------- | ------------------- | ----------------- | ----------- |
| BufferTyping 256            | 45,214 us / 166 MB  | 3,531 us / 6.1 MB | 12.8x / 27x |
| BufferTyping 1024           | 182,280 us / 684 MB | 14,401 us / 28 MB | 12.7x / 25x |
| PieceTableTyping 1024       | 128.5 us / 1.4 MB   | 81.8 us / 410 KB  | 1.6x / 3.5x |
| QuantizeAccent              | 2.6 us / 15.6 KB    | 175 ns / 48 B     | 15x / 332x  |
| Per keystroke at 100k chars | ~178 us / ~667 KB   | ~14 us / ~24 KB   | 13x / 28x   |

The remaining BufferTyping cost is the O(lines) array splice plus
`positionToIndex`'s line walk — both flatten further with a line-offset
index if a future measurement justifies it. ToStringWhole at 10k pays
~7 us more (per-piece lock on the shared add buffer) — the accepted
price for thread-safe reads from effect tasks; at 100k it is faster
than baseline. Highlight parses are unchanged per parse but now
debounced 75 ms (a burst schedules many, only the newest parses),
capped at `Highlight.maxParseChars` (2M chars), and grammar-less
buffers no longer pay a full document materialization per keystroke.
