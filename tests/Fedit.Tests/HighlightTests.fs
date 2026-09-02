module Fedit.Tests.HighlightTests

open System.IO
open Xunit
open FsUnit.Xunit
open Fedit

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
let ``maxParseChars bounds worst-case parse time`` () =
    // ~1 ms per 1k chars measured: the cap must stay in the low-seconds
    // range so an oversized buffer never schedules a multi-second parse.
    // Cap-enforcement itself is exercised at the chokepoint in UpdateTests.
    Assert.InRange(Highlight.maxParseChars, 100_000, 10_000_000)

[<Fact>]
let ``detectLanguage maps F# extensions`` () =
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "foo.fs") "")
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "Bar.FSI") "")
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "script.fsx") "")
    Assert.Equal(None, Highlight.detectLanguage None "")

[<Theory>]
[<InlineData("foo.js", "javascript")>]
[<InlineData("foo.mjs", "javascript")>]
[<InlineData("foo.cjs", "javascript")>]
[<InlineData("bar.ts", "typescript")>]
[<InlineData("baz.py", "python")>]
[<InlineData("stub.pyi", "python")>]
[<InlineData("gui.pyw", "python")>]
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
[<InlineData("deploy.sh", "bash")>]
[<InlineData("lib.bash", "bash")>]
[<InlineData("run.zsh", "bash")>]
[<InlineData("setup.ksh", "bash")>]
[<InlineData("Open.command", "bash")>]
let ``detectLanguage maps bundled language extensions`` (path: string) (expected: string) =
    Assert.Equal(Some expected, Highlight.detectLanguage (Some path) "")

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
[<InlineData("Cargo.toml", "toml")>]
[<InlineData("Pkg.toml", "toml")>]
[<InlineData("config.toml", "toml")>]
[<InlineData("script.sema", "sema")>]
[<InlineData("main.sema", "sema")>]
[<InlineData("script.applescript", "applescript")>]
[<InlineData("component.res", "rescript")>]
[<InlineData("interface.resi", "rescript")>]
[<InlineData("main.zig", "zig")>]
[<InlineData("build.zig", "zig")>]
let ``detectLanguage maps vendored language extensions`` (path: string) (expected: string) =
    Assert.Equal(Some expected, Highlight.detectLanguage (Some path) "")

[<Theory>]
[<InlineData(".bashrc")>]
[<InlineData(".bash_profile")>]
[<InlineData(".bash_aliases")>]
[<InlineData(".profile")>]
[<InlineData(".zshrc")>]
[<InlineData(".zshenv")>]
[<InlineData(".kshrc")>]
[<InlineData("PKGBUILD")>]
[<InlineData("APKBUILD")>]
let ``detectLanguage maps shell config filenames`` (path: string) =
    Assert.Equal(Some "bash", Highlight.detectLanguage (Some path) "")

[<Fact>]
let ``detectLanguage detects shell scripts by shebang`` () =
    // Extensionless scripts identified only by their #! line.
    Assert.Equal(Some "bash", Highlight.detectLanguage (Some "deploy") "#!/bin/bash\necho hi")
    Assert.Equal(Some "bash", Highlight.detectLanguage (Some "configure") "#!/bin/sh\nset -e")
    Assert.Equal(Some "bash", Highlight.detectLanguage (Some "run") "#!/usr/bin/env bash\n")
    Assert.Equal(Some "bash", Highlight.detectLanguage None "#!/usr/bin/env zsh\n")
    Assert.Equal(Some "bash", Highlight.detectLanguage (Some "task") "#! /bin/bash -eu\n")
    // Shebangs we have no grammar for, and plain text, stay unresolved.
    Assert.Equal(None, Highlight.detectLanguage (Some "main") "#!/usr/bin/node\n")
    Assert.Equal(None, Highlight.detectLanguage (Some "notes") "just some text\n")

[<Fact>]
let ``detectLanguage detects python scripts by shebang`` () =
    Assert.Equal(Some "python", Highlight.detectLanguage (Some "app") "#!/usr/bin/env python\nprint(1)")
    Assert.Equal(Some "python", Highlight.detectLanguage (Some "tool") "#!/usr/bin/env python3\n")
    Assert.Equal(Some "python", Highlight.detectLanguage (Some "legacy") "#!/usr/bin/python2\n")
    Assert.Equal(Some "python", Highlight.detectLanguage (Some "pinned") "#!/usr/bin/env python3.12\n")
    Assert.Equal(Some "python", Highlight.detectLanguage (Some "flagged") "#! /usr/bin/python3 -u\n")
    // The version suffix is digits-and-dots only — a longer name is a different
    // program, not a python interpreter.
    Assert.Equal(None, Highlight.detectLanguage (Some "app") "#!/usr/bin/env pythonista\n")

[<Theory>]
[<InlineData("tsx")>]
[<InlineData("php")>]
[<InlineData("markdown")>]
[<InlineData("xml")>]
[<InlineData("dart")>]
[<InlineData("just")>]
[<InlineData("make")>]
[<InlineData("astro")>]
[<InlineData("toml")>]
[<InlineData("sema")>]
[<InlineData("bash")>]
[<InlineData("applescript")>]
[<InlineData("rescript")>]
[<InlineData("zig")>]
let ``registry loads language without throwing`` (lang: string) =
    let registry = HighlightRegistry.tryCreate ()
    Assert.True(registry.IsSome, "HighlightRegistry.tryCreate returned None")
    let reg = registry.Value
    Assert.True(reg.TryGetLanguage(lang).IsSome, $"language '{lang}' not loaded in registry")
    Assert.True(reg.TryGetQuery(lang).IsSome, $"query for '{lang}' not loaded in registry")

[<Theory>]
[<InlineData("tsx", "const x: string = 'hi'")>]
[<InlineData("php", "<?php $x = 42; // comment")>]
[<InlineData("markdown", "# Heading\n\nsome text")>]
[<InlineData("xml", "<root attr=\"v\">text</root>")>]
[<InlineData("dart", "void main() { print('hi'); }")>]
[<InlineData("just", "build:\n    cargo build")>]
[<InlineData("make", "all:\n\techo hi")>]
[<InlineData("astro", "<h1>Hello</h1>")>]
[<InlineData("toml", "title = \"x\"\n[owner]\nname = \"y\"")>]
[<InlineData("sema", "(define x \"hello\") ; note")>]
[<InlineData("bash", "#!/bin/bash\nif [ -f foo ]; then echo \"hi $HOME\"; fi")>]
[<InlineData("applescript", "(* hello *)\ntell application \"Finder\"\n  set x to 42\nend tell")>]
[<InlineData("rescript", "let name = \"fedit\"\nlet answer = 42")>]
[<InlineData("zig", "const std = @import(\"std\");\npub fn main() void {\n    std.debug.print(\"hi\", .{});\n}")>]
let ``parseSpans produces non-empty spans for new languages`` (lang: string) (src: string) =
    let registry = HighlightRegistry.tryCreate ()
    Assert.True(registry.IsSome, "registry missing")
    let spans = Highlight.parseSpans registry.Value lang src
    Assert.True(spans.IsSome, $"parseSpans returned None for '{lang}'")
    Assert.True(spans.Value.Length > 0, $"no spans produced for '{lang}'")

[<Fact>]
[<Trait("Category", "Smoke")>]
let ``parseSpans yields keyword, string, and comment captures for bash`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let source = "#!/bin/bash\n# deploy\nif [ -f foo ]; then\n  echo \"hi $HOME\"\nfi"

    match Highlight.parseSpans registry "bash" source with
    | None -> Assert.Fail "parseSpans returned None — bash query likely failed to compile"
    | Some spans ->
        Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Keyword)
        Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = String)
        Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Comment)

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
let ``parseSpans projects spans from F# source`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let source = "let x = 42\nlet name = \"hello\""

    match Highlight.parseSpans registry "fsharp" source with
    | None -> Assert.Fail "parseSpans returned None"
    | Some spans ->
        Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Keyword)
        Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = String)

[<Fact>]
let ``parseSpans reports UTF-16 char offsets after an astral-plane char`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    // "🚀" is one codepoint but two UTF-16 chars (and four UTF-8 bytes).
    // Span offsets are .NET char indices — TreeSitter.DotNet parses UTF-16
    // and divides byte offsets by two — so the literal after the emoji must
    // land exactly where the .NET string says it does.
    let source = "let a = \"🚀\"\nlet b = \"plain\""
    let literal = "\"plain\""
    let expectedStart = source.IndexOf(literal, System.StringComparison.Ordinal)
    let expectedEnd = expectedStart + literal.Length

    match Highlight.parseSpans registry "fsharp" source with
    | None -> Assert.Fail "parseSpans returned None"
    | Some spans ->
        let covering =
            spans
            |> Array.filter (fun s -> s.Capture = String)
            |> Array.tryFind (fun s -> s.StartByte < expectedEnd && s.EndByte > expectedStart)

        match covering with
        | None -> Assert.Fail "no String span overlaps the second string literal"
        | Some span ->
            Assert.Equal(expectedStart, span.StartByte)
            Assert.Equal(expectedEnd, span.EndByte)

[<Fact>]
[<Trait("Category", "Smoke")>]
let ``selectionLadder climbs strictly nesting ranges from a caret to the root`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let source = "let value = 42"
    // A zero-width caret inside the "value" identifier (chars 4..9).
    let caret = 5

    match Highlight.selectionLadder registry "fsharp" source caret caret with
    | None -> Assert.Fail "selectionLadder returned None"
    | Some ranges ->
        Assert.True(ranges.Length >= 2, "expected at least an inner token and the root")

        // Every step contains the caret.
        for (s, e) in ranges do
            Assert.True(s <= caret && caret <= e, $"range {s}..{e} does not contain caret {caret}")

        // Innermost→outermost: each step strictly contains the previous one.
        for i in 0 .. ranges.Length - 2 do
            let (s, e) = ranges[i]
            let (s2, e2) = ranges[i + 1]

            Assert.True(
                s2 <= s && e <= e2 && (s2 < s || e2 > e),
                $"step {i} ({s}..{e}) is not strictly inside ({s2}..{e2})"
            )

        // The innermost step is a non-empty token within the identifier.
        let (s0, e0) = ranges[0]
        Assert.True(e0 > s0, "innermost range is empty")
        Assert.True(s0 >= 4 && e0 <= 9, $"innermost {s0}..{e0} is not within the identifier")

        // The outermost step is the whole document (the tree root).
        Assert.Equal((0, source.Length), ranges[ranges.Length - 1])

[<Fact>]
[<Trait("Category", "Smoke")>]
let ``selectionLadder over the whole document yields only the root`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let source = "let x = 1"

    match Highlight.selectionLadder registry "fsharp" source 0 source.Length with
    | None -> Assert.Fail "selectionLadder returned None"
    | Some ranges ->
        // Nothing encloses the full document but the root and any same-span
        // wrappers, which collapse to a single step.
        Assert.NotEmpty ranges
        Assert.All(ranges, fun r -> Assert.Equal((0, source.Length), r))

[<Fact>]
let ``selectionLadder climbs a bundled grammar (python) from a caret to the root`` () =
    // Python is bundled in TreeSitter.DotNet, so this exercises the ladder
    // end-to-end wherever the tests run — not only where the external F#
    // grammar is built.
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let source = "def foo():\n    return 42"
    let caret = source.IndexOf("foo", System.StringComparison.Ordinal) + 1

    match Highlight.selectionLadder registry "python" source caret caret with
    | None -> Assert.Fail "selectionLadder returned None (python grammar missing?)"
    | Some ranges ->
        Assert.True(ranges.Length >= 2, "expected an inner token and the root")

        for (s, e) in ranges do
            Assert.True(s <= caret && caret <= e, $"range {s}..{e} does not contain caret {caret}")

        for i in 0 .. ranges.Length - 2 do
            let (s, e) = ranges[i]
            let (s2, e2) = ranges[i + 1]

            Assert.True(
                s2 <= s && e <= e2 && (s2 < s || e2 > e),
                $"step {i} ({s}..{e}) is not strictly inside ({s2}..{e2})"
            )

        Assert.Equal((0, source.Length), ranges[ranges.Length - 1])

[<Fact>]
let ``selectionLadder returns None for an unknown language`` () =
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    Assert.True((Highlight.selectionLadder registry "not-a-language" "x" 0 0).IsNone)

[<Theory>]
[<Trait("Category", "Smoke")>]
[<InlineData("html", "<style>\na { color: red; }\n</style>", "color")>]
[<InlineData("html", "<script>\nconst x = 'y';\n</script>", "const")>]
[<InlineData("markdown", "# Title\n\n```js\nconst x = 'y';\n```\n", "const")>]
[<InlineData("astro", "---\nconst title = 'x';\n---\n<h1>{title}</h1>", "const")>]
[<InlineData("php", "<div class=\"a\"></div>\n<?php echo 1; ?>", "class")>]
let ``parseSpans highlights injected languages`` (lang: string) (src: string) (token: string) =
    // Child-language spans are offset into the host document: the token's
    // char range must be painted, and painted by the child grammar's
    // capture (a keyword / attribute), not the host's raw-text span.
    use registry =
        match HighlightRegistry.tryCreate () with
        | Some r -> r
        | None -> failwith "no registry"

    let spans = Highlight.parseSpans registry lang src |> Option.defaultValue [||]
    let at = src.IndexOf token

    match Highlight.spanAt spans at with
    | Some span ->
        Assert.True(span.EndByte - span.StartByte <= token.Length + 1, $"'{token}' painted by a host span: {span}")
    | None -> Assert.Fail $"'{token}' in {lang} is not highlighted: {spans}"


[<Theory>]
[<InlineData("typescript", "const x: string = 'hi'")>]
[<InlineData("tsx", "const x: string = 'hi'")>]
let ``typescript queries inherit the javascript captures`` (lang: string) (src: string) =
    // The TS query files carry only the TS-specific patterns; without the
    // `; inherits: javascript` header `const` and the string paint nothing.
    use registry = (HighlightRegistry.tryCreate ()).Value
    let spans = Highlight.parseSpans registry lang src |> Option.defaultValue [||]
    Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Keyword)
    Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = String)


// -- user languages (config.json `languages`) ------------------------------------

let private nativeDir =
    Path.Combine(System.AppContext.BaseDirectory, "runtimes", "osx-arm64", "native")

let private withGrammarDir (body: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), "fedit-lang-" + Path.GetRandomFileName())

    Directory.CreateDirectory dir |> ignore

    try
        body dir
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``detectLanguageWith consults user extensions first`` () =
    let user = Map.ofList [ ".vue", "vue"; ".md", "mdx-custom" ]

    Highlight.detectLanguageWith user (Some "/a/App.vue") ""
    |> should equal (Some "vue")

    Highlight.detectLanguageWith user (Some "/a/README.MD") ""
    |> should equal (Some "mdx-custom")

    Highlight.detectLanguageWith user (Some "/a/x.fs") ""
    |> should equal (Some "fsharp")

    Highlight.detectLanguageWith Map.empty (Some "/a/App.vue") ""
    |> should equal None

[<Fact>]
let ``a user language loads its grammar from an absolute path and its own queries`` () =
    // Stand-in for a third-party grammar: the shipped toml library copied
    // under a new name, so nothing in the app directory can satisfy it.
    if File.Exists(Path.Combine(nativeDir, "libtree-sitter-toml.dylib")) then
        withGrammarDir (fun dir ->
            let library = Path.Combine(dir, "libtree-sitter-mytoml.dylib")
            File.Copy(Path.Combine(nativeDir, "libtree-sitter-toml.dylib"), library)
            let queries = Path.Combine(dir, "queries")
            Directory.CreateDirectory queries |> ignore
            File.WriteAllText(Path.Combine(queries, "highlights.scm"), "(bare_key) @attribute\n(string) @string\n")

            let spec =
                { Name = "mytoml"
                  Extensions = [ "mytoml" ]
                  Library = Some library
                  Symbol = Some "tree_sitter_toml"
                  Queries = Some queries }

            use registry = (HighlightRegistry.tryCreateWith [ spec ]).Value

            let spans =
                Highlight.parseSpans registry "mytoml" "title = \"x\""
                |> Option.defaultValue [||]

            Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Attribute)
            Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = String))

[<Fact>]
let ``a user language without a symbol derives it from the library name`` () =
    if File.Exists(Path.Combine(nativeDir, "libtree-sitter-toml.dylib")) then
        withGrammarDir (fun dir ->
            // `libtree-sitter-toml.dylib` → `tree_sitter_toml`
            let library = Path.Combine(dir, "libtree-sitter-toml.dylib")
            File.Copy(Path.Combine(nativeDir, "libtree-sitter-toml.dylib"), library)

            let spec =
                { Name = "toml2"
                  Extensions = []
                  Library = Some library
                  Symbol = None
                  Queries = None }

            use registry = (HighlightRegistry.tryCreateWith [ spec ]).Value
            (registry.TryGetLanguage "toml2").IsSome |> should equal true)

[<Fact>]
let ``a queries-only user language overrides a shipped language's queries`` () =
    withGrammarDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "highlights.scm"), "(document) @comment\n")

        let spec =
            { Name = "json"
              Extensions = []
              Library = None
              Symbol = None
              Queries = Some dir }

        use registry = (HighlightRegistry.tryCreateWith [ spec ]).Value

        let spans =
            Highlight.parseSpans registry "json" "{\"a\": 1}" |> Option.defaultValue [||]

        spans |> Array.map _.Capture |> Array.distinct |> should equal [| Comment |])

[<Fact>]
let ``a missing user grammar library leaves the language unregistered`` () =
    let spec =
        { Name = "ghost"
          Extensions = [ ".ghost" ]
          Library = Some "/nonexistent/libtree-sitter-ghost.dylib"
          Symbol = None
          Queries = None }

    use registry = (HighlightRegistry.tryCreateWith [ spec ]).Value
    (registry.TryGetLanguage "ghost").IsNone |> should equal true
    Highlight.parseSpans registry "ghost" "x" |> should equal None

[<Fact>]
let ``AddLanguages registers a grammar after the registry was created`` () =
    if File.Exists(Path.Combine(nativeDir, "libtree-sitter-toml.dylib")) then
        withGrammarDir (fun dir ->
            let library = Path.Combine(dir, "libtree-sitter-late.dylib")
            File.Copy(Path.Combine(nativeDir, "libtree-sitter-toml.dylib"), library)
            let queries = Path.Combine(dir, "queries")
            Directory.CreateDirectory queries |> ignore
            File.WriteAllText(Path.Combine(queries, "highlights.scm"), "(bare_key) @attribute\n")

            use registry = (HighlightRegistry.tryCreate ()).Value
            Highlight.parseSpans registry "late" "a = 1" |> should equal None

            registry.AddLanguages
                [ { Name = "late"
                    Extensions = [ ".late" ]
                    Library = Some library
                    Symbol = Some "tree_sitter_toml"
                    Queries = Some queries } ]

            let spans = Highlight.parseSpans registry "late" "a = 1" |> Option.defaultValue [||]
            Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Attribute))
