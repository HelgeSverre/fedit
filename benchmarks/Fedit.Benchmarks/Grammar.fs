module Fedit.Benchmarks.Grammar

open Fedit

/// Process-wide registry, loaded once and kept for the process lifetime.
/// `None` when no native grammars resolve at all. The fsharp grammar needs
/// the locally-built sidecar (`just build-grammars`); the TreeSitter.DotNet
/// bundled grammars (javascript, json, ...) normally load regardless.
let registry: HighlightRegistry option = HighlightRegistry.tryCreate ()

/// Preferred grammar for the F#-flavoured corpus, falling back to any loaded
/// grammar so the harness still measures real parser work on a fresh clone.
let pickLanguage () : string option =
    registry
    |> Option.bind (fun reg ->
        [ "fsharp"; "javascript"; "json" ]
        |> List.tryFind (fun id -> (reg.TryGetLanguage id).IsSome && (reg.TryGetQuery id).IsSome))

let parse (language: string) (source: string) : HighlightSpan array option =
    registry |> Option.bind (fun reg -> Highlight.parseSpans reg language source)
