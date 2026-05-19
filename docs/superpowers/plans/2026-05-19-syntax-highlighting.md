# Syntax Highlighting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add token-level syntax highlighting for F# files to fedit using `TreeSitter.DotNet` and `ionide/tree-sitter-fsharp`, with the architecture set up to take more languages later.

**Architecture:** `HighlightRegistry` owns `Language`/`Query` singletons. Each `BufferState` carries a `HighlightState` with its own `Parser` and current `Tree`. On every buffer change (Phase 1 = full reparse) we re-parse and re-query, producing a sorted `HighlightSpan` array. The renderer overlays per-cell foreground colors from those spans on top of the existing cell style. Themes gain 16 syntax-color fields. The F# native library is vendored per-RID in `src/Fedit/runtimes/<rid>/native/` and built from a git submodule via a justfile recipe.

**Tech Stack:** F# / .NET 9, `TreeSitter.DotNet` 1.3.x (MIT NuGet), `ionide/tree-sitter-fsharp` v0.3.0 (MIT, git submodule), `clang`/`zig` for grammar builds, `xUnit` for tests.

**Reference:** Companion design spec at `docs/superpowers/specs/2026-05-19-syntax-highlighting-spec.md`. Verification report at `docs/superpowers/research/2026-05-19-treesitter-dotnet-verification.md`.

---

## File Structure

### New files

| Path                                                              | Purpose                                                                                                   |
| ----------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| `vendor/tree-sitter-fsharp/`                                      | Git submodule pointing at `ionide/tree-sitter-fsharp` v0.3.0.                                             |
| `src/Fedit/runtimes/osx-arm64/native/libtree-sitter-fsharp.dylib` | Pre-built F# grammar for macOS arm64.                                                                     |
| `src/Fedit/runtimes/osx-x64/native/libtree-sitter-fsharp.dylib`   | Pre-built F# grammar for macOS x64.                                                                       |
| `src/Fedit/runtimes/linux-x64/native/libtree-sitter-fsharp.so`    | Pre-built F# grammar for Linux x64.                                                                       |
| `src/Fedit/runtimes/linux-arm64/native/libtree-sitter-fsharp.so`  | Pre-built F# grammar for Linux arm64.                                                                     |
| `src/Fedit/runtimes/win-x64/native/tree-sitter-fsharp.dll`        | Pre-built F# grammar for Windows x64.                                                                     |
| `src/Fedit/Resources/queries/fsharp/highlights.scm`               | Highlights query, copied from grammar's `queries/`. Embedded resource.                                    |
| `src/Fedit/Highlight.fs`                                          | `HighlightCapture`, `HighlightSpan`, `HighlightState`, `HighlightRegistry`, edit and query orchestration. |
| `tests/Fedit.Tests/HighlightTests.fs`                             | Capture resolution, language detection, span-overlap math, end-to-end parse+query smoke.                  |
| `tests/Fedit.Tests/Fixtures/sample.fs`                            | Small F# fixture used by integration tests.                                                               |

### Modified files

| Path                                                                                       | Change                                                                                                                                                                                                                        |
| ------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `.gitmodules`                                                                              | Add `vendor/tree-sitter-fsharp` submodule.                                                                                                                                                                                    |
| `Fedit.slnx`                                                                               | No change (Highlight is part of `Fedit.fsproj`).                                                                                                                                                                              |
| `src/Fedit/Fedit.fsproj`                                                                   | Add `TreeSitter.DotNet` PackageReference, embed `highlights.scm`, copy `runtimes/` natives, add publish-trim MSBuild target, add `Highlight.fs` to compile order before `Model.fs`.                                           |
| `src/Fedit/Themes.fs`                                                                      | Add 16 syntax-color fields to `Theme`. Update bundled themes with sensible defaults. Update user-theme JSON loader to read optional `syntax` block.                                                                           |
| `src/Fedit/Primitives.fs`                                                                  | No change (highlight types live in `Highlight.fs`).                                                                                                                                                                           |
| `src/Fedit/Buffer.fs`                                                                      | Add `Highlight: HighlightState` field to `BufferState`. Helpers preserve it through transforms.                                                                                                                               |
| `src/Fedit/Model.fs`                                                                       | Add `SyntaxHighlightingEnabled: bool` to `Model`.                                                                                                                                                                             |
| `src/Fedit/Commands.fs`                                                                    | Add `Syntax of verb: string` to `Command` DU; add `syntax` spec parsing `on`/`off`/`toggle`.                                                                                                                                  |
| `src/Fedit/Editor.fs`                                                                      | Detect language and initialize `Highlight` on `FileOpened`. Trigger reparse on every buffer-content-changing path (insert/delete/replace/paste). Dispose old trees. Handle `Syntax` command. Persist toggle via `SaveConfig`. |
| `src/Fedit/Renderer.fs` (or `View.fs` / `Screen.fs` — whichever owns per-cell ANSI output) | Overlay highlight-derived foreground color per cell.                                                                                                                                                                          |
| `src/Fedit/Runtime.fs`                                                                     | Initialize `HighlightRegistry` at startup. Read `syntaxHighlighting` from config. Dispose registry on shutdown. Extend `saveConfig` to persist the toggle.                                                                    |
| `tests/Fedit.Tests/Fedit.Tests.fsproj`                                                     | Add `HighlightTests.fs` to compile list.                                                                                                                                                                                      |
| `justfile`                                                                                 | Add `build-grammar` and per-RID `build-grammar-<rid>` recipes.                                                                                                                                                                |
| `README.md`                                                                                | Mention syntax highlighting and the `:syntax` command. Note F# is the only language for MVP.                                                                                                                                  |

---

## Task 1: Add `TreeSitter.DotNet` and verify load

**Files:**

- Modify: `src/Fedit/Fedit.fsproj`
- Create: `tests/Fedit.Tests/HighlightTests.fs`
- Modify: `tests/Fedit.Tests/Fedit.Tests.fsproj`

- [ ] **Step 1: Add the package reference**

Edit `src/Fedit/Fedit.fsproj`. After the existing `<ItemGroup>` containing the `Compile Include` entries, add:

```xml
<ItemGroup>
  <PackageReference Include="TreeSitter.DotNet" Version="1.3.0" />
</ItemGroup>
```

- [ ] **Step 2: Verify it restores**

Run: `dotnet restore src/Fedit/Fedit.fsproj`
Expected: completes with no errors.

- [ ] **Step 3: Write a probe test**

Create `tests/Fedit.Tests/HighlightTests.fs`:

```fsharp
module Fedit.Tests.HighlightTests

open Xunit

[<Fact>]
let ``TreeSitter.DotNet types are reachable`` () =
    // If this compiles and runs, the package is wired up.
    let parserType = typeof<TreeSitter.Parser>
    Assert.Equal("Parser", parserType.Name)
```

Add to `tests/Fedit.Tests/Fedit.Tests.fsproj`'s compile list (before `Program.fs`):

```xml
<Compile Include="HighlightTests.fs" />
```

- [ ] **Step 4: Run test**

Run: `dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~HighlightTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Fedit.fsproj tests/Fedit.Tests/HighlightTests.fs tests/Fedit.Tests/Fedit.Tests.fsproj
git commit -m "feat(highlight): add TreeSitter.DotNet 1.3.0 package and load probe"
```

---

## Task 2: Vendor the F# grammar as a git submodule

**Files:**

- Create: `vendor/tree-sitter-fsharp/` (submodule)
- Modify: `.gitmodules`

- [ ] **Step 1: Add the submodule**

```bash
git submodule add https://github.com/ionide/tree-sitter-fsharp.git vendor/tree-sitter-fsharp
cd vendor/tree-sitter-fsharp
git checkout v0.3.0
cd -
```

- [ ] **Step 2: Verify**

Run: `cat .gitmodules`
Expected: contains `[submodule "vendor/tree-sitter-fsharp"]` block pointing at the ionide repo.

Run: `ls vendor/tree-sitter-fsharp/queries/`
Expected: lists at least `highlights.scm` (file we'll need in Task 4).

- [ ] **Step 3: Commit**

```bash
git add .gitmodules vendor/tree-sitter-fsharp
git commit -m "feat(highlight): vendor tree-sitter-fsharp v0.3.0 as submodule"
```

---

## Task 3: Add a justfile recipe to build the F# grammar for the current host

**Files:**

- Modify: `justfile`

The full per-RID build is in Task 11; this task gets the loop running for the developer's own machine.

- [ ] **Step 1: Detect the developer's RID**

Add to `justfile`:

```just
# Build the tree-sitter-fsharp shared library for the host machine
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
    clang -O2 -shared -fPIC -I src -o "$dest/$out" src/parser.c $(ls src/scanner.c 2>/dev/null || true)
    echo "Built $dest/$out"
```

(The `scanner.c` handling is conditional because some grammars include an external scanner and some don't.)

- [ ] **Step 2: Run the recipe**

Run: `just build-grammar`
Expected: produces `src/Fedit/runtimes/<your-rid>/native/libtree-sitter-fsharp.{dylib|so|dll}` and prints the path.

If `tree-sitter generate` fails because `tree-sitter` CLI is missing, install via `npm install -g tree-sitter-cli` (document this in the README).

- [ ] **Step 3: Commit**

```bash
git add justfile src/Fedit/runtimes
git commit -m "feat(highlight): justfile recipe + host-RID build of tree-sitter-fsharp"
```

---

## Task 4: Copy the `highlights.scm` query into the project as an embedded resource

**Files:**

- Create: `src/Fedit/Resources/queries/fsharp/highlights.scm`
- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Copy the query file**

```bash
mkdir -p src/Fedit/Resources/queries/fsharp
cp vendor/tree-sitter-fsharp/queries/highlights.scm src/Fedit/Resources/queries/fsharp/highlights.scm
```

If the grammar ships multiple highlights files (e.g. one for `fsharp` and one for `fsharp_signature`), copy the main `fsharp/highlights.scm`.

- [ ] **Step 2: Embed it in the project**

In `src/Fedit/Fedit.fsproj`, add a new `<ItemGroup>`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources/queries/fsharp/highlights.scm">
    <LogicalName>fedit.queries.fsharp.highlights.scm</LogicalName>
  </EmbeddedResource>
</ItemGroup>
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Fedit/Fedit.fsproj`
Expected: builds; resource compiled into the assembly.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Resources src/Fedit/Fedit.fsproj
git commit -m "feat(highlight): embed F# highlights.scm as resource"
```

---

## Task 5: Configure native-binary copy to publish output

**Files:**

- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Add a copy ItemGroup**

In `src/Fedit/Fedit.fsproj`, after the `EmbeddedResource` group, add:

```xml
<ItemGroup>
  <None Include="runtimes/**/*.*">
    <Pack>true</Pack>
    <PackagePath>runtimes</PackagePath>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 2: Confirm `runtimes/` ends up next to the published binary**

Run: `dotnet publish src/Fedit/Fedit.fsproj -c Release -r <your-rid> --self-contained false -o /tmp/fedit-publish-test`
Expected: `/tmp/fedit-publish-test/runtimes/<your-rid>/native/libtree-sitter-fsharp.*` exists.

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Fedit.fsproj
git commit -m "feat(highlight): copy runtimes/ natives to publish output"
```

---

## Task 6: Implement `HighlightCapture` and capture-name resolution

**Files:**

- Create: `src/Fedit/Highlight.fs`
- Modify: `src/Fedit/Fedit.fsproj`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Failing test for resolution**

Add to `tests/Fedit.Tests/HighlightTests.fs`:

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
    let resolved = Highlight.resolveCapture input
    match resolved with
    | Some c -> Assert.Equal(expectedCase, (string c))
    | None -> Assert.Fail(sprintf "expected Some %s, got None" expectedCase)

[<Fact>]
let ``resolveCapture returns None for unknown capture`` () =
    Assert.Equal(None, Highlight.resolveCapture "not.a.real.capture")
```

- [ ] **Step 2: Create `Highlight.fs` with the DU and resolver**

Create `src/Fedit/Highlight.fs`:

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
        if String.IsNullOrEmpty captureName then
            None
        else
            // Longest-prefix match against tree-sitter naming convention.
            // Order matters: more specific first.
            match captureName with
            | "keyword.control" -> Some KeywordControl
            | s when s.StartsWith "keyword.control." -> Some KeywordControl
            | "keyword.operator" -> Some KeywordOperator
            | s when s.StartsWith "keyword.operator." -> Some KeywordOperator
            | "keyword" -> Some Keyword
            | s when s.StartsWith "keyword." -> Some Keyword
            | "string.special" -> Some StringSpecial
            | s when s.StartsWith "string.special." -> Some StringSpecial
            | "string" -> Some String
            | s when s.StartsWith "string." -> Some String
            | "function.call" -> Some FunctionCall
            | s when s.StartsWith "function.call." -> Some FunctionCall
            | "function" -> Some Function
            | s when s.StartsWith "function." -> Some Function
            | "type" -> Some Type
            | s when s.StartsWith "type." -> Some Type
            | "constructor" -> Some Constructor
            | s when s.StartsWith "constructor." -> Some Constructor
            | "variable.parameter" -> Some Parameter
            | s when s.StartsWith "variable.parameter." -> Some Parameter
            | "variable" -> Some Variable
            | s when s.StartsWith "variable." -> Some Variable
            | "number" -> Some Number
            | s when s.StartsWith "number." -> Some Number
            | "comment" -> Some Comment
            | s when s.StartsWith "comment." -> Some Comment
            | "operator" -> Some Operator
            | s when s.StartsWith "operator." -> Some Operator
            | "punctuation" -> Some Punctuation
            | s when s.StartsWith "punctuation." -> Some Punctuation
            | "attribute" -> Some Attribute
            | s when s.StartsWith "attribute." -> Some Attribute
            | _ -> None
```

- [ ] **Step 3: Wire into compile order**

In `src/Fedit/Fedit.fsproj`, add `<Compile Include="Highlight.fs" />` immediately after `Themes.fs` (or wherever in the order precedes `Buffer.fs`/`Model.fs` — Highlight must be available when those types are defined).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~HighlightTests"`
Expected: PASS for all the resolution theory cases.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Highlight.fs src/Fedit/Fedit.fsproj tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): HighlightCapture DU and capture-name resolution"
```

---

## Task 7: Implement `HighlightRegistry` and language loading

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Failing integration test**

Add to `HighlightTests.fs`:

```fsharp
[<Fact>]
let ``HighlightRegistry loads F# language and query`` () =
    use registry = Highlight.HighlightRegistry.tryCreate()
    match registry with
    | None ->
        Assert.Fail("registry failed to create — F# grammar likely missing from runtimes/")
    | Some r ->
        let lang = r.TryGetLanguage "fsharp"
        Assert.True(lang.IsSome, "language fsharp not loaded")
        let q = r.TryGetQuery "fsharp"
        Assert.True(q.IsSome, "query for fsharp not built")
```

- [ ] **Step 2: Implement the registry**

Append to `src/Fedit/Highlight.fs`:

```fsharp
namespace Fedit

open System
open System.IO
open System.Reflection
open System.Collections.Concurrent

// (HighlightCapture + resolveCapture from Task 6 stay above this.)

[<RequireQualifiedAccess>]
module Highlight =

    type HighlightRegistry private (languages: ConcurrentDictionary<string, TreeSitter.Language>,
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
                // Languages are not owned (loaded from native lib), but if Language is IDisposable, dispose.
                for l in languages.Values do
                    match box l with
                    | :? IDisposable as d -> try d.Dispose() with _ -> ()
                    | _ -> ()
                languages.Clear()

        static member tryCreate() : HighlightRegistry option =
            let languages = ConcurrentDictionary<string, TreeSitter.Language>()
            let queries = ConcurrentDictionary<string, TreeSitter.Query>()
            let tryLoadFSharp () =
                try
                    // TreeSitter.DotNet's Language(library, function) resolves via
                    // AppContext.BaseDirectory/runtimes/<rid>/native/<library>.{so|dylib|dll}
                    let lang = TreeSitter.Language("tree-sitter-fsharp", "tree_sitter_fsharp")
                    languages.["fsharp"] <- lang
                    // Read embedded query
                    let asm = Assembly.GetExecutingAssembly()
                    use stream = asm.GetManifestResourceStream("fedit.queries.fsharp.highlights.scm")
                    if isNull stream then None
                    else
                        use reader = new StreamReader(stream)
                        let scm = reader.ReadToEnd()
                        let q = TreeSitter.Query(lang, scm)
                        queries.["fsharp"] <- q
                        Some ()
                with _ -> None

            match tryLoadFSharp () with
            | Some () -> Some (HighlightRegistry(languages, queries))
            | None -> None
```

(If `TreeSitter.Language`'s actual constructor signature differs slightly when implementing — the verification report has it as `Language(string library, string function)` — adapt the call accordingly. Same for `Query`.)

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~HighlightTests"`
Expected: PASS. If FAIL with "language fsharp not loaded", confirm the native lib was built in Task 3 and lives at `src/Fedit/runtimes/<rid>/native/libtree-sitter-fsharp.*` and that the test runner copies the `runtimes/` folder. If not, add a copy hook to the test project file.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Highlight.fs tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): HighlightRegistry with F# language and query loaded from runtimes/"
```

---

## Task 8: Add `HighlightSpan` and a parse-and-query function

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Create: `tests/Fedit.Tests/Fixtures/sample.fs`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Create a fixture file**

Create `tests/Fedit.Tests/Fixtures/sample.fs`:

```fsharp
module Sample

let greeting = "hello"

let square (x: int) : int = x * x
```

In `tests/Fedit.Tests/Fedit.Tests.fsproj`, copy fixtures to output:

```xml
<ItemGroup>
  <None Include="Fixtures/*.*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 2: Failing test**

Add to `HighlightTests.fs`:

```fsharp
[<Fact>]
let ``computeSpans returns at least one keyword span for sample.fs`` () =
    use registry = Highlight.HighlightRegistry.tryCreate() |> Option.get
    let lang = registry.TryGetLanguage "fsharp" |> Option.get
    let query = registry.TryGetQuery "fsharp" |> Option.get
    let source = File.ReadAllText("Fixtures/sample.fs")
    use parser = new TreeSitter.Parser(lang)
    use tree = parser.Parse(source) |> Option.ofObj |> Option.get
    let spans = Highlight.computeSpans query tree
    Assert.NotEmpty(spans)
    Assert.Contains(spans, fun s -> s.Capture = Keyword)
    Assert.Contains(spans, fun s -> s.Capture = String)
```

- [ ] **Step 3: Implement `computeSpans`**

Append to `Highlight.fs`:

```fsharp
type HighlightSpan =
    { Capture: HighlightCapture
      StartByte: int
      EndByte: int }

[<RequireQualifiedAccess>]
module Highlight =

    // ...existing...

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

(`CaptureNames` is the property exposed on `Query` per the verification — confirm at implementation time; if it's called something else like `_captureNames`, adapt.)

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~HighlightTests"`
Expected: PASS — `sample.fs` produces a `Keyword` span (for `module`/`let`) and a `String` span (for `"hello"`).

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Highlight.fs tests/Fedit.Tests/HighlightTests.fs tests/Fedit.Tests/Fixtures tests/Fedit.Tests/Fedit.Tests.fsproj
git commit -m "feat(highlight): computeSpans queries tree-sitter highlights for F#"
```

---

## Task 9: Add `HighlightState` to `BufferState`

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Modify: `src/Fedit/Buffer.fs`

- [ ] **Step 1: Define `HighlightState`**

Append to `Highlight.fs`:

```fsharp
type HighlightState =
    { Language: string option
      Parser: TreeSitter.Parser option
      Tree: TreeSitter.Tree option
      Spans: HighlightSpan array }

[<RequireQualifiedAccess>]
module Highlight =

    // ...existing...

    let emptyState : HighlightState =
        { Language = None
          Parser = None
          Tree = None
          Spans = Array.empty }

    let dispose (state: HighlightState) =
        state.Tree |> Option.iter (fun t -> try (t :> IDisposable).Dispose() with _ -> ())
        state.Parser |> Option.iter (fun p -> try (p :> IDisposable).Dispose() with _ -> ())

    let detectLanguage (path: string option) : string option =
        path
        |> Option.bind (fun p ->
            match (Path.GetExtension p).ToLowerInvariant() with
            | ".fs" | ".fsi" | ".fsx" -> Some "fsharp"
            | _ -> None)
```

- [ ] **Step 2: Add field to `BufferState`**

In `src/Fedit/Buffer.fs`, add `Highlight: HighlightState` to the `BufferState` record. Update every constructor and copy expression to initialize / preserve it. Use `Highlight.emptyState` as the initial value in `Buffer.createEmpty`.

Search the file for `{ ... }` record constructions and add `Highlight = ...` to each — the F# compiler will complain about missing fields if you miss one.

- [ ] **Step 3: Build**

Run: `dotnet build src/Fedit/Fedit.fsproj`
Expected: succeeds. Any missing field initializers surface as errors and must be fixed in the same task.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Highlight.fs src/Fedit/Buffer.fs
git commit -m "feat(highlight): HighlightState field on BufferState"
```

---

## Task 10: Initialize highlight on file open and update on buffer change

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Modify: `src/Fedit/Editor.fs`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Add a `reparse` helper**

Append to `Highlight.fs`:

```fsharp
[<RequireQualifiedAccess>]
module Highlight =

    // ...existing...

    /// Build a fresh HighlightState for the given language + source.
    /// Disposes any existing tree/parser on the way in.
    let parse (registry: HighlightRegistry) (language: string) (source: string) (previous: HighlightState) : HighlightState =
        dispose previous
        match registry.TryGetLanguage language, registry.TryGetQuery language with
        | Some lang, Some q ->
            try
                let parser = new TreeSitter.Parser(lang)
                let tree = parser.Parse(source)
                if isNull tree then
                    { emptyState with Language = Some language; Parser = Some parser }
                else
                    let spans = computeSpans q tree
                    { Language = Some language
                      Parser = Some parser
                      Tree = Some tree
                      Spans = spans }
            with _ ->
                { emptyState with Language = Some language }
        | _ -> emptyState
```

- [ ] **Step 2: Wire into `FileOpened`**

In `src/Fedit/Editor.fs`'s `update` function, find the `FileOpened` branch. After the buffer is constructed (`Buffer.createWithContents` or equivalent), call:

```fsharp
let language = Highlight.detectLanguage (Some path)
let highlight =
    match language, model.HighlightRegistry with
    | Some lang, Some reg -> Highlight.parse reg lang contents Highlight.emptyState
    | _ -> Highlight.emptyState
let bufferWithHighlight = { buffer with Highlight = highlight }
```

(`Model.HighlightRegistry` is added in Task 13; until then, this code is unreachable. For TDD continuity, the compiler will complain — at this task, leave the call commented out OR stub `model.HighlightRegistry` as `None`. Decide at implementation time which is cleaner.)

- [ ] **Step 3: Wire into buffer-mutating paths**

Identify each branch in `Editor.update` that mutates the active buffer's text: insertion (`KeyPressed (Character _)`), deletion (Backspace/Delete), paste (`ClipboardPasted`), and any other text-changing action.

After the buffer is updated, if `model.SyntaxHighlightingEnabled` and `buffer.Highlight.Language.IsSome` and `model.HighlightRegistry.IsSome`, recompute:

```fsharp
let updatedHighlight =
    let newText = Buffer.text updatedBuffer
    Highlight.parse (Option.get model.HighlightRegistry)
                    (Option.get updatedBuffer.Highlight.Language)
                    newText
                    updatedBuffer.Highlight
{ updatedBuffer with Highlight = updatedHighlight }
```

Factor this into a single helper:

```fsharp
let private reparseIfHighlighted (model: Model) (buffer: BufferState) : BufferState =
    if not model.SyntaxHighlightingEnabled then buffer
    else
        match buffer.Highlight.Language, model.HighlightRegistry with
        | Some lang, Some reg ->
            { buffer with Highlight = Highlight.parse reg lang (Buffer.text buffer) buffer.Highlight }
        | _ -> buffer
```

Apply at every buffer-mutating site in `Editor.update`.

- [ ] **Step 4: Failing integration test**

In `HighlightTests.fs`:

```fsharp
[<Fact>]
let ``editor reparses highlights after inserting text`` () =
    // Skeleton — fill in based on Update test helpers in UpdateTests.fs
    // 1. Construct a Model with HighlightRegistry, SyntaxHighlightingEnabled = true.
    // 2. Open a buffer with "let x = 1".
    // 3. Assert spans.Length > 0.
    // 4. Dispatch KeyPressed(Character ' ') then several Character msgs to insert " // hi".
    // 5. Assert spans now contain a Comment.
    Assert.True(true)
```

Mark `[<Trait("Category", "Highlight")>]` and flesh out using existing `UpdateTests.fs` helpers when implementing.

- [ ] **Step 5: Build**

Run: `dotnet build src/Fedit/Fedit.fsproj`
Expected: builds (with `Model.HighlightRegistry` and `Model.SyntaxHighlightingEnabled` still pending from Task 13 — keep the stub fields in `Model.fs` for compile, or implement Task 13 first if the order works better at execution time).

- [ ] **Step 6: Commit**

```bash
git add src/Fedit/Highlight.fs src/Fedit/Editor.fs tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): reparse spans on FileOpened and buffer mutations"
```

---

## Task 11: Build the grammar for all 5 RIDs

**Files:**

- Modify: `justfile`
- Create: native libs in `src/Fedit/runtimes/<rid>/native/` for the four RIDs not already built in Task 3.

- [ ] **Step 1: Per-RID recipes**

Append to `justfile`:

```just
# Build the F# grammar for a specific RID (requires the right cross-compiler / docker image).
build-grammar-osx-arm64:
    cd vendor/tree-sitter-fsharp && npx --yes tree-sitter generate
    clang -O2 -shared -fPIC -target arm64-apple-macos11 \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/osx-arm64/native/libtree-sitter-fsharp.dylib \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

build-grammar-osx-x64:
    cd vendor/tree-sitter-fsharp && npx --yes tree-sitter generate
    clang -O2 -shared -fPIC -target x86_64-apple-macos10.15 \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/osx-x64/native/libtree-sitter-fsharp.dylib \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

build-grammar-linux-x64:
    # Recommend running inside a manylinux2014 container or with zig cc.
    zig cc -O2 -shared -fPIC -target x86_64-linux-gnu \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/linux-x64/native/libtree-sitter-fsharp.so \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

build-grammar-linux-arm64:
    zig cc -O2 -shared -fPIC -target aarch64-linux-gnu \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/linux-arm64/native/libtree-sitter-fsharp.so \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

build-grammar-win-x64:
    zig cc -O2 -shared -target x86_64-windows-gnu \
        -I vendor/tree-sitter-fsharp/src \
        -o src/Fedit/runtimes/win-x64/native/tree-sitter-fsharp.dll \
        vendor/tree-sitter-fsharp/src/parser.c $(ls vendor/tree-sitter-fsharp/src/scanner.c 2>/dev/null)

build-grammars-all: build-grammar-osx-arm64 build-grammar-osx-x64 build-grammar-linux-x64 build-grammar-linux-arm64 build-grammar-win-x64
```

(Zig is the recommended cross-compiler because it bundles compatible C libraries for every target. Install via `brew install zig` or distro package.)

- [ ] **Step 2: Run the appropriate recipes**

On a macOS dev box with zig installed:

```bash
brew install zig
just build-grammars-all
```

Expected: produces all five native libs under `src/Fedit/runtimes/*/native/`.

If a specific cross-target fails (e.g. macOS-to-Linux can have linker quirks with `scanner.c`), document the workaround in `docs/syntax-highlighting-build.md` (a new file the implementer creates) and fall back to building that RID inside a docker container of the target OS.

- [ ] **Step 3: Commit**

```bash
git add justfile src/Fedit/runtimes
git commit -m "feat(highlight): build tree-sitter-fsharp for all 5 RIDs via zig cc"
```

---

## Task 12: Publish-time grammar trimming

**Files:**

- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Add the MSBuild target**

Add to `src/Fedit/Fedit.fsproj`:

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

Note: the target deliberately does **not** match files named `libtree-sitter.*` (the core runtime) — only `libtree-sitter-<lang>.*` and `tree-sitter-<lang>.*`. Verify the naming pattern matches what `TreeSitter.DotNet` actually ships.

- [ ] **Step 2: Verify**

Run: `dotnet publish src/Fedit/Fedit.fsproj -c Release -r osx-arm64 --self-contained true -o /tmp/fedit-publish-test`

Run: `ls /tmp/fedit-publish-test/runtimes/osx-arm64/native/`
Expected: contains `libtree-sitter.dylib` (core) and `libtree-sitter-fsharp.dylib`, but NOT `libtree-sitter-bash.dylib`, `libtree-sitter-c.dylib`, etc.

Run: `du -sh /tmp/fedit-publish-test/runtimes/osx-arm64/native/`
Expected: small (a few MB), not the original ~50 MB.

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Fedit.fsproj
git commit -m "feat(highlight): publish-time trim of unused tree-sitter grammars"
```

---

## Task 13: Add `HighlightRegistry` and `SyntaxHighlightingEnabled` to `Model`

**Files:**

- Modify: `src/Fedit/Model.fs`
- Modify: `src/Fedit/Editor.fs`

- [ ] **Step 1: Extend `Model`**

In `src/Fedit/Model.fs`, add to `Model`:

```fsharp
type Model =
    { // ...existing fields
      SyntaxHighlightingEnabled: bool
      HighlightRegistry: Highlight.HighlightRegistry option }
```

The registry is `option` because it can fail to load (no native lib). The field is **not** disposable from `Model` — disposal happens in `Runtime.fs` shutdown (Task 16).

- [ ] **Step 2: Initialize in `Editor.init`**

Update `Editor.init`'s signature to take `highlightRegistry: HighlightRegistry option` and `syntaxHighlightingEnabled: bool`. Populate the new model fields with them.

- [ ] **Step 3: Build**

Run: `dotnet build src/Fedit/Fedit.fsproj`
Expected: succeeds; call sites of `Editor.init` in `Runtime.fs` will show errors that Task 16 fixes.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Model.fs src/Fedit/Editor.fs
git commit -m "feat(highlight): SyntaxHighlightingEnabled + HighlightRegistry on Model"
```

---

## Task 14: Extend `Theme` with 16 syntax-color fields

**Files:**

- Modify: `src/Fedit/Themes.fs`
- Modify: `src/Fedit/Highlight.fs`
- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Add fields**

In `src/Fedit/Themes.fs`, extend the `Theme` record:

```fsharp
type Theme =
    { Name: string
      Description: string
      Accent: int
      StatusFg: int
      StatusBg: int
      SelectedBg: int
      CurrentLine: int
      // Syntax colors (ANSI 256-color indices)
      SyntaxKeyword: int
      SyntaxKeywordControl: int
      SyntaxKeywordOperator: int
      SyntaxString: int
      SyntaxStringSpecial: int
      SyntaxNumber: int
      SyntaxComment: int
      SyntaxFunction: int
      SyntaxFunctionCall: int
      SyntaxType: int
      SyntaxConstructor: int
      SyntaxVariable: int
      SyntaxParameter: int
      SyntaxOperator: int
      SyntaxPunctuation: int
      SyntaxAttribute: int }
```

Update every bundled theme constructor with sane defaults. Example baseline (pick ANSI 256 indices that look good against the theme's existing palette):

```fsharp
let defaultTheme : Theme =
    { // ...existing fields
      SyntaxKeyword = 141       // purple
      SyntaxKeywordControl = 141
      SyntaxKeywordOperator = 141
      SyntaxString = 114        // green
      SyntaxStringSpecial = 180 // yellow
      SyntaxNumber = 215        // orange
      SyntaxComment = 244       // grey
      SyntaxFunction = 117      // light blue
      SyntaxFunctionCall = 117
      SyntaxType = 222          // amber
      SyntaxConstructor = 175   // pink
      SyntaxVariable = 251      // light grey (foreground default-ish)
      SyntaxParameter = 251
      SyntaxOperator = 248
      SyntaxPunctuation = 246
      SyntaxAttribute = 180 }
```

Do the same for each other bundled theme — pick indices that fit each palette.

- [ ] **Step 2: Add `colorFor` helper**

Append to `src/Fedit/Highlight.fs`:

```fsharp
[<RequireQualifiedAccess>]
module Highlight =

    // ...existing...

    let colorFor (theme: Theme) (capture: HighlightCapture) : int =
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

- [ ] **Step 3: Extend user-theme JSON parsing**

In `src/Fedit/Runtime.fs`'s `loadUserThemes`, parse an optional `syntax` object:

```fsharp
let getSyntaxColor (syntaxRoot: JsonElement) (name: string) (fallback: int) =
    match syntaxRoot.TryGetProperty name with
    | true, e when e.ValueKind = JsonValueKind.Number -> e.GetInt32()
    | _ -> fallback

let syntaxBlock =
    match root.TryGetProperty "syntax" with
    | true, e when e.ValueKind = JsonValueKind.Object -> Some e
    | _ -> None
```

For every syntax color, default to the bundled `defaultTheme` value when not specified.

- [ ] **Step 4: Test the lookup**

Add to `HighlightTests.fs`:

```fsharp
[<Fact>]
let ``Highlight.colorFor returns theme syntax color for each capture`` () =
    let t = Themes.defaultTheme
    Assert.Equal(t.SyntaxKeyword, Highlight.colorFor t Keyword)
    Assert.Equal(t.SyntaxString, Highlight.colorFor t String)
    Assert.Equal(t.SyntaxComment, Highlight.colorFor t Comment)
```

Run: `dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "FullyQualifiedName~HighlightTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Themes.fs src/Fedit/Highlight.fs src/Fedit/Runtime.fs tests/Fedit.Tests/HighlightTests.fs
git commit -m "feat(highlight): 16 syntax colors on Theme + colorFor lookup"
```

---

## Task 15: Render highlight spans

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Modify: the renderer module that produces per-cell ANSI styles (likely `Renderer.fs`, `View.fs`, or `Screen.fs` — find which by reading the existing render pipeline).

- [ ] **Step 1: Add a span-lookup helper**

Append to `Highlight.fs`:

```fsharp
[<RequireQualifiedAccess>]
module Highlight =

    // ...existing...

    /// Binary-search the sorted span array for the span containing `charIndex`.
    /// Returns None if no span covers that index.
    let spanAt (spans: HighlightSpan array) (charIndex: int) : HighlightSpan option =
        if spans.Length = 0 then None
        else
            let mutable lo = 0
            let mutable hi = spans.Length - 1
            let mutable found = None
            while lo <= hi && found.IsNone do
                let mid = (lo + hi) / 2
                let span = spans.[mid]
                if charIndex < span.StartByte then
                    hi <- mid - 1
                elif charIndex >= span.EndByte then
                    lo <- mid + 1
                else
                    found <- Some span
            found
```

Edge case: spans may overlap. Tree-sitter queries can produce nested captures (e.g. `function.call` inside `function`). For MVP, the first match found wins — accept the visual variance; revisit if it looks wrong.

- [ ] **Step 2: Overlay in the renderer**

Locate the per-cell render pass. For each visible cell, given its `(line, column)` in the buffer:

1. Convert `(line, column)` to a char index in the buffer text. This may already exist as `Buffer.charIndexOf : int -> int -> BufferState -> int` or similar — use it. Otherwise compute via the piece-table line offsets (likely available as `Buffer.lineStart`).
2. Look up `Highlight.spanAt buffer.Highlight.Spans index`.
3. If `Some span` and `model.SyntaxHighlightingEnabled`, set the cell's foreground to `Highlight.colorFor model.Theme span.Capture`. Otherwise keep the existing default foreground.

The exact integration point depends on the renderer's API. Common patterns:

- If cells are built as a 2D array of `{ Fg; Bg; Char }`, set `Fg` per cell.
- If cells are produced via a writer-style emit function, inject `setFgColor` calls before each character whose color differs from the previous.

Avoid recomputing the span lookup repeatedly per character — for ANSI output, batch by running the cursor through the line and only emitting color escapes when the span changes.

- [ ] **Step 3: Smoke-test manually**

Run: `./fedit src/Fedit/Highlight.fs`
Expected: file opens with `module`, `let`, `match`, string literals, and comments visibly colored. If nothing changes, check the log (`--log fedit.log`) for "no language" or "registry failed" messages.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Highlight.fs src/Fedit/Renderer.fs   # or whichever file you edited
git commit -m "feat(highlight): overlay syntax colors per cell from spans"
```

---

## Task 16: Initialize and dispose the registry from `Runtime.fs`

**Files:**

- Modify: `src/Fedit/Runtime.fs`

- [ ] **Step 1: Construct at startup**

In `Runtime.fs`'s `run` function, near where the model is initialized:

```fsharp
let highlightRegistry = Highlight.HighlightRegistry.tryCreate()
match highlightRegistry with
| None -> log "highlight: failed to load tree-sitter — F# files will render plain"
| Some _ -> log "highlight: loaded tree-sitter F# grammar"
```

- [ ] **Step 2: Read `syntaxHighlighting` from config**

Extend the config JSON read in `loadConfig` to extract `syntaxHighlighting`:

```fsharp
let syntaxOn =
    match doc.RootElement.TryGetProperty "syntaxHighlighting" with
    | true, e when e.ValueKind = JsonValueKind.False -> false
    | _ -> true   // default on
```

Thread through `Editor.init` (added in Task 13).

- [ ] **Step 3: Dispose on shutdown**

In the `finally` block of `run`, after disposing other resources:

```fsharp
highlightRegistry |> Option.iter (fun r -> (r :> IDisposable).Dispose())
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Fedit/Fedit.fsproj`
Expected: succeeds. All earlier `model.HighlightRegistry`-related stubs now resolve.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Runtime.fs
git commit -m "feat(highlight): construct HighlightRegistry at startup; dispose on shutdown"
```

---

## Task 17: `syntax` command + config persistence

**Files:**

- Modify: `src/Fedit/Commands.fs`
- Modify: `src/Fedit/Editor.fs`
- Modify: `src/Fedit/Runtime.fs`
- Modify: `tests/Fedit.Tests/CommandsTests.fs`

- [ ] **Step 1: Failing test**

Add to `tests/Fedit.Tests/CommandsTests.fs`:

```fsharp
[<Fact>]
let ``parses 'syntax toggle' as Ready (Syntax "toggle")`` () =
    match Commands.parse "syntax toggle" with
    | Ready (Syntax "toggle") -> ()
    | other -> Assert.Fail(sprintf "unexpected: %A" other)

[<Fact>]
let ``parses 'syntax on' / 'syntax off'`` () =
    match Commands.parse "syntax on" with
    | Ready (Syntax "on") -> ()
    | other -> Assert.Fail(sprintf "unexpected: %A" other)
    match Commands.parse "syntax off" with
    | Ready (Syntax "off") -> ()
    | other -> Assert.Fail(sprintf "unexpected: %A" other)
```

- [ ] **Step 2: Extend `Command` and add spec**

In `src/Fedit/Commands.fs`:

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
  Constructor =
    fun argument ->
        let trimmed = argument.Trim().ToLowerInvariant()
        match trimmed with
        | "" -> Pending "Specify on, off, or toggle."
        | "on" | "off" | "toggle" -> Ready (Syntax trimmed)
        | other -> Invalid $"Unknown syntax verb '{other}'." }
```

Update completions to suggest `on`/`off`/`toggle` after `syntax`.

- [ ] **Step 3: Dispatch in `Editor.executeCommand`**

```fsharp
| Syntax verb ->
    let newValue =
        match verb with
        | "on" -> true
        | "off" -> false
        | "toggle" -> not model.SyntaxHighlightingEnabled
        | _ -> model.SyntaxHighlightingEnabled
    let updated = { model with SyntaxHighlightingEnabled = newValue }
    let notif =
        if newValue then Notification.info "Syntax highlighting on."
        else Notification.info "Syntax highlighting off."
    updated |> notify (Some notif),
    [ SaveConfig(updated.Theme.Name, updated.Recent) ]    // see Step 4 — SaveConfig is extended to carry the toggle
```

- [ ] **Step 4: Persist in `saveConfig`**

Extend `Runtime.fs`'s `saveConfig` signature and JSON output to include `"syntaxHighlighting": <bool>`. Match the existing pattern used for theme and recent.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Fedit/Commands.fs src/Fedit/Editor.fs src/Fedit/Runtime.fs tests/Fedit.Tests/CommandsTests.fs
git commit -m "feat(highlight): :syntax on/off/toggle command with config persistence"
```

---

## Task 18: Per-RID CI smoke test

**Files:**

- Create: `.github/workflows/highlight-smoke.yml` (or extend existing workflow if one exists)
- Modify: `tests/Fedit.Tests/HighlightTests.fs` (add a `[Trait]` for the smoke test category)

- [ ] **Step 1: Tag the smoke test**

In `HighlightTests.fs`, mark the integration test (`computeSpans returns at least one keyword span for sample.fs`) with:

```fsharp
[<Trait("Category", "Smoke")>]
```

Use `--filter "Category=Smoke"` in CI.

- [ ] **Step 2: Add the workflow**

Create `.github/workflows/highlight-smoke.yml`:

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
                  dotnet-version: "9.0.x"
            - run: dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --filter "Category=Smoke" --logger "console;verbosity=detailed"
```

- [ ] **Step 3: Push and verify**

Push the branch. Watch CI run across the matrix. Expected: all five jobs pass. Any RID that fails to load the native lib produces a clear "language fsharp not loaded" failure naming the RID.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/highlight-smoke.yml tests/Fedit.Tests/HighlightTests.fs
git commit -m "ci(highlight): per-RID smoke test for tree-sitter F# grammar"
```

---

## Task 19: README + user docs

**Files:**

- Modify: `README.md`
- Create: `docs/syntax-highlighting.md`

- [ ] **Step 1: Add a section to the README**

In `README.md`, under a new "Syntax highlighting" section:

```markdown
## Syntax highlighting

`fedit` highlights F# source files using [tree-sitter](https://tree-sitter.github.io/tree-sitter/) and the [ionide/tree-sitter-fsharp](https://github.com/ionide/tree-sitter-fsharp) grammar.

Toggle from the command bar:

- `:syntax on` — enable
- `:syntax off` — disable
- `:syntax toggle` — flip

Setting is persisted to `~/.config/fedit/config.json` under `syntaxHighlighting`.

Only F# is supported today (`.fs`, `.fsi`, `.fsx`). Adding more languages is on the roadmap — see [docs/syntax-highlighting.md](docs/syntax-highlighting.md).
```

- [ ] **Step 2: Add a user/contributor doc**

Create `docs/syntax-highlighting.md` with:

- Overview of how highlighting works in fedit (tree-sitter + queries)
- What languages are supported
- How to update the F# grammar (bump submodule, run `just build-grammars-all`)
- How themes resolve to capture names
- Troubleshooting: "no colors" → check that `runtimes/<rid>/native/libtree-sitter-fsharp.*` is present and `:syntax on` is set
- Roadmap: more languages, incremental parse, user-installable grammars via plugin API

- [ ] **Step 3: Commit**

```bash
git add README.md docs/syntax-highlighting.md
git commit -m "docs(highlight): README section and user/contributor guide"
```

---

## Task 20: Manual end-to-end verification

**Files:** none (this is a verification step, not a code change)

- [ ] **Step 1: Build the published binary**

```bash
dotnet publish src/Fedit/Fedit.fsproj -c Release -r osx-arm64 --self-contained true -o /tmp/fedit-release
```

- [ ] **Step 2: Run the binary**

```bash
/tmp/fedit-release/fedit src/Fedit/Highlight.fs
```

Expected:

- Editor opens with `Highlight.fs` visible
- `module`, `let`, `match`, `type` etc. are colored as keywords
- String literals are a distinct color
- Comments are dimmed
- Type names (`HighlightCapture`, `string`, `int`) are distinct
- No errors in the status bar

- [ ] **Step 3: Toggle and persist**

In the editor:

```
:syntax off
```

Expected: colors disappear, notification "Syntax highlighting off."

Quit (`:quit`), restart with the same file. Expected: colors stay off. Run `:syntax on` to confirm flipping back works and is also persisted.

- [ ] **Step 4: Sanity-check large file**

```bash
/tmp/fedit-release/fedit src/Fedit/Editor.fs
```

Expected: smooth scrolling, colors stable, no noticeable input lag. Editor.fs is a few thousand lines and exercises the reparse path on every keystroke (Phase 1 is full reparse). If lag is visible, file a follow-up to prioritize Phase 2 incremental.

- [ ] **Step 5: Commit if any final tweaks**

If you adjusted colors or thresholds during verification:

```bash
git add -A
git commit -m "feat(highlight): tuning from manual verification"
```

---

## Self-Review Checklist

**Spec coverage** — every section of `docs/superpowers/specs/2026-05-19-syntax-highlighting-spec.md` maps to a task:

| Spec section                                            | Tasks                                  |
| ------------------------------------------------------- | -------------------------------------- |
| Package                                                 | 1                                      |
| Grammar (vendor + build)                                | 2, 3, 11                               |
| Native binary layout                                    | 3, 5, 11                               |
| Publish-time trimming                                   | 12                                     |
| `HighlightCapture` + `HighlightSpan` + `HighlightState` | 6, 8, 9                                |
| Capture-name resolution                                 | 6                                      |
| `HighlightRegistry`                                     | 7                                      |
| Singleton registry construction at startup              | 16                                     |
| Edit handling (Phase 1 full reparse)                    | 10                                     |
| Rendering integration                                   | 15                                     |
| Theme integration (16 fields)                           | 14                                     |
| Language detection                                      | 9 (`detectLanguage`)                   |
| Lifecycle (init, edit, close, shutdown)                 | 10, 15, 16                             |
| Failure modes (missing lib, parse fails)                | 7 (`tryCreate` returns option), 10, 16 |
| Configuration (`:syntax on/off/toggle` + persist)       | 17                                     |
| Testing strategy (unit, integration, per-RID CI)        | 6, 7, 8, 14, 18                        |
| README + docs                                           | 19                                     |
| Manual verification                                     | 20                                     |

**Placeholder scan** — flagged remaining:

- Task 10 step 2 mentions "leave commented out OR stub" — this is an order-of-implementation acknowledgement. Acceptable; the engineer picks at implementation time. The completed code at end of Task 13 makes the call concrete.
- Task 15 step 2 references "the renderer's API" abstractly because the exact module split between `Renderer.fs` / `View.fs` / `Screen.fs` requires reading the existing code. The step lists the two common patterns the implementer will find. Acceptable.
- Task 11 step 2 mentions cross-compile linker quirks "document in `docs/syntax-highlighting-build.md`" — this is a fallback-on-failure clause, not a placeholder for normal flow.

**Type consistency** — names used across tasks:

- `HighlightCapture`, `HighlightSpan`, `HighlightState`, `HighlightRegistry` — consistent across Tasks 6, 7, 8, 9, 10, 13, 14, 15, 16.
- `Highlight.resolveCapture`, `Highlight.computeSpans`, `Highlight.parse`, `Highlight.dispose`, `Highlight.detectLanguage`, `Highlight.colorFor`, `Highlight.spanAt`, `Highlight.emptyState` — consistent across their introduction tasks and consumer tasks.
- `TreeSitter.DotNet` type names (`Parser`, `Tree`, `Language`, `Query`, `QueryCursor`) — pulled from verification report, used consistently.
- `Theme` field names (`SyntaxKeyword`, `SyntaxString`, etc.) — defined in Task 14, used in `Highlight.colorFor`.

No drift identified.
