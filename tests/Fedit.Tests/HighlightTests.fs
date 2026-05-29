module Fedit.Tests.HighlightTests

open System.IO
open Xunit
open Fedit

[<Fact>]
let ``TreeSitter.DotNet types are reachable`` () =
    let parserType = typeof<TreeSitter.Parser>
    Assert.Equal("Parser", parserType.Name)

[<Theory>]
[<InlineData("keyword", "Keyword")>]
[<InlineData("keyword.control", "KeywordControl")>]
[<InlineData("keyword.operator", "KeywordOperator")>]
[<InlineData("keyword.something.else", "Keyword")>]
[<InlineData("string", "String")>]
[<InlineData("string.special", "StringSpecial")>]
[<InlineData("string.special.path", "StringSpecial")>]
[<InlineData("function.call", "FunctionCall")>]
[<InlineData("function", "Function")>]
[<InlineData("type.builtin", "Type")>]
[<InlineData("number", "Number")>]
[<InlineData("comment", "Comment")>]
[<InlineData("variable.parameter", "Parameter")>]
[<InlineData("variable", "Variable")>]
[<InlineData("operator", "Operator")>]
[<InlineData("punctuation.bracket", "Punctuation")>]
[<InlineData("attribute", "Attribute")>]
[<InlineData("constructor", "Constructor")>]
let ``resolveCapture maps tree-sitter capture names`` (input: string) (expectedCase: string) =
    match Highlight.resolveCapture input with
    | Some c -> Assert.Equal(expectedCase, string c)
    | None -> Assert.Fail($"expected Some {expectedCase}, got None")

[<Fact>]
let ``resolveCapture returns None for unknown capture`` () =
    Assert.Equal(None, Highlight.resolveCapture "not.a.real.capture")
    Assert.Equal(None, Highlight.resolveCapture "")

[<Fact>]
let ``detectLanguage maps F# extensions`` () =
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "foo.fs"))
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "Bar.FSI"))
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "script.fsx"))
    Assert.Equal(None, Highlight.detectLanguage None)

[<Theory>]
[<InlineData("foo.js", "javascript")>]
[<InlineData("foo.mjs", "javascript")>]
[<InlineData("foo.cjs", "javascript")>]
[<InlineData("bar.ts", "typescript")>]
[<InlineData("baz.py", "python")>]
[<InlineData("data.json", "json")>]
[<InlineData("Program.cs", "c-sharp")>]
[<InlineData("main.go", "go")>]
[<InlineData("lib.rs", "rust")>]
[<InlineData("index.html", "html")>]
[<InlineData("page.htm", "html")>]
[<InlineData("styles.css", "css")>]
[<InlineData("main.c", "c")>]
[<InlineData("header.h", "c")>]
[<InlineData("app.tsx", "tsx")>]
[<InlineData("index.php", "php")>]
[<InlineData("script.phtml", "php")>]
let ``detectLanguage maps bundled language extensions`` (path: string) (expected: string) =
    Assert.Equal(Some expected, Highlight.detectLanguage (Some path))

[<Theory>]
[<InlineData("readme.md", "markdown")>]
[<InlineData("doc.mdx", "markdown")>]
[<InlineData("notes.markdown", "markdown")>]
[<InlineData("schema.xml", "xml")>]
[<InlineData("icon.svg", "xml")>]
[<InlineData("transform.xsl", "xml")>]
[<InlineData("widget.dart", "dart")>]
[<InlineData("Justfile", "just")>]
[<InlineData("justfile", "just")>]
[<InlineData("tasks.just", "just")>]
[<InlineData("Makefile", "make")>]
[<InlineData("makefile", "make")>]
[<InlineData("GNUmakefile", "make")>]
[<InlineData("rules.mk", "make")>]
[<InlineData("index.astro", "astro")>]
let ``detectLanguage maps vendored language extensions`` (path: string) (expected: string) =
    Assert.Equal(Some expected, Highlight.detectLanguage (Some path))

[<Theory>]
[<InlineData("tsx")>]
[<InlineData("php")>]
[<InlineData("markdown")>]
[<InlineData("xml")>]
[<InlineData("dart")>]
[<InlineData("just")>]
[<InlineData("make")>]
[<InlineData("astro")>]
let ``registry loads language without throwing`` (lang: string) =
    let registry = HighlightRegistry.tryCreate ()
    Assert.True(registry.IsSome, "HighlightRegistry.tryCreate returned None")
    let reg = registry.Value
    Assert.True(reg.TryGetLanguage(lang).IsSome, $"language '{lang}' not loaded in registry")
    Assert.True(reg.TryGetQuery(lang).IsSome, $"query for '{lang}' not loaded in registry")

[<Theory>]
[<InlineData("tsx",      "const x: string = 'hi'")>]
[<InlineData("php",      "<?php $x = 42; // comment")>]
[<InlineData("markdown", "# Heading\n\nsome text")>]
[<InlineData("xml",      "<root attr=\"v\">text</root>")>]
[<InlineData("dart",     "void main() { print('hi'); }")>]
[<InlineData("just",     "build:\n    cargo build")>]
[<InlineData("make",     "all:\n\techo hi")>]
[<InlineData("astro",    "<h1>Hello</h1>")>]
let ``parse produces non-empty spans for new languages`` (lang: string) (src: string) =
    let registry = HighlightRegistry.tryCreate ()
    Assert.True(registry.IsSome, "registry missing")
    let state = Highlight.parse registry.Value lang src None
    Assert.True(state.IsSome, $"parse returned None for '{lang}'")
    Assert.True(state.Value.Spans.Length > 0, $"no spans produced for '{lang}'")

[<Fact>]
let ``spanAt returns covering span via binary search`` () =
    let spans: HighlightSpan array =
        [| { Capture = Keyword
             StartByte = 0
             EndByte = 6 }
           { Capture = String
             StartByte = 10
             EndByte = 17 }
           { Capture = Comment
             StartByte = 20
             EndByte = 30 } |]

    Assert.Equal(Some Keyword, Highlight.spanAt spans 3 |> Option.map (fun s -> s.Capture))
    Assert.Equal(Some String, Highlight.spanAt spans 10 |> Option.map (fun s -> s.Capture))
    Assert.Equal(Some String, Highlight.spanAt spans 16 |> Option.map (fun s -> s.Capture))
    Assert.Equal(None, Highlight.spanAt spans 7)
    Assert.Equal(None, Highlight.spanAt spans 30)
    Assert.Equal(None, Highlight.spanAt [||] 5)

[<Fact>]
let ``spanAt finds first/last/single elements`` () =
    let spans: HighlightSpan array =
        [| { Capture = Keyword
             StartByte = 0
             EndByte = 3 } |]

    Assert.Equal(Some Keyword, Highlight.spanAt spans 0 |> Option.map (fun s -> s.Capture))
    Assert.Equal(Some Keyword, Highlight.spanAt spans 2 |> Option.map (fun s -> s.Capture))
    Assert.Equal(None, Highlight.spanAt spans 3)

[<Fact>]
let ``colorFor maps captures to theme syntax fields`` () =
    let t = Themes.defaultTheme
    Assert.Equal(t.SyntaxKeyword, Highlight.colorFor t Keyword)
    Assert.Equal(t.SyntaxString, Highlight.colorFor t String)
    Assert.Equal(t.SyntaxComment, Highlight.colorFor t Comment)
    Assert.Equal(t.SyntaxNumber, Highlight.colorFor t Number)
    Assert.Equal(t.SyntaxAttribute, Highlight.colorFor t Attribute)

[<Fact>]
[<Trait("Category", "Smoke")>]
let ``HighlightRegistry loads F# language and query`` () =
    match HighlightRegistry.tryCreate () with
    | None -> Assert.Fail("registry failed to create — F# grammar likely missing from runtimes/")
    | Some registry ->
        use r = registry
        Assert.True((r.TryGetLanguage "fsharp").IsSome, "language fsharp not loaded")
        Assert.True((r.TryGetQuery "fsharp").IsSome, "query for fsharp not built")

[<Fact>]
[<Trait("Category", "Smoke")>]
let ``HighlightRegistry loads bundled languages`` () =
    match HighlightRegistry.tryCreate () with
    | None -> Assert.Fail("registry failed to create")
    | Some registry ->
        use r = registry
        // Verify at least a few bundled grammars loaded successfully.
        // Not every grammar is guaranteed on every platform (e.g. win-x64
        // may lag), so we assert on the ones most likely to be present.
        Assert.True((r.TryGetLanguage "javascript").IsSome, "javascript not loaded")
        Assert.True((r.TryGetQuery "javascript").IsSome, "javascript query not built")
        Assert.True((r.TryGetLanguage "python").IsSome, "python not loaded")
        Assert.True((r.TryGetQuery "python").IsSome, "python query not built")
        Assert.True((r.TryGetLanguage "json").IsSome, "json not loaded")
        Assert.True((r.TryGetQuery "json").IsSome, "json query not built")

[<Fact>]
[<Trait("Category", "Smoke")>]
let ``computeSpans returns keyword + string spans for sample.fs`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let lang = registry.TryGetLanguage "fsharp" |> Option.get
    let query = registry.TryGetQuery "fsharp" |> Option.get
    let source = File.ReadAllText "Fixtures/sample.fs"
    use parser = new TreeSitter.Parser(lang)

    match parser.Parse source with
    | null -> Assert.Fail "parser returned null"
    | tree ->
        use _ = tree
        let spans = Highlight.computeSpans query tree
        Assert.NotEmpty spans
        Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Keyword)
        Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = String)

[<Fact>]
let ``parse builds HighlightState from F# source`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let source = "let x = 42\nlet name = \"hello\""

    match Highlight.parse registry "fsharp" source None with
    | None -> Assert.Fail "parse returned None"
    | Some state ->
        Assert.Equal("fsharp", state.Language)
        Assert.True(state.Tree.IsSome, "tree should be present")
        Assert.Contains(state.Spans, fun s -> s.Capture = Keyword)
        Assert.Contains(state.Spans, fun s -> s.Capture = String)
        Highlight.dispose state
