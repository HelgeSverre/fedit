# Syntax Highlighting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Revised:** 2026-05-21 to track code drift since the original 2026-05-19 plan. Major changes: `Theme` now carries `Color` values (not raw `int`); `Config` is nested inside `Model`; `ConfigIO` owns persistence (not `Runtime.fs`); the per-cell renderer lives in `View.fs` (`Layout.renderEditor`), not `Renderer.fs`; `HighlightState` lives on `Model.HighlightStates` keyed by buffer id rather than on `BufferState` (avoids touching `Buffer.fs`); the project targets `net10.0`.

**Goal:** Add token-level syntax highlighting for F# files to fedit using `TreeSitter.DotNet` and `ionide/tree-sitter-fsharp`, with the architecture set up to take more languages later.

**Architecture:** `HighlightRegistry` owns `Language`/`Query` singletons and is held on `Model.HighlightRegistry`. Each open buffer has an entry in `Model.HighlightStates : Map<int, HighlightState>` with its own `Parser` and current `Tree`. On every buffer change (Phase 1 = full reparse) we re-parse and re-query, producing a sorted `HighlightSpan` array. `Layout.renderEditor` overlays per-cell foreground colors from those spans on top of the existing cell style. `Theme` gains 16 `Color`-typed syntax fields. The F# native library is vendored per-RID in `src/Fedit/runtimes/<rid>/native/` and built from a git submodule via a justfile recipe.

**Tech Stack:** F# / .NET 10, `TreeSitter.DotNet` 1.3.x (MIT NuGet), `ionide/tree-sitter-fsharp` v0.3.0 (MIT, git submodule), `clang`/`zig` for grammar builds, `xUnit` for tests.

**Reference:** Companion design spec at `docs/superpowers/specs/2026-05-19-syntax-highlighting-spec.md`. Verification report at `docs/superpowers/research/2026-05-19-treesitter-dotnet-verification.md`.

---

## File Structure

### New files

| Path                                                              | Purpose                                                                                                  |
| ----------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| `vendor/tree-sitter-fsharp/`                                      | Git submodule pointing at `ionide/tree-sitter-fsharp` v0.3.0.                                            |
| `src/Fedit/runtimes/osx-arm64/native/libtree-sitter-fsharp.dylib` | Pre-built F# grammar for macOS arm64.                                                                    |
| `src/Fedit/runtimes/osx-x64/native/libtree-sitter-fsharp.dylib`   | Pre-built F# grammar for macOS x64.                                                                      |
| `src/Fedit/runtimes/linux-x64/native/libtree-sitter-fsharp.so`    | Pre-built F# grammar for Linux x64.                                                                      |
| `src/Fedit/runtimes/linux-arm64/native/libtree-sitter-fsharp.so`  | Pre-built F# grammar for Linux arm64.                                                                    |
| `src/Fedit/runtimes/win-x64/native/tree-sitter-fsharp.dll`        | Pre-built F# grammar for Windows x64.                                                                    |
| `src/Fedit/Resources/queries/fsharp/highlights.scm`               | Highlights query, copied from grammar's `queries/`. Embedded resource.                                   |
| `src/Fedit/Highlight.fs`                                          | `HighlightCapture`, `HighlightSpan`, `HighlightState`, `HighlightRegistry`, parse + query orchestration. |
| `tests/Fedit.Tests/HighlightTests.fs`                             | Capture resolution, language detection, span-overlap math, end-to-end parse+query smoke.                 |
| `tests/Fedit.Tests/Fixtures/sample.fs`                            | Small F# fixture used by integration tests.                                                              |

### Modified files

| Path                                   | Change                                                                                                                                                                                                                         |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `.gitmodules`                          | Add `vendor/tree-sitter-fsharp` submodule.                                                                                                                                                                                     |
| `Directory.Packages.props`             | Add a `<PackageVersion Include="TreeSitter.DotNet" Version="1.3.0" />` row (CPM is in use; the project files do not pin versions inline).                                                                                      |
| `src/Fedit/Fedit.fsproj`               | Add `TreeSitter.DotNet` PackageReference (no version — CPM resolves), embed `highlights.scm`, copy `runtimes/` natives to output, add publish-trim MSBuild target, add `<Compile Include="Highlight.fs" />` after `Themes.fs`. |
| `src/Fedit/Themes.fs`                  | Add 16 `Color`-typed syntax fields to `Theme`. Update bundled themes with sensible defaults.                                                                                                                                   |
| `src/Fedit/Model.fs`                   | Add `SyntaxHighlightingEnabled: bool` to `Config`; update `Config.defaults`. Add `HighlightRegistry: HighlightRegistry option` and `HighlightStates: Map<int, HighlightState>` to `Model`.                                     |
| `src/Fedit/Commands.fs`                | Add `Syntax of verb: string` to `Command` DU; add `syntax` spec parsing `on`/`off`/`toggle`; thread through `completionsWith` for argument completion.                                                                         |
| `src/Fedit/Config.fs` (`ConfigIO`)     | `load` reads optional `"syntaxHighlighting"`; `save` writes it back. `loadUserThemes` reads optional `syntax` block with per-field fallback to `Themes.defaultTheme`.                                                          |
| `src/Fedit/Editor.fs`                  | `init` accepts the registry and stashes it on `Model`. `FileOpened` detects language and seeds `HighlightStates`. A `reparseIfHighlighted` helper runs on every buffer-mutating path. `executeCommand` handles `Syntax`.       |
| `src/Fedit/View.fs` (`Layout`)         | Inside `renderEditor`, after the text row is written, walk the visible columns and overlay `Highlight.colorFor` foregrounds where the buffer's spans cover the cell.                                                           |
| `src/Fedit/Runtime.fs`                 | Construct the registry, pass it to `Editor.init`. Dispose the registry + per-buffer highlight states on shutdown.                                                                                                              |
| `tests/Fedit.Tests/Fedit.Tests.fsproj` | Add `HighlightTests.fs` to compile list (before `Program.fs`). Copy `Fixtures/*.*` to output.                                                                                                                                  |
| `justfile`                             | Add `build-grammar` (host) and per-RID `build-grammar-<rid>` recipes, plus `build-grammars-all`.                                                                                                                               |
| `README.md`                            | Mention syntax highlighting and the `:syntax` command. Note F# is the only language for MVP.                                                                                                                                   |
| `docs/syntax-highlighting.md`          | Contributor/user guide (created).                                                                                                                                                                                              |

---

## Task 1: Add `TreeSitter.DotNet` and verify load

**Files:**

- Modify: `Directory.Packages.props`, `src/Fedit/Fedit.fsproj`
- Create: `tests/Fedit.Tests/HighlightTests.fs`
- Modify: `tests/Fedit.Tests/Fedit.Tests.fsproj`

- [ ] **Step 1: Add the package version (CPM) and reference**

In `Directory.Packages.props`, add `<PackageVersion Include="TreeSitter.DotNet" Version="1.3.0" />`.

In `src/Fedit/Fedit.fsproj`, after the existing `<PackageReference Include="FSharp.Core" />`, add:

```xml
<PackageReference Include="TreeSitter.DotNet" />
```

- [ ] **Step 2: Verify it restores**

Run: `just build` (or `dotnet restore Fedit.slnx`).
Expected: completes with no errors.

- [ ] **Step 3: Write a probe test**

Create `tests/Fedit.Tests/HighlightTests.fs`:

```fsharp
module Fedit.Tests.HighlightTests

open Xunit

[<Fact>]
let ``TreeSitter.DotNet types are reachable`` () =
    let parserType = typeof<TreeSitter.Parser>
    Assert.Equal("Parser", parserType.Name)
```

Add to `tests/Fedit.Tests/Fedit.Tests.fsproj` compile list (before `Program.fs`):

```xml
<Compile Include="HighlightTests.fs" />
```

- [ ] **Step 4: Run test**

Run: `just test`
Expected: new test passes; all existing tests still pass.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/Fedit/Fedit.fsproj tests/Fedit.Tests/HighlightTests.fs tests/Fedit.Tests/Fedit.Tests.fsproj
git commit -m "feat(highlight): add TreeSitter.DotNet package and load probe"
```

---

## Task 2: Vendor the F# grammar as a git submodule

**Files:**

- Create: `vendor/tree-sitter-fsharp/` (submodule)
- Modify: `.gitmodules`

- [ ] **Step 1: Add the submodule**

```bash
git submodule add https://github.com/ionide/tree-sitter-fsharp.git vendor/tree-sitter-fsharp
(cd vendor/tree-sitter-fsharp && git checkout v0.3.0)
```

- [ ] **Step 2: Verify**

Run: `cat .gitmodules`
Expected: contains `[submodule "vendor/tree-sitter-fsharp"]` block pointing at the ionide repo.

Run: `ls vendor/tree-sitter-fsharp/queries/`
Expected: lists at least `highlights.scm`.

- [ ] **Step 3: Commit**

```bash
git add .gitmodules vendor/tree-sitter-fsharp
git commit -m "feat(highlight): vendor tree-sitter-fsharp v0.3.0 as submodule"
```

---

## Task 3: Host-RID grammar build via justfile

**Files:**

- Modify: `justfile`

- [ ] **Step 1: Add the recipe**

```just
# Build the tree-sitter-fsharp shared library for the host machine.
[group('build')]
build-grammar:
    #!/usr/bin/env bash
    set -euo pipefail
    cd vendor/tree-sitter-fsharp
    npx --yes tree-sitter generate
    case "$(uname -s)/$(uname -m)" in
        Darwin/arm64) rid="osx-arm64"; out="libtree-sitter-fsharp.dylib" ;;
        Darwin/x86_64) rid="osx-x64"; out="libtree-sitter-fsharp.dylib" ;;
        Linux/x86_64) rid="linux-x64"; out="libtree-sitter-fsharp.so" ;;
        Linux/aarch64) rid="linux-arm64"; out="libtree-sitter-fsharp.so" ;;
        *) echo "Unknown host: $(uname -s)/$(uname -m)"; exit 1 ;;
    esac
    dest="../../src/Fedit/runtimes/$rid/native"
    mkdir -p "$dest"
    extra="$(ls src/scanner.c 2>/dev/null || true)"
    clang -O2 -shared -fPIC -I src -o "$dest/$out" src/parser.c $extra
    echo "Built $dest/$out"
```

- [ ] **Step 2: Run the recipe**

Run: `just build-grammar`
Expected: produces `src/Fedit/runtimes/<host-rid>/native/libtree-sitter-fsharp.{dylib|so}`.

If `tree-sitter generate` fails because the CLI is missing, install via `npm install -g tree-sitter-cli` (note this in the contributor doc later in Task 19).

- [ ] **Step 3: Commit**

```bash
git add justfile src/Fedit/runtimes
git commit -m "feat(highlight): justfile recipe + host-RID build of tree-sitter-fsharp"
```

---

## Task 4: Embed `highlights.scm` as a resource

**Files:**

- Create: `src/Fedit/Resources/queries/fsharp/highlights.scm`
- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Copy the query file**

```bash
mkdir -p src/Fedit/Resources/queries/fsharp
cp vendor/tree-sitter-fsharp/queries/highlights.scm src/Fedit/Resources/queries/fsharp/highlights.scm
```

If the grammar ships multiple highlight files, copy the one under `queries/fsharp/highlights.scm` (the main F# one, not `fsharp_signature`).

- [ ] **Step 2: Embed it**

In `src/Fedit/Fedit.fsproj`, add a new `<ItemGroup>`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources/queries/fsharp/highlights.scm">
    <LogicalName>fedit.queries.fsharp.highlights.scm</LogicalName>
  </EmbeddedResource>
</ItemGroup>
```

- [ ] **Step 3: Build**

Run: `just build`
Expected: succeeds; resource compiled into the assembly.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Resources src/Fedit/Fedit.fsproj
git commit -m "feat(highlight): embed F# highlights.scm as resource"
```

---

## Task 5: Copy native runtimes to output / publish

**Files:**

- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Add a copy ItemGroup**

```xml
<ItemGroup>
  <None Include="runtimes/**/*.*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 2: Verify**

Run: `dotnet publish src/Fedit/Fedit.fsproj -c Release -r osx-arm64 --self-contained true -o /tmp/fedit-publish-test`
Expected: `/tmp/fedit-publish-test/runtimes/osx-arm64/native/libtree-sitter-fsharp.dylib` exists.

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Fedit.fsproj
git commit -m "feat(highlight): copy runtimes/ natives to publish output"
```

---

## Task 6: `HighlightCapture` + capture-name resolution

**Files:**

- Create: `src/Fedit/Highlight.fs`
- Modify: `src/Fedit/Fedit.fsproj`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Failing tests**

Append to `tests/Fedit.Tests/HighlightTests.fs`:

```fsharp
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
```

- [ ] **Step 2: Create `Highlight.fs`**

```fsharp
namespace Fedit

open System

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

[<RequireQualifiedAccess>]
module Highlight =

    let resolveCapture (captureName: string) : HighlightCapture option =
        if String.IsNullOrEmpty captureName then None
        else
            let starts (prefix: string) = captureName.StartsWith(prefix + ".", StringComparison.Ordinal)
            match captureName with
            | "keyword.control" -> Some KeywordControl
            | s when s.StartsWith("keyword.control.", StringComparison.Ordinal) -> Some KeywordControl
            | "keyword.operator" -> Some KeywordOperator
            | s when s.StartsWith("keyword.operator.", StringComparison.Ordinal) -> Some KeywordOperator
            | "keyword" -> Some Keyword
            | s when starts "keyword" -> Some Keyword
            | "string.special" -> Some StringSpecial
            | s when s.StartsWith("string.special.", StringComparison.Ordinal) -> Some StringSpecial
            | "string" -> Some String
            | s when starts "string" -> Some String
            | "function.call" -> Some FunctionCall
            | s when s.StartsWith("function.call.", StringComparison.Ordinal) -> Some FunctionCall
            | "function" -> Some Function
            | s when starts "function" -> Some Function
            | "type" -> Some Type
            | s when starts "type" -> Some Type
            | "constructor" -> Some Constructor
            | s when starts "constructor" -> Some Constructor
            | "variable.parameter" -> Some Parameter
            | s when s.StartsWith("variable.parameter.", StringComparison.Ordinal) -> Some Parameter
            | "variable" -> Some Variable
            | s when starts "variable" -> Some Variable
            | "number" -> Some Number
            | s when starts "number" -> Some Number
            | "comment" -> Some Comment
            | s when starts "comment" -> Some Comment
            | "operator" -> Some Operator
            | s when starts "operator" -> Some Operator
            | "punctuation" -> Some Punctuation
            | s when starts "punctuation" -> Some Punctuation
            | "attribute" -> Some Attribute
            | s when starts "attribute" -> Some Attribute
            | _ -> None
```

- [ ] **Step 3: Wire into compile order**

In `src/Fedit/Fedit.fsproj`, add `<Compile Include="Highlight.fs" />` immediately after `Themes.fs` and before `Commands.fs`. Highlight has no upward deps inside Fedit at this stage (later steps add Theme lookup via `Highlight.colorFor`, which is fine — Themes is already compiled by then).

- [ ] **Step 4: Run tests**

Run: `just test`
Expected: new resolver tests pass; existing suites untouched.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Highlight.fs src/Fedit/Fedit.fsproj tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): HighlightCapture DU and capture-name resolution"
```

---

## Task 7: `HighlightRegistry` + language loading

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Failing integration test**

```fsharp
[<Fact>]
let ``HighlightRegistry loads F# language and query`` () =
    use registry =
        match Highlight.HighlightRegistry.tryCreate() with
        | Some r -> r
        | None -> failwith "registry failed to create — F# grammar likely missing from runtimes/"
    Assert.True((registry.TryGetLanguage "fsharp").IsSome, "language fsharp not loaded")
    Assert.True((registry.TryGetQuery "fsharp").IsSome, "query for fsharp not built")
```

- [ ] **Step 2: Implement the registry**

Append to `src/Fedit/Highlight.fs`:

```fsharp
open System.IO
open System.Reflection
open System.Collections.Concurrent

type HighlightRegistry
    private (languages: ConcurrentDictionary<string, TreeSitter.Language>,
             queries: ConcurrentDictionary<string, TreeSitter.Query>) =

    member _.TryGetLanguage(name: string) : TreeSitter.Language option =
        match languages.TryGetValue name with
        | true, l -> Some l
        | _ -> None

    member _.TryGetQuery(name: string) : TreeSitter.Query option =
        match queries.TryGetValue name with
        | true, q -> Some q
        | _ -> None

    interface IDisposable with
        member _.Dispose() =
            for q in queries.Values do
                try (q :> IDisposable).Dispose() with _ -> ()
            queries.Clear()
            languages.Clear()   // Language wrappers don't own the native lib; OS reclaims on exit.

    static member tryCreate() : HighlightRegistry option =
        let languages = ConcurrentDictionary<string, TreeSitter.Language>()
        let queries = ConcurrentDictionary<string, TreeSitter.Query>()
        try
            let lang = TreeSitter.Language("tree-sitter-fsharp", "tree_sitter_fsharp")
            languages.["fsharp"] <- lang
            let asm = Assembly.GetExecutingAssembly()
            use stream = asm.GetManifestResourceStream("fedit.queries.fsharp.highlights.scm")
            if isNull stream then
                None
            else
                use reader = new StreamReader(stream)
                let scm = reader.ReadToEnd()
                queries.["fsharp"] <- new TreeSitter.Query(lang, scm)
                Some (HighlightRegistry(languages, queries))
        with _ -> None
```

Adapt the `TreeSitter.Language` / `TreeSitter.Query` constructor signatures if the actual API differs slightly from the verification report — confirm at implementation time.

- [ ] **Step 3: Run tests**

Run: `just test`
Expected: PASS. If FAIL with "language fsharp not loaded", the test runner's output directory doesn't carry `runtimes/`. Add a `<None Include="..\..\src\Fedit\runtimes\**\*.*">` copy block to the test fsproj, OR add a `<RuntimeIdentifier>` hint so MSBuild copies the host RID's natives next to the test DLL.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Highlight.fs tests/Fedit.Tests/HighlightTests.fs tests/Fedit.Tests/Fedit.Tests.fsproj
git commit -m "feat(highlight): HighlightRegistry loads F# language + query"
```

---

## Task 8: `HighlightSpan` + `computeSpans`

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Create: `tests/Fedit.Tests/Fixtures/sample.fs`
- Modify: `tests/Fedit.Tests/Fedit.Tests.fsproj`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Fixture**

Create `tests/Fedit.Tests/Fixtures/sample.fs`:

```fsharp
module Sample

let greeting = "hello"

let square (x: int) : int = x * x
```

In `tests/Fedit.Tests/Fedit.Tests.fsproj`, add:

```xml
<ItemGroup>
  <None Include="Fixtures/*.*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 2: Failing test**

```fsharp
[<Fact>]
let ``computeSpans returns at least one keyword and one string span for sample.fs`` () =
    use registry = Highlight.HighlightRegistry.tryCreate() |> Option.defaultWith (fun () -> failwith "no registry")
    let lang = registry.TryGetLanguage "fsharp" |> Option.get
    let query = registry.TryGetQuery "fsharp" |> Option.get
    let source = System.IO.File.ReadAllText "Fixtures/sample.fs"
    use parser = new TreeSitter.Parser(lang)
    use tree =
        match parser.Parse source with
        | null -> failwith "parser returned null"
        | t -> t
    let spans = Highlight.computeSpans query tree
    Assert.NotEmpty spans
    Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = Keyword)
    Assert.Contains(spans, fun (s: HighlightSpan) -> s.Capture = String)
```

- [ ] **Step 3: Implement**

Append:

```fsharp
type HighlightSpan =
    { Capture: HighlightCapture
      StartByte: int
      EndByte: int }

[<RequireQualifiedAccess>]
module Highlight =
    // ...existing definitions...

    let computeSpans (query: TreeSitter.Query) (tree: TreeSitter.Tree) : HighlightSpan array =
        let result = ResizeArray<HighlightSpan>()
        use cursor = query.Execute(tree.RootNode)
        for capture in cursor.Captures do
            let name = query.CaptureNames.[int capture.Index]
            match resolveCapture name with
            | Some c ->
                let node = capture.Node
                result.Add
                    { Capture = c
                      StartByte = node.StartIndex
                      EndByte = node.EndIndex }
            | None -> ()
        result.Sort(fun a b -> compare a.StartByte b.StartByte)
        result.ToArray()
```

(Confirm `cursor.Captures` and `query.CaptureNames` names at implementation time — the verification report uses `_captureNames` internally; the public surface may be named slightly differently.)

- [ ] **Step 4: Run tests**

Run: `just test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Highlight.fs tests/Fedit.Tests/HighlightTests.fs tests/Fedit.Tests/Fixtures tests/Fedit.Tests/Fedit.Tests.fsproj
git commit -m "feat(highlight): computeSpans queries tree-sitter highlights for F#"
```

---

## Task 9: `HighlightState`, `parse`, `dispose`, `detectLanguage`, `spanAt`

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

No `Buffer.fs` change — `HighlightState` is held on `Model`, not on `BufferState`.

- [ ] **Step 1: Add helpers**

```fsharp
type HighlightState =
    { Language: string
      Parser: TreeSitter.Parser
      Tree: TreeSitter.Tree option
      Spans: HighlightSpan array }

[<RequireQualifiedAccess>]
module Highlight =
    // ...existing...

    let detectLanguage (path: string option) : string option =
        path
        |> Option.bind (fun p ->
            match (Path.GetExtension p).ToLowerInvariant() with
            | ".fs" | ".fsi" | ".fsx" -> Some "fsharp"
            | _ -> None)

    let dispose (state: HighlightState) =
        state.Tree |> Option.iter (fun t -> try (t :> IDisposable).Dispose() with _ -> ())
        try (state.Parser :> IDisposable).Dispose() with _ -> ()

    /// Build a fresh HighlightState for the given language + source.
    /// Disposes the prior state's tree/parser before allocating new ones.
    let parse (registry: HighlightRegistry) (language: string) (source: string) (previous: HighlightState option) : HighlightState option =
        previous |> Option.iter dispose
        match registry.TryGetLanguage language, registry.TryGetQuery language with
        | Some lang, Some query ->
            try
                let parser = new TreeSitter.Parser(lang)
                let tree = parser.Parse source
                if isNull tree then
                    Some { Language = language; Parser = parser; Tree = None; Spans = Array.empty }
                else
                    Some
                        { Language = language
                          Parser = parser
                          Tree = Some tree
                          Spans = computeSpans query tree }
            with _ -> None
        | _ -> None

    /// Binary search the sorted span array for the span containing `charIndex`.
    /// Returns None if no span covers that index. Spans may nest; first hit wins.
    let spanAt (spans: HighlightSpan array) (charIndex: int) : HighlightSpan option =
        if spans.Length = 0 then None
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
```

- [ ] **Step 2: Unit tests**

```fsharp
[<Fact>]
let ``detectLanguage maps F# extensions`` () =
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "foo.fs"))
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "Bar.FSI"))
    Assert.Equal(Some "fsharp", Highlight.detectLanguage (Some "script.fsx"))
    Assert.Equal(None, Highlight.detectLanguage (Some "readme.md"))
    Assert.Equal(None, Highlight.detectLanguage None)

[<Fact>]
let ``spanAt returns covering span via binary search`` () =
    let spans : HighlightSpan array =
        [| { Capture = Keyword; StartByte = 0; EndByte = 6 }
           { Capture = String;  StartByte = 10; EndByte = 17 }
           { Capture = Comment; StartByte = 20; EndByte = 30 } |]
    Assert.Equal(Some Keyword, (Highlight.spanAt spans 3) |> Option.map (fun s -> s.Capture))
    Assert.Equal(Some String,  (Highlight.spanAt spans 10) |> Option.map (fun s -> s.Capture))
    Assert.Equal(None,         Highlight.spanAt spans 7)
    Assert.Equal(None,         Highlight.spanAt spans 30)
```

- [ ] **Step 3: Run tests**

Run: `just test`

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Highlight.fs tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): HighlightState, parse, dispose, detectLanguage, spanAt"
```

---

## Task 10: Add `HighlightRegistry` + `HighlightStates` to `Model`; thread through `Editor.init`

**Files:**

- Modify: `src/Fedit/Model.fs`
- Modify: `src/Fedit/Editor.fs`

- [ ] **Step 1: Extend `Config` and `Model`**

In `src/Fedit/Model.fs`, extend `Config`:

```fsharp
type Config =
    { // ...existing fields
      SyntaxHighlightingEnabled: bool }
```

Update `Config.defaults` to set `SyntaxHighlightingEnabled = true`.

Extend `Model`:

```fsharp
type Model =
    { // ...existing fields
      HighlightRegistry: HighlightRegistry option
      HighlightStates: Map<int, HighlightState> }
```

- [ ] **Step 2: Update `Editor.init`**

Add parameters `(highlightRegistry: HighlightRegistry option)` and initialize the new model fields. Signature:

```fsharp
let init rootPath size config userThemes (highlightRegistry: HighlightRegistry option) = ...
```

Set `HighlightRegistry = highlightRegistry; HighlightStates = Map.empty` in the returned model.

- [ ] **Step 3: Build**

Run: `just build`
Expected: surfaces errors at `Runtime.run`'s call to `Editor.init` (no registry passed yet). That's Task 16. Either pass `None` temporarily or implement Task 16 in the same pass.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Model.fs src/Fedit/Editor.fs
git commit -m "feat(highlight): SyntaxHighlightingEnabled + HighlightRegistry/HighlightStates on Model"
```

---

## Task 11: Initialize highlight on `FileOpened`; reparse on buffer mutations

**Files:**

- Modify: `src/Fedit/Editor.fs`

- [ ] **Step 1: Helper**

Add inside the `Editor` module:

```fsharp
let private reparseIfHighlighted (model: Model) (buffer: BufferState) (states: Map<int, HighlightState>) : Map<int, HighlightState> =
    if not model.Config.SyntaxHighlightingEnabled then states
    else
        match model.HighlightRegistry, Map.tryFind buffer.Id states with
        | Some registry, Some existing ->
            match Highlight.parse registry existing.Language (Buffer.text buffer) (Some existing) with
            | Some next -> Map.add buffer.Id next states
            | None -> Map.remove buffer.Id states
        | _ -> states
```

- [ ] **Step 2: Wire `FileOpened`**

Inside the `FileOpened` Ok branch, after `buffer` is constructed:

```fsharp
let nextStates =
    if model.Config.SyntaxHighlightingEnabled then
        match Highlight.detectLanguage (Some path), model.HighlightRegistry with
        | Some lang, Some registry ->
            match Highlight.parse registry lang normalized None with
            | Some state -> Map.add buffer.Id state model.HighlightStates
            | None -> model.HighlightStates
        | _ -> model.HighlightStates
    else model.HighlightStates
```

Thread `HighlightStates = nextStates` into the returned model.

- [ ] **Step 3: Wire buffer-mutating paths**

Locate the active-buffer mutation sites in `Editor.update` and helpers (`updateActiveBuffer` is the chokepoint for most of them — see `Editor.fs:39`). After computing the updated buffer, also rebuild the highlight state for that buffer:

```fsharp
let private updateActiveBuffer transform model =
    // ...existing rebuild of the buffer...
    let nextStates = reparseIfHighlighted model updatedBuffer model.HighlightStates
    { model with
        Editors = { model.Editors with Buffers = ... }
        HighlightStates = nextStates }
```

Audit any other site that constructs an updated `BufferState` outside `updateActiveBuffer` (paste, plugin actions in `applyPluginActions`) and pipe them through the same helper.

- [ ] **Step 4: Run tests**

Run: `just test`
Expected: existing tests still pass (no regressions). End-to-end "reparses after insert" test lands in Task 18 / smoke once everything is wired.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Editor.fs
git commit -m "feat(highlight): seed + reparse spans on FileOpened and buffer mutations"
```

---

## Task 12: Build the grammar for all 5 RIDs

**Files:**

- Modify: `justfile`
- Create: native libs in `src/Fedit/runtimes/<rid>/native/` for the four RIDs not built in Task 3.

- [ ] **Step 1: Per-RID recipes**

```just
[group('build')]
build-grammar-osx-arm64:
    cd vendor/tree-sitter-fsharp && npx --yes tree-sitter generate
    clang -O2 -shared -fPIC -target arm64-apple-macos11 \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/osx-arm64/native/libtree-sitter-fsharp.dylib \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

[group('build')]
build-grammar-osx-x64:
    cd vendor/tree-sitter-fsharp && npx --yes tree-sitter generate
    clang -O2 -shared -fPIC -target x86_64-apple-macos10.15 \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/osx-x64/native/libtree-sitter-fsharp.dylib \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

[group('build')]
build-grammar-linux-x64:
    zig cc -O2 -shared -fPIC -target x86_64-linux-gnu \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/linux-x64/native/libtree-sitter-fsharp.so \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

[group('build')]
build-grammar-linux-arm64:
    zig cc -O2 -shared -fPIC -target aarch64-linux-gnu \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/linux-arm64/native/libtree-sitter-fsharp.so \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

[group('build')]
build-grammar-win-x64:
    zig cc -O2 -shared -target x86_64-windows-gnu \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/win-x64/native/tree-sitter-fsharp.dll \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

[group('build')]
build-grammars-all: build-grammar-osx-arm64 build-grammar-osx-x64 build-grammar-linux-x64 build-grammar-linux-arm64 build-grammar-win-x64
```

- [ ] **Step 2: Run them**

```bash
brew install zig
just build-grammars-all
```

If a target fails (cross-compile linker quirks for `scanner.c` are not uncommon), build that RID inside a docker container of the target OS and note the workaround in `docs/syntax-highlighting.md` (Task 19).

- [ ] **Step 3: Commit**

```bash
git add justfile src/Fedit/runtimes
git commit -m "feat(highlight): build tree-sitter-fsharp for all 5 RIDs via zig cc"
```

---

## Task 13: Publish-time grammar trimming

**Files:**

- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: MSBuild target**

```xml
<Target Name="TrimUnusedTreeSitterGrammars" AfterTargets="Publish">
  <ItemGroup>
    <_TSGrammars Include="$(PublishDir)runtimes/$(RuntimeIdentifier)/native/libtree-sitter-*.so" />
    <_TSGrammars Include="$(PublishDir)runtimes/$(RuntimeIdentifier)/native/libtree-sitter-*.dylib" />
    <_TSGrammars Include="$(PublishDir)runtimes/$(RuntimeIdentifier)/native/tree-sitter-*.dll" />
    <_TSGrammarsKeep Include="$(PublishDir)runtimes/$(RuntimeIdentifier)/native/libtree-sitter-fsharp.so" />
    <_TSGrammarsKeep Include="$(PublishDir)runtimes/$(RuntimeIdentifier)/native/libtree-sitter-fsharp.dylib" />
    <_TSGrammarsKeep Include="$(PublishDir)runtimes/$(RuntimeIdentifier)/native/tree-sitter-fsharp.dll" />
    <_TSGrammarsRemove Include="@(_TSGrammars)" Exclude="@(_TSGrammarsKeep)" />
  </ItemGroup>
  <Delete Files="@(_TSGrammarsRemove)" />
  <Message Text="Trimmed @(_TSGrammarsRemove->Count()) tree-sitter grammars (kept F#)." Importance="high" />
</Target>
```

Pattern matches `libtree-sitter-<lang>.*` only — leaves `libtree-sitter.{so,dylib,dll}` (the core) intact. Verify against actual `TreeSitter.DotNet` payload naming.

- [ ] **Step 2: Verify**

```bash
dotnet publish src/Fedit/Fedit.fsproj -c Release -r osx-arm64 --self-contained true -o /tmp/fedit-publish-test
ls /tmp/fedit-publish-test/runtimes/osx-arm64/native/
du -sh /tmp/fedit-publish-test/runtimes/osx-arm64/native/
```

Expected: only `libtree-sitter.dylib` (core) and `libtree-sitter-fsharp.dylib` remain; size is single-digit MB.

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Fedit.fsproj
git commit -m "feat(highlight): publish-time trim of unused tree-sitter grammars"
```

---

## Task 14: 16 syntax colors on `Theme`

**Files:**

- Modify: `src/Fedit/Themes.fs`
- Modify: `src/Fedit/Highlight.fs`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Extend `Theme`**

In `src/Fedit/Themes.fs`:

```fsharp
type Theme =
    { Name: string
      Description: string
      Accent: Color
      StatusBg: Color
      SelectedBg: Color
      CurrentLine: Color
      StatusFg: Color
      SyntaxKeyword: Color
      SyntaxKeywordControl: Color
      SyntaxKeywordOperator: Color
      SyntaxString: Color
      SyntaxStringSpecial: Color
      SyntaxNumber: Color
      SyntaxComment: Color
      SyntaxFunction: Color
      SyntaxFunctionCall: Color
      SyntaxType: Color
      SyntaxConstructor: Color
      SyntaxVariable: Color
      SyntaxParameter: Color
      SyntaxOperator: Color
      SyntaxPunctuation: Color
      SyntaxAttribute: Color }
```

Pick `Color.indexed N` (or `Color.ofHex` for off-cube picks) defaults per bundled theme. Example baseline for `green`:

```fsharp
SyntaxKeyword        = Color.indexed 141   // purple — distinct from accent green
SyntaxKeywordControl = Color.indexed 141
SyntaxKeywordOperator= Color.indexed 141
SyntaxString         = Color.indexed 114   // muted green (not the accent)
SyntaxStringSpecial  = Color.indexed 180
SyntaxNumber         = Color.indexed 215
SyntaxComment        = Color.indexed 244
SyntaxFunction       = Color.indexed 117
SyntaxFunctionCall   = Color.indexed 117
SyntaxType           = Color.indexed 222
SyntaxConstructor    = Color.indexed 175
SyntaxVariable       = Color.Default       // keep surface fg
SyntaxParameter      = Color.Default
SyntaxOperator       = Color.indexed 248
SyntaxPunctuation    = Color.indexed 246
SyntaxAttribute      = Color.indexed 180
```

Do the same for each other bundled theme, picking indices that fit. Aim for: keyword stands out, comment is dim, string is distinct from accent, types/functions are readable.

- [ ] **Step 2: `Highlight.colorFor`**

Append to `src/Fedit/Highlight.fs`:

```fsharp
[<RequireQualifiedAccess>]
module Highlight =
    // ...existing...

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
```

`Highlight.fs` already sits after `Themes.fs` (per Task 6) so `Theme` is in scope.

- [ ] **Step 3: Tests**

```fsharp
[<Fact>]
let ``colorFor maps captures to theme fields`` () =
    let t = Themes.defaultTheme
    Assert.Equal(t.SyntaxKeyword, Highlight.colorFor t Keyword)
    Assert.Equal(t.SyntaxString,  Highlight.colorFor t String)
    Assert.Equal(t.SyntaxComment, Highlight.colorFor t Comment)
```

Run: `just test`

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Themes.fs src/Fedit/Highlight.fs tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): 16 Color syntax fields on Theme + colorFor"
```

---

## Task 15: User theme JSON — optional `syntax` block

**Files:**

- Modify: `src/Fedit/Config.fs` (`ConfigIO.loadUserThemes`)

- [ ] **Step 1: Parse a `syntax` object**

Inside `loadUserThemes`, after the existing chrome-color block matches, read an optional `syntax` object. For each of the 16 fields, fall back to the bundled `Themes.defaultTheme` value when unset or malformed:

```fsharp
let syntaxColor (root: System.Text.Json.JsonElement) (name: string) (fallback: Color) =
    match root.TryGetProperty name with
    | true, e when e.ValueKind = System.Text.Json.JsonValueKind.String ->
        e.GetString() |> Option.ofObj |> Option.bind Color.tryParse |> Option.defaultValue fallback
    | _ -> fallback

let synBlock =
    match root.TryGetProperty "syntax" with
    | true, e when e.ValueKind = System.Text.Json.JsonValueKind.Object -> Some e
    | _ -> None

let pickSyntax field fallback =
    match synBlock with
    | Some o -> syntaxColor o field fallback
    | None -> fallback

let d = Themes.defaultTheme
// ...
SyntaxKeyword = pickSyntax "keyword" d.SyntaxKeyword
SyntaxKeywordControl = pickSyntax "keywordControl" d.SyntaxKeywordControl
// ...etc
```

(Define the mapping consistently — lowercase / camelCase keys as the user types them in JSON.)

- [ ] **Step 2: Test**

Add a fixture user theme with two overrides and assert the rest fall back. Use a temp directory in the test to avoid touching `~/.config/fedit/themes`.

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Config.fs tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): user theme JSON gains optional syntax block"
```

---

## Task 16: Wire registry from `Runtime.fs`; persist `syntaxHighlighting`

**Files:**

- Modify: `src/Fedit/Runtime.fs`
- Modify: `src/Fedit/Config.fs`

- [ ] **Step 1: Construct registry at startup**

In `Runtime.run`, before `Editor.init`:

```fsharp
let highlightRegistry = Highlight.HighlightRegistry.tryCreate()
match highlightRegistry with
| None -> log "highlight: failed to load tree-sitter — F# files will render plain"
| Some _ -> log "highlight: loaded tree-sitter F# grammar"
```

Update the `Editor.init` call to pass `highlightRegistry`.

- [ ] **Step 2: Config persistence**

In `Config.fs`'s `ConfigIO.load`, read `syntaxHighlighting`:

```fsharp
let syntaxHighlightingEnabled =
    match root.TryGetProperty "syntaxHighlighting" with
    | true, e when e.ValueKind = System.Text.Json.JsonValueKind.False -> false
    | true, e when e.ValueKind = System.Text.Json.JsonValueKind.True -> true
    | _ -> defaults.SyntaxHighlightingEnabled
```

Add to the returned `Config`.

In `ConfigIO.save`, add:

```fsharp
root["syntaxHighlighting"] <- System.Text.Json.Nodes.JsonValue.Create config.SyntaxHighlightingEnabled
```

- [ ] **Step 3: Dispose on shutdown**

In the `finally` block of `Runtime.run`, after disposing watchers / writers:

```fsharp
model.HighlightStates |> Map.iter (fun _ s -> Highlight.dispose s)
highlightRegistry |> Option.iter (fun r -> (r :> IDisposable).Dispose())
```

- [ ] **Step 4: Build + test**

Run: `just check`
Expected: lint + build + test all green.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Runtime.fs src/Fedit/Config.fs
git commit -m "feat(highlight): construct registry at startup, persist syntaxHighlighting"
```

---

## Task 17: `:syntax on|off|toggle` command

**Files:**

- Modify: `src/Fedit/Commands.fs`
- Modify: `src/Fedit/Editor.fs`
- Modify: `tests/Fedit.Tests/CommandsTests.fs`

- [ ] **Step 1: Failing tests**

```fsharp
[<Fact>]
let ``parses 'syntax toggle' as Ready (Syntax "toggle")`` () =
    match Commands.parse "syntax toggle" with
    | Ready (Syntax "toggle") -> ()
    | other -> Assert.Fail($"unexpected: %A{other}")

[<Fact>]
let ``parses syntax on / off`` () =
    match Commands.parse "syntax on" with
    | Ready (Syntax "on") -> ()
    | other -> Assert.Fail($"unexpected: %A{other}")
    match Commands.parse "syntax off" with
    | Ready (Syntax "off") -> ()
    | other -> Assert.Fail($"unexpected: %A{other}")
```

- [ ] **Step 2: Extend `Command` + spec**

In `Commands.fs`:

```fsharp
type Command =
    // ...existing
    | Syntax of verb: string
```

Add to `specs`:

```fsharp
{ Name = "syntax"
  Usage = "syntax <on|off|toggle>"
  Summary = "Toggle syntax highlighting."
  Hidden = false
  Constructor =
    fun argument ->
        let trimmed = argument.Trim().ToLowerInvariant()
        match trimmed with
        | "" -> Pending "Specify on, off, or toggle."
        | "on" | "off" | "toggle" -> Ready (Syntax trimmed)
        | other -> Invalid $"Unknown syntax verb '{other}'." }
```

Add `syntax` to `completionsWith`'s verb-completion list (mirror the `plugin` block).

- [ ] **Step 3: Dispatch in `Editor.executeCommand`**

```fsharp
| Syntax verb ->
    let newValue =
        match verb with
        | "on" -> true
        | "off" -> false
        | "toggle" -> not model.Config.SyntaxHighlightingEnabled
        | _ -> model.Config.SyntaxHighlightingEnabled
    let nextConfig = { model.Config with SyntaxHighlightingEnabled = newValue }
    let updated = { model with Config = nextConfig }
    // If turning off, drop existing highlight states so we stop reparsing.
    // If turning on, seed states for any open buffer that has a language.
    let updated =
        if newValue then
            match updated.HighlightRegistry with
            | None -> updated
            | Some registry ->
                let nextStates =
                    updated.Editors.Buffers
                    |> Map.fold (fun acc id buffer ->
                        match Highlight.detectLanguage buffer.FilePath with
                        | None -> acc
                        | Some lang ->
                            match Highlight.parse registry lang (Buffer.text buffer) None with
                            | Some s -> Map.add id s acc
                            | None -> acc) Map.empty
                { updated with HighlightStates = nextStates }
        else
            updated.HighlightStates |> Map.iter (fun _ s -> Highlight.dispose s)
            { updated with HighlightStates = Map.empty }
    let note =
        if newValue then "Syntax highlighting on." else "Syntax highlighting off."
    updated |> notify (Some (Notification.info note)),
    [ SaveConfig nextConfig ]
```

- [ ] **Step 4: Run tests**

Run: `just test`

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Commands.fs src/Fedit/Editor.fs tests/Fedit.Tests/CommandsTests.fs
git commit -m "feat(highlight): :syntax on/off/toggle with config persistence"
```

---

## Task 18: Renderer overlay in `View.fs`

**Files:**

- Modify: `src/Fedit/View.fs`
- Optional: extra integration assertion in `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Add the overlay**

Inside `Layout.renderEditor` (`View.fs`), after the `Screen.writeText` call that paints the row's text and before the selection / search overlays, walk visible columns and re-set the cell's `Style.Foreground` from the spans:

```fsharp
if model.Config.SyntaxHighlightingEnabled then
    match Map.tryFind buffer.Id model.HighlightStates with
    | Some highlight when highlight.Spans.Length > 0 ->
        let lineStart = lineStarts[lineIndex]
        let lineText = rows[lineIndex]
        let visibleStart = buffer.ViewportLeft
        let visibleEnd = min lineText.Length (buffer.ViewportLeft + contentWidth)
        for col in visibleStart .. visibleEnd - 1 do
            match Highlight.spanAt highlight.Spans (lineStart + col) with
            | Some span ->
                let fg = Highlight.colorFor theme span.Capture
                if fg <> Color.Default then
                    let displayCol = col - buffer.ViewportLeft
                    let cellX = x + gutterWidth + displayCol
                    let cellY = row
                    if cellX < x + width && cellY >= 0 && cellY < height then
                        let existing = current.Cells.[cellY, cellX]
                        Screen.setCell cellX cellY { existing.Style with Foreground = fg } existing.Glyph current
            | None -> ()
    | _ -> ()
```

`current` is the local `Screen` reference in `renderEditor`; adapt if the local variable name differs. Selection / search overlays already run after this and continue to overwrite as today, preserving their precedence.

Avoid recomputing the binary search inside a hot loop more than needed; if profiling later shows it matters, batch into runs of same-span columns. MVP keeps it simple.

- [ ] **Step 2: Smoke-test manually**

Run: `./fedit src/Fedit/Highlight.fs`
Expected: file opens with `module`, `let`, `match`, string literals, and comments visibly colored. If nothing changes, check `--log fedit.log` for "registry failed" or "language fsharp not loaded".

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/View.fs
git commit -m "feat(highlight): overlay syntax colors per cell in renderEditor"
```

---

## Task 19: Per-RID CI smoke test

**Files:**

- Create: `.github/workflows/highlight-smoke.yml`
- Modify: `tests/Fedit.Tests/HighlightTests.fs` (tag the smoke test)

- [ ] **Step 1: Tag the smoke test**

Mark the `computeSpans returns at least one keyword and one string span` test with `[<Trait("Category", "Smoke")>]`.

- [ ] **Step 2: Workflow**

```yaml
name: highlight-smoke

on:
    push:
        paths:
            - "src/Fedit/Highlight.fs"
            - "src/Fedit/runtimes/**"
            - "vendor/tree-sitter-fsharp"
    pull_request:
        paths:
            - "src/Fedit/Highlight.fs"
            - "src/Fedit/runtimes/**"

jobs:
    smoke:
        strategy:
            fail-fast: false
            matrix:
                include:
                    - os: macos-14
                      rid: osx-arm64
                    - os: macos-13
                      rid: osx-x64
                    - os: ubuntu-latest
                      rid: linux-x64
                    - os: ubuntu-24.04-arm
                      rid: linux-arm64
                    - os: windows-latest
                      rid: win-x64
        runs-on: ${{ matrix.os }}
        steps:
            - uses: actions/checkout@v4
              with:
                  submodules: true
            - uses: actions/setup-dotnet@v4
              with:
                  dotnet-version: "10.0.x"
            - run: dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "Category=Smoke" --logger "console;verbosity=detailed"
```

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/highlight-smoke.yml tests/Fedit.Tests/HighlightTests.fs
git commit -m "ci(highlight): per-RID smoke test for tree-sitter F# grammar"
```

---

## Task 20: README + contributor doc + manual verification

**Files:**

- Modify: `README.md`
- Create: `docs/syntax-highlighting.md`

- [ ] **Step 1: README**

Add a short "Syntax highlighting" section to `README.md`:

```markdown
## Syntax highlighting

`fedit` highlights F# source files using [tree-sitter](https://tree-sitter.github.io/tree-sitter/) and the [ionide/tree-sitter-fsharp](https://github.com/ionide/tree-sitter-fsharp) grammar.

Toggle from the command bar:

- `:syntax on` — enable
- `:syntax off` — disable
- `:syntax toggle` — flip

Persisted to `~/.config/fedit/config.json` under `syntaxHighlighting`.

Only F# is supported today (`.fs`, `.fsi`, `.fsx`). See [docs/syntax-highlighting.md](docs/syntax-highlighting.md) for the roadmap.
```

- [ ] **Step 2: Contributor doc**

Create `docs/syntax-highlighting.md` covering: overview, supported languages, how to update the F# grammar (bump submodule, run `just build-grammars-all`), how themes map to captures, troubleshooting (no colors → check `runtimes/<rid>/native/libtree-sitter-fsharp.*` exists), roadmap (more languages, incremental parse, plugin-installed grammars).

- [ ] **Step 3: Manual end-to-end**

```bash
dotnet publish src/Fedit/Fedit.fsproj -c Release -r osx-arm64 --self-contained true -o /tmp/fedit-release
/tmp/fedit-release/fedit src/Fedit/Highlight.fs
```

Expected: keywords/strings/comments/types visibly colored. `:syntax off` → colors disappear, notification fires; quit + reopen → state persisted. Open `src/Fedit/Editor.fs` (~1200 lines): scrolling smooth, no per-keystroke lag. If lag is visible, queue Phase 2 (incremental parse).

- [ ] **Step 4: Commit**

```bash
git add README.md docs/syntax-highlighting.md
git commit -m "docs(highlight): README section and contributor guide"
```

---

## Self-Review Checklist

**Spec coverage** — every section of the spec maps to a task:

| Spec section                                            | Tasks                  |
| ------------------------------------------------------- | ---------------------- |
| Package                                                 | 1                      |
| Grammar (vendor + build)                                | 2, 3, 12               |
| Native binary layout                                    | 3, 5, 12               |
| Publish-time trimming                                   | 13                     |
| `HighlightCapture` / `HighlightSpan` / `HighlightState` | 6, 8, 9                |
| Capture-name resolution                                 | 6                      |
| `HighlightRegistry`                                     | 7                      |
| Singleton init at startup                               | 16                     |
| Edit handling (Phase 1 full reparse)                    | 11                     |
| Rendering integration                                   | 18                     |
| Theme integration (16 fields)                           | 14                     |
| User theme JSON (syntax block)                          | 15                     |
| Language detection                                      | 9                      |
| Lifecycle (init, edit, close, shutdown)                 | 10, 11, 16             |
| Failure modes                                           | 7, 11, 16              |
| Configuration (`:syntax` + persist)                     | 16, 17                 |
| Testing strategy                                        | 6, 7, 8, 9, 14, 17, 19 |
| README + docs                                           | 20                     |
| Manual verification                                     | 20                     |

**Placeholder scan:**

- Task 7 step 3 mentions adjusting the test fsproj's runtime copy if tests can't find the native lib. That's a fallback-on-failure clause, not a placeholder.
- Task 12 step 2 mentions docker fallback for cross-compile linker failures — same.
- Task 18 step 1 says "adapt if the local variable name differs" — `View.fs`'s `renderEditor` uses a local `screen` value (`screen` parameter), confirmed at implementation time.

**Type consistency:**

- `HighlightCapture`, `HighlightSpan`, `HighlightState`, `HighlightRegistry` — consistent across Tasks 6–11, 14, 16–18.
- `Color`-typed theme fields (`SyntaxKeyword`, `SyntaxString`, …) — defined in Task 14, used in `Highlight.colorFor` and the renderer overlay (Task 18).
- `Config.SyntaxHighlightingEnabled` and `Model.HighlightRegistry` / `Model.HighlightStates` — defined in Task 10, consumed in 11/16/17/18.
- `Effect.SaveConfig of Config` — already in the codebase; Task 17 emits it without a signature change.
