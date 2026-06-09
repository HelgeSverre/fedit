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
        let asm = Assembly.GetExecutingAssembly()
        let mutable anyLoaded = false

        let tryLoadBundled (id: string) (resourceName: string) =
            try
                let lang = new TreeSitter.Language(id)
                languages.[id] <- lang

                match asm.GetManifestResourceStream(resourceName) with
                | null -> ()
                | stream ->
                    use reader = new StreamReader(stream)
                    let scm = reader.ReadToEnd()
                    queries.[id] <- new TreeSitter.Query(lang, scm)
                    anyLoaded <- true
            with _ ->
                ()

        // F# uses the explicit (library, function) constructor because it is
        // not bundled inside TreeSitter.DotNet — we ship our own cross-built
        // native under runtimes/<rid>/native/.
        try
            let fsLang = new TreeSitter.Language("tree-sitter-fsharp", "tree_sitter_fsharp")

            languages.["fsharp"] <- fsLang

            match asm.GetManifestResourceStream("fedit.queries.fsharp.highlights.scm") with
            | null -> ()
            | stream ->
                use reader = new StreamReader(stream)
                queries.["fsharp"] <- new TreeSitter.Query(fsLang, reader.ReadToEnd())
                anyLoaded <- true
        with _ ->
            ()

        // Bundled grammars — simple string-id constructor.
        [ "javascript"
          "typescript"
          "tsx"
          "python"
          "json"
          "c-sharp"
          "go"
          "rust"
          "html"
          "css"
          "c"
          "php"
          "bash" ]
        |> List.iter (fun id -> tryLoadBundled id $"fedit.queries.{id}.highlights.scm")

        // External grammars — vendored natives under runtimes/<rid>/native/.
        let tryLoadExternal (id: string) (libName: string) (funcName: string) (resourceName: string) =
            try
                let lang = new TreeSitter.Language(libName, funcName)
                languages.[id] <- lang

                match asm.GetManifestResourceStream(resourceName) with
                | null -> ()
                | stream ->
                    use reader = new StreamReader(stream)
                    let scm = reader.ReadToEnd()
                    queries.[id] <- new TreeSitter.Query(lang, scm)
                    anyLoaded <- true
            with _ ->
                ()

        [ "markdown", "tree-sitter-markdown", "tree_sitter_markdown"
          "xml", "tree-sitter-xml", "tree_sitter_xml"
          "dart", "tree-sitter-dart", "tree_sitter_dart"
          "just", "tree-sitter-just", "tree_sitter_just"
          "make", "tree-sitter-make", "tree_sitter_make"
          "astro", "tree-sitter-astro", "tree_sitter_astro"
          "toml", "tree-sitter-toml", "tree_sitter_toml"
          "sema", "tree-sitter-sema", "tree_sitter_sema"
          "applescript", "tree-sitter-applescript", "tree_sitter_applescript"
          "rescript", "tree-sitter-rescript", "tree_sitter_rescript"
          "zig", "tree-sitter-zig", "tree_sitter_zig" ]
        |> List.iter (fun (id, lib, func) -> tryLoadExternal id lib func $"fedit.queries.{id}.highlights.scm")

        if anyLoaded then
            Some(new HighlightRegistry(languages, queries))
        else
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
            | _ when startsDot "keyword" -> Some Keyword
            | "string.special" -> Some StringSpecial
            | s when s.StartsWith("string.special.", StringComparison.Ordinal) -> Some StringSpecial
            | "string" -> Some String
            | _ when startsDot "string" -> Some String
            | "function.call" -> Some FunctionCall
            | s when s.StartsWith("function.call.", StringComparison.Ordinal) -> Some FunctionCall
            | "function" -> Some Function
            | _ when startsDot "function" -> Some Function
            | "type" -> Some Type
            | _ when startsDot "type" -> Some Type
            | "constructor" -> Some Constructor
            | _ when startsDot "constructor" -> Some Constructor
            | "variable.parameter" -> Some Parameter
            | s when s.StartsWith("variable.parameter.", StringComparison.Ordinal) -> Some Parameter
            | "variable" -> Some Variable
            | _ when startsDot "variable" -> Some Variable
            | "number" -> Some Number
            | _ when startsDot "number" -> Some Number
            | "comment" -> Some Comment
            | _ when startsDot "comment" -> Some Comment
            | "operator" -> Some Operator
            | _ when startsDot "operator" -> Some Operator
            | "punctuation" -> Some Punctuation
            | _ when startsDot "punctuation" -> Some Punctuation
            | "attribute" -> Some Attribute
            | _ when startsDot "attribute" -> Some Attribute
            // Constant / boolean — treated as types (distinct values, no mutation)
            | "constant" -> Some Type
            | _ when startsDot "constant" -> Some Type
            | "boolean" -> Some Keyword
            | _ when startsDot "boolean" -> Some Keyword
            // Tag names (XML/HTML/JSX) — treated as types
            | "tag" -> Some Type
            | _ when startsDot "tag" -> Some Type
            // Properties — treated as attributes
            | "property" -> Some Attribute
            | _ when startsDot "property" -> Some Attribute
            // Module / namespace — treated as types
            | "module" -> Some Type
            | _ when startsDot "module" -> Some Type
            // Labels — treated as variables
            | "label" -> Some Variable
            | _ when startsDot "label" -> Some Variable
            // Character literals — treated as strings
            | "character" -> Some String
            | _ when startsDot "character" -> Some String
            // identifier.parameter (Dart / some grammars)
            | "identifier.parameter" -> Some Parameter
            | s when s.StartsWith("identifier.parameter.", StringComparison.Ordinal) -> Some Parameter
            | "identifier" -> Some Variable
            | _ when startsDot "identifier" -> Some Variable
            // Markup captures (new nvim-treesitter convention)
            | "markup.heading" -> Some Function
            | s when s.StartsWith("markup.heading.", StringComparison.Ordinal) -> Some Function
            | "markup.raw" -> Some StringSpecial
            | s when s.StartsWith("markup.raw.", StringComparison.Ordinal) -> Some StringSpecial
            | "markup.link" -> Some String
            | s when s.StartsWith("markup.link.", StringComparison.Ordinal) -> Some String
            | "markup" -> Some String
            | _ when startsDot "markup" -> Some String
            // Legacy nvim-treesitter text.* convention
            | "text.title" -> Some Function
            | s when s.StartsWith("text.title.", StringComparison.Ordinal) -> Some Function
            | "text.literal" -> Some StringSpecial
            | s when s.StartsWith("text.literal.", StringComparison.Ordinal) -> Some StringSpecial
            | "text.uri" -> Some StringSpecial
            | s when s.StartsWith("text.uri.", StringComparison.Ordinal) -> Some StringSpecial
            | "text" -> Some String
            | _ when startsDot "text" -> Some String
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

    /// Interpreter basenames that map onto the bundled `bash` grammar.
    /// tree-sitter-bash is the de-facto grammar for every POSIX-ish shell.
    let private shellInterpreters =
        set [ "sh"; "bash"; "dash"; "zsh"; "ksh"; "mksh"; "ash" ]

    /// Detect a shell script from its `#!` line when the path gives no
    /// hint (extensionless scripts). Resolves `/usr/bin/env <interp>` to
    /// the real interpreter and ignores flags / env-var assignments.
    let private detectShebang (source: string) : string option =
        let firstLine =
            match source.IndexOf '\n' with
            | -1 -> source
            | i -> source.Substring(0, i)

        let line = firstLine.Trim()

        if not (line.StartsWith("#!", StringComparison.Ordinal)) then
            None
        else
            let tokens =
                line.Substring(2).Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList

            match tokens with
            | [] -> None
            | first :: more ->
                let interp =
                    match Path.GetFileName first with
                    | "env" ->
                        // `/usr/bin/env [-S] [VAR=x] bash` — first plain token.
                        more
                        |> List.tryFind (fun t ->
                            not (t.StartsWith("-", StringComparison.Ordinal)) && not (t.Contains "="))
                        |> Option.map Path.GetFileName
                    | other -> Some other

                match interp with
                | Some name when Set.contains name shellInterpreters -> Some "bash"
                | _ -> None

    let detectLanguage (path: string option) (source: string) : string option =
        let byPath =
            path
            |> Option.bind (fun p ->
                let basename = Path.GetFileName p

                match basename with
                | "Justfile"
                | "justfile" -> Some "just"
                | "Makefile"
                | "makefile"
                | "GNUmakefile" -> Some "make"
                // Shell dotfiles / config files with no extension.
                | ".bashrc"
                | ".bash_profile"
                | ".bash_aliases"
                | ".bash_login"
                | ".bash_logout"
                | ".profile"
                | ".zshrc"
                | ".zshenv"
                | ".zprofile"
                | ".zlogin"
                | ".zlogout"
                | ".kshrc"
                | "PKGBUILD"
                | "APKBUILD" -> Some "bash"
                | _ ->
                    let ext =
                        match Path.GetExtension p with
                        | null -> ""
                        | s -> s.ToLowerInvariant()

                    match ext with
                    | ".fs"
                    | ".fsi"
                    | ".fsx" -> Some "fsharp"
                    | ".js"
                    | ".mjs"
                    | ".cjs" -> Some "javascript"
                    | ".ts" -> Some "typescript"
                    | ".tsx" -> Some "tsx"
                    | ".py" -> Some "python"
                    | ".json" -> Some "json"
                    | ".cs" -> Some "c-sharp"
                    | ".go" -> Some "go"
                    | ".rs" -> Some "rust"
                    | ".html"
                    | ".htm" -> Some "html"
                    | ".css" -> Some "css"
                    | ".c"
                    | ".h" -> Some "c"
                    | ".php"
                    | ".phtml" -> Some "php"
                    | ".sh"
                    | ".bash"
                    | ".zsh"
                    | ".ksh"
                    | ".command" -> Some "bash"
                    | ".md"
                    | ".mdx"
                    | ".markdown" -> Some "markdown"
                    | ".xml"
                    | ".svg"
                    | ".xsl"
                    | ".xslt" -> Some "xml"
                    | ".dart" -> Some "dart"
                    | ".just" -> Some "just"
                    | ".mk" -> Some "make"
                    | ".astro" -> Some "astro"
                    | ".toml" -> Some "toml"
                    | ".sema" -> Some "sema"
                    | ".applescript" -> Some "applescript"
                    | ".res"
                    | ".resi" -> Some "rescript"
                    | ".zig" -> Some "zig"
                    | _ -> None)

        match byPath with
        | Some _ -> byPath
        | None -> detectShebang source

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
