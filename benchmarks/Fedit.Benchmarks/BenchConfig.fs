module Fedit.Benchmarks.BenchConfig

open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Toolchains.InProcess.Emit

/// ShortRun (1 launch, 3 warmup, 3 measured iterations) keeps the full micro
/// suite around four minutes. In-process so the pinned SDK, central package
/// versions, TreatWarningsAsErrors, and native grammar sidecars never have to
/// survive BenchmarkDotNet's generated-child-project round trip. Numbers are
/// for relative before/after comparison on one machine, not absolutes.
let instance =
    ManualConfig.Create(DefaultConfig.Instance).AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance))
