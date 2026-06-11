module Fedit.Benchmarks.ParseBench

/// Native tree-sitter parse cost per document size — exactly the work the
/// `ParseHighlight` effect interpreter does on every edit tick today
/// (finding D1). Manual harness rather than BDN: the grammar natives resolve
/// next to the running binary and BDN's generated child project does not
/// carry them; here a missing grammar is reported instead of silently
/// measuring nothing.
let run () =
    printfn ""
    printfn "── Highlight.parseSpans (tree-sitter, one-shot parse + span projection) ──"

    match Grammar.pickLanguage () with
    | None -> printfn "skipped: no grammars loaded (run 'just build-grammars' first)."
    | Some lang ->
        if lang <> "fsharp" then
            printfn "note: fsharp grammar missing — using '%s' instead (run 'just build-grammars')." lang

        Manual.header ()

        for size, iterations in [ 10_000, 100; 100_000, 25; 1_000_000, 5 ] do
            let source = Corpus.generate size

            Manual.measure $"parseSpans {lang} %d{size} chars" 2 iterations (fun () ->
                Grammar.parse lang source |> ignore)
