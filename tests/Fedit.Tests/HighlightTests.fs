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
    Assert.Equal(None, Highlight.detectLanguage (Some "readme.md"))
    Assert.Equal(None, Highlight.detectLanguage None)

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
