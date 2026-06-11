module Fedit.Benchmarks.Program

open System.Diagnostics
open BenchmarkDotNet.Running

/// The manual harness has no equivalent of BDN's optimization validator;
/// warn loudly if Fedit.dll was built without JIT optimizations.
let private warnIfDebug () =
    let asm = typeof<Fedit.Model>.Assembly

    let deoptimized =
        asm.GetCustomAttributes(typeof<DebuggableAttribute>, false)
        |> Array.exists (fun a -> (a :?> DebuggableAttribute).IsJITOptimizerDisabled)

    if deoptimized then
        eprintfn "WARNING: Fedit built without optimizations — numbers are meaningless. Run via 'just bench-manual'."

[<EntryPoint>]
let main argv =
    match Array.tryHead argv with
    | Some "manual" ->
        warnIfDebug ()
        let scope = argv |> Array.tryItem 1 |> Option.defaultValue "all"

        if scope = "all" || scope = "frames" then
            FrameBench.run ()

        if scope = "all" || scope = "parse" then
            ParseBench.run ()

        0
    | _ ->
        BenchmarkSwitcher
            .FromTypes(
                [| typeof<PieceTableBenchmarks>
                   typeof<EditSessionBenchmarks>
                   typeof<BufferBenchmarks>
                   typeof<HighlightLookupBenchmarks>
                   typeof<ColorBenchmarks>
                   typeof<RendererBenchmarks> |]
            )
            .Run(argv, BenchConfig.instance)
        |> ignore

        0
