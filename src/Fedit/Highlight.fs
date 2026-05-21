namespace Fedit

open System
open System.IO
open System.Reflection
open System.Collections.Concurrent

/// One per token-class we paint. `tree-sitter-fsharp` emits capture
/// names like `keyword`, `keyword.control`, `string.special.path`,
/// `function.call`, `variable.parameter`, etc.; we collapse them into
/// this short DU and map onto themed colors via `Highlight.colorFor`.
type HighlightCapture =
    | Keyword
    | KeywordControl
    | KeywordOperator
    | String
    | StringSpecial
    | Number
    | Comment
    | Function
    | FunctionCall
    | Type
    | Constructor
    | Variable
    | Parameter
    | Operator
    | Punctuation
    | Attribute

/// A byte-range painted in a single capture class. `StartByte` and
/// `EndByte` are .NET char indices (TreeSitter.DotNet treats source as
/// UTF-16 and exposes `Node.StartIndex` / `EndIndex` already divided
/// back to char positions).
type HighlightSpan =
    { Capture: HighlightCapture
      StartByte: int
      EndByte: int }

/// Per-buffer parse state. The parser is owned (one per buffer); the
/// tree is owned and re-allocated on every Phase 1 reparse.
type HighlightState =
    { Language: string
      Parser: TreeSitter.Parser
      Tree: TreeSitter.Tree option
      Spans: HighlightSpan array }

/// Process-wide singleton: one `Language` + one compiled `Query` per
/// supported language name. Parsers are per-buffer, not in here.
type HighlightRegistry
    private
    (
        languages: ConcurrentDictionary<string, TreeSitter.Language>,
        queries: ConcurrentDictionary<string, TreeSitter.Query>
    ) =

    member _.TryGetLanguage(name: string) : TreeSitter.Language option =
        match languages.TryGetValue name with
        | true, value -> Some value
        | _ -> None

    member _.TryGetQuery(name: string) : TreeSitter.Query option =
        match queries.TryGetValue name with
        | true, value -> Some value
        | _ -> None

    interface IDisposable with
        member _.Dispose() =
            for q in queries.Values do
                try
                    (q :> IDisposable).Dispose()
                with _ ->
                    ()

            queries.Clear()
            // Language wrappers don't own the loaded dylib; OS reclaims on exit.
            languages.Clear()

    static member tryCreate() : HighlightRegistry option =
        let languages = ConcurrentDictionary<string, TreeSitter.Language>()
        let queries = ConcurrentDictionary<string, TreeSitter.Query>()

        try
            // The two-arg constructor takes (library, function): the loader
            // resolves `tree-sitter-fsharp.{dylib|so|dll}` under
            // `AppContext.BaseDirectory/runtimes/<rid>/native/` and looks up
            // the `tree_sitter_fsharp` symbol.
            let lang = new TreeSitter.Language("tree-sitter-fsharp", "tree_sitter_fsharp")
            languages.["fsharp"] <- lang

            let asm = Assembly.GetExecutingAssembly()

            match asm.GetManifestResourceStream("fedit.queries.fsharp.highlights.scm") with
            | null -> None
            | stream ->
                use reader = new StreamReader(stream)
                let scm = reader.ReadToEnd()
                let q = new TreeSitter.Query(lang, scm)
                queries.["fsharp"] <- q
                Some(new HighlightRegistry(languages, queries))
        with _ ->
            None

[<RequireQualifiedAccess>]
module Highlight =

    /// Map a tree-sitter capture name onto our `HighlightCapture` DU.
    /// Resolution is longest-prefix-first against the standard
    /// `category.subcategory` naming convention. Unknown captures
    /// return `None` (no styling).
    let resolveCapture (captureName: string) : HighlightCapture option =
        if String.IsNullOrEmpty captureName then
            None
        else
            let startsDot (prefix: string) =
                captureName.StartsWith(prefix + ".", StringComparison.Ordinal)

            match captureName with
            | "keyword.control" -> Some KeywordControl
            | s when s.StartsWith("keyword.control.", StringComparison.Ordinal) -> Some KeywordControl
            | "keyword.operator" -> Some KeywordOperator
            | s when s.StartsWith("keyword.operator.", StringComparison.Ordinal) -> Some KeywordOperator
            | "keyword" -> Some Keyword
            | s when startsDot "keyword" -> Some Keyword
            | "string.special" -> Some StringSpecial
            | s when s.StartsWith("string.special.", StringComparison.Ordinal) -> Some StringSpecial
            | "string" -> Some String
            | s when startsDot "string" -> Some String
            | "function.call" -> Some FunctionCall
            | s when s.StartsWith("function.call.", StringComparison.Ordinal) -> Some FunctionCall
            | "function" -> Some Function
            | s when startsDot "function" -> Some Function
            | "type" -> Some Type
            | s when startsDot "type" -> Some Type
            | "constructor" -> Some Constructor
            | s when startsDot "constructor" -> Some Constructor
            | "variable.parameter" -> Some Parameter
            | s when s.StartsWith("variable.parameter.", StringComparison.Ordinal) -> Some Parameter
            | "variable" -> Some Variable
            | s when startsDot "variable" -> Some Variable
            | "number" -> Some Number
            | s when startsDot "number" -> Some Number
            | "comment" -> Some Comment
            | s when startsDot "comment" -> Some Comment
            | "operator" -> Some Operator
            | s when startsDot "operator" -> Some Operator
            | "punctuation" -> Some Punctuation
            | s when startsDot "punctuation" -> Some Punctuation
            | "attribute" -> Some Attribute
            | s when startsDot "attribute" -> Some Attribute
            | _ -> None

    /// Walk every capture in `tree`, project into our DU, return a
    /// `StartByte`-sorted array.
    let computeSpans (query: TreeSitter.Query) (tree: TreeSitter.Tree) : HighlightSpan array =
        let result = ResizeArray<HighlightSpan>()
        use cursor = query.Execute(tree.RootNode)

        for capture in cursor.Captures do
            match resolveCapture capture.Name with
            | Some c ->
                let node = capture.Node

                result.Add
                    { Capture = c
                      StartByte = node.StartIndex
                      EndByte = node.EndIndex }
            | None -> ()

        result.Sort(fun a b -> compare a.StartByte b.StartByte)
        result.ToArray()

    let detectLanguage (path: string option) : string option =
        path
        |> Option.bind (fun p ->
            let ext =
                match Path.GetExtension p with
                | null -> ""
                | s -> s.ToLowerInvariant()

            match ext with
            | ".fs"
            | ".fsi"
            | ".fsx" -> Some "fsharp"
            | _ -> None)

    let dispose (state: HighlightState) =
        state.Tree
        |> Option.iter (fun t ->
            try
                (t :> IDisposable).Dispose()
            with _ ->
                ())

        try
            (state.Parser :> IDisposable).Dispose()
        with _ ->
            ()

    /// Build a fresh `HighlightState`. If `previous` is `Some`, dispose
    /// its tree + parser first. Returns `None` when the language isn't
    /// available in the registry or the parser failed to allocate.
    let parse
        (registry: HighlightRegistry)
        (language: string)
        (source: string)
        (previous: HighlightState option)
        : HighlightState option =
        previous |> Option.iter dispose

        match registry.TryGetLanguage language, registry.TryGetQuery language with
        | Some lang, Some query ->
            try
                let parser = new TreeSitter.Parser(lang)

                match parser.Parse source with
                | null ->
                    Some
                        { Language = language
                          Parser = parser
                          Tree = None
                          Spans = Array.empty }
                | tree ->
                    Some
                        { Language = language
                          Parser = parser
                          Tree = Some tree
                          Spans = computeSpans query tree }
            with _ ->
                None
        | _ -> None

    /// Binary search the sorted `spans` for the one containing
    /// `charIndex`. First hit wins on nested captures.
    let spanAt (spans: HighlightSpan array) (charIndex: int) : HighlightSpan option =
        if spans.Length = 0 then
            None
        else
            let mutable lo = 0
            let mutable hi = spans.Length - 1
            let mutable found = None

            while lo <= hi && found.IsNone do
                let mid = (lo + hi) / 2
                let span = spans.[mid]

                if charIndex < span.StartByte then hi <- mid - 1
                elif charIndex >= span.EndByte then lo <- mid + 1
                else found <- Some span

            found

    /// Theme lookup for a capture. Defined here because Themes.fs
    /// has already been compiled by the time we hit this file.
    let colorFor (theme: Theme) (capture: HighlightCapture) : Color =
        match capture with
        | Keyword -> theme.SyntaxKeyword
        | KeywordControl -> theme.SyntaxKeywordControl
        | KeywordOperator -> theme.SyntaxKeywordOperator
        | String -> theme.SyntaxString
        | StringSpecial -> theme.SyntaxStringSpecial
        | Number -> theme.SyntaxNumber
        | Comment -> theme.SyntaxComment
        | Function -> theme.SyntaxFunction
        | FunctionCall -> theme.SyntaxFunctionCall
        | Type -> theme.SyntaxType
        | Constructor -> theme.SyntaxConstructor
        | Variable -> theme.SyntaxVariable
        | Parameter -> theme.SyntaxParameter
        | Operator -> theme.SyntaxOperator
        | Punctuation -> theme.SyntaxPunctuation
        | Attribute -> theme.SyntaxAttribute
