module Fedit.Benchmarks.Manual

open System
open System.Diagnostics

let header () =
    printfn "%-46s %9s %9s %9s %9s %11s" "scenario" "mean ms" "p50 ms" "p95 ms" "max ms" "KB/op"
    printfn "%s" (String.replicate 98 "-")

/// Print one stats row from raw per-iteration timings (milliseconds) and the
/// total bytes allocated across all iterations.
let report (label: string) (timings: float array) (allocatedBytes: int64) =
    let sorted = Array.sort timings
    let n = sorted.Length
    let mean = Array.average sorted
    let p50 = sorted[n / 2]
    let p95 = sorted[min (n - 1) (n * 95 / 100)]
    let kbPerOp = float allocatedBytes / float n / 1024.0
    printfn "%-46s %9.3f %9.3f %9.3f %9.3f %11.1f" label mean p50 p95 sorted[n - 1] kbPerOp

/// Warmup, settle the GC, then time `iterations` calls of `action`
/// individually. No console output inside the timed region.
let measure (label: string) (warmup: int) (iterations: int) (action: unit -> unit) =
    for _ in 1..warmup do
        action ()

    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()

    let timings = Array.zeroCreate iterations
    let allocBefore = GC.GetAllocatedBytesForCurrentThread()
    let sw = Stopwatch()

    for i in 0 .. iterations - 1 do
        sw.Restart()
        action ()
        sw.Stop()
        timings[i] <- sw.Elapsed.TotalMilliseconds

    report label timings (GC.GetAllocatedBytesForCurrentThread() - allocBefore)
