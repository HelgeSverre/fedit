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
