# Syntax Highlighting Design Spec

**Status:** Draft
**Date:** 2026-05-19
**Scope:** MVP syntax highlighting for fedit, F# only, via `TreeSitter.DotNet` + `ionide/tree-sitter-fsharp`.

## Goal

Render F# source files with token-level coloring (keywords, strings, comments, identifiers, numbers, types, operators). Highlight stays coherent with edits and large files. Architecture is forward-compatible with adding more languages later.

## Non-goals (MVP)

- Languages other than F#.
- Semantic highlighting via LSP (defer; tree-sitter syntax is enough).
- User-installable grammars at runtime (defer to a v2 grammar plugin story).
- Selection-aware overlay coloring (highlight under selection should fall back to defaults — out of scope).
- Per-grammar configuration UI.
- Incremental parsing via `Tree.Edit` on the first cut. **Phase 1 is full reparse on every buffer change.** Incremental lands in Phase 2 once edit-records are plumbed through `Buffer.fs`.

## Decisions locked in

### Package

- **`TreeSitter.DotNet` 1.3.0+** (MIT, NuGet), wrapping `libtree-sitter` v0.26.3.
- Verified: incremental `Tree.Edit`, `Parser.Parse(source, oldTree)`, queries with `QueryCursor.SetRange`, RID coverage for osx-arm64/x64, linux-x64/arm64, win-x64.
- Risks (see verification doc): bus factor 1, issue #11 heap corruption under heavy batch parsing (not our use case), no source-link symbols. Mitigations baked into this plan.

### Grammar

- **`ionide/tree-sitter-fsharp` v0.3.0** (MIT, actively maintained as of 2026-04-27).
- Vendored as a git submodule at `vendor/tree-sitter-fsharp/`.
- Built per-RID by a justfile recipe (`just build-grammars` / `just build-grammar-<rid>`).
- `highlights.scm` copied from the grammar's `queries/` directory into `src/Fedit/Resources/queries/fsharp/highlights.scm`. Embedded as a project resource so the published binary doesn't depend on the working directory.

### Native binary layout

```
src/Fedit/runtimes/<rid>/native/
  libtree-sitter-fsharp.dylib    (osx-arm64, osx-x64)
  libtree-sitter-fsharp.so       (linux-x64, linux-arm64)
  tree-sitter-fsharp.dll         (win-x64)
```

Files are checked into source control (small, ~200–500 KB each) so contributors don't need a C toolchain to build/run fedit. A `just build-grammar-<rid>` recipe regenerates them when the grammar is updated.

### Publish-time trimming

After `dotnet publish -r <rid>`, an MSBuild target deletes the 30 bundled grammars from `TreeSitter.DotNet` we don't ship. Keeps `libtree-sitter` (core) and `libtree-sitter-fsharp` only. Saves ~45 MB per RID.

### State model

A `HighlightState` lives on each `BufferState`:

```fsharp
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

type HighlightSpan =
    { Capture: HighlightCapture
      StartByte: int       // .NET char index (TreeSitter.DotNet uses char-indexed UTF-16)
      EndByte: int }

type HighlightState =
    { Language: string option           // "fsharp" | None
      Tree: TreeSitter.Tree option       // owned; disposed when replaced
      Spans: HighlightSpan array }       // last computed; sorted by StartByte
```

`HighlightState.Default = { Language = None; Tree = None; Spans = [||] }` for non-highlighted buffers.

### Singleton language/query registry

A `HighlightRegistry` module holds:

- `Language` per language name (constructed once, never disposed during app life)
- `Query` per language name (constructed once, never disposed)
- `Parser` per buffer — NOT shared, one parser per `BufferState`

The registry is initialized at startup. If the F# native library is not findable, `HighlightRegistry.tryGetLanguage "fsharp"` returns `None` and all F# buffers fall back to unhighlighted rendering with a one-time warning notification.

### Edit handling

**Phase 1 (MVP):** on any buffer change, dispose old `Tree`, call `parser.Parse(newText, null)` for a fresh full parse, run highlights query over the whole tree, store sorted span array.

**Phase 2 (later):** add `EditRecord` to `Buffer.fs` operations; convert to `TreeSitter.Edit`; call `tree.Edit(edit)` then `parser.Parse(newText, tree)`; only re-query the changed range from `GetChangedRanges`. This plan covers Phase 1 only; Phase 2 is a follow-up plan.

### Rendering integration

`Screen.fs` / `Renderer.fs` already converts model state to a grid of styled cells. Add a `Highlight.styleFor` lookup that, given a buffer and a (line, column) cell coordinate, returns an optional foreground color to overlay on the existing cell style.

The lookup uses binary search on the sorted span array to find the span containing the cell's char index. O(log n) per cell; cheap on a single viewport.

### Theme integration

`Theme` record gains 16 new fields (one per `HighlightCapture` case) of type `int` (ANSI color index, matching existing theme fields):

```fsharp
type Theme =
    { // ...existing fields
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

Bundled themes get tasteful defaults. User theme JSON gains optional `syntax` block; missing fields fall back to bundled defaults from the same base palette.

### Capture-name resolution

The query `.scm` file emits capture names like `keyword`, `keyword.control`, `string`, `string.special.path`, `function.call`, `type.builtin`. We resolve longest-prefix-first against our `HighlightCapture` DU:

```
keyword.control → KeywordControl
keyword.operator → KeywordOperator
keyword.* → Keyword
keyword → Keyword
string.special → StringSpecial
string.* → String
function.call → FunctionCall
function.* → Function
type.* → Type
type → Type
... etc
```

Unknown captures resolve to `None` (no styling).

### Language detection

```fsharp
let detectLanguage (path: string option) : string option =
    path
    |> Option.bind (fun p ->
        match Path.GetExtension(p).ToLowerInvariant() with
        | ".fs" | ".fsi" | ".fsx" -> Some "fsharp"
        | _ -> None)
```

Called on buffer open. Buffers without a path (`scratch`) get no language and no highlighting.

### Lifecycle

| Event                        | Action                                                                                                             |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| Startup                      | Register F# language + load `highlights.scm` from embedded resource.                                               |
| Buffer opened (`FileOpened`) | Detect language. If supported, create `Parser`, parse contents, run query, store spans in `BufferState.Highlight`. |
| Buffer edit (text change)    | Reparse from scratch; rebuild spans.                                                                               |
| Buffer closed                | Dispose `Parser` and `Tree`.                                                                                       |
| App shutdown                 | Dispose `Parser`/`Tree` for each buffer. Languages/queries leak (cheap; OS reclaims).                              |

### Failure modes

- Native F# library missing → `Highlight.tryInit "fsharp"` returns `None`; one-time warning notification; buffers render without highlighting.
- Parse returns null → keep previous spans; log to `--log` if active.
- Query construction fails (bad `.scm`) → log; buffer keeps empty spans.

### Configuration

A new boolean field on the model:

```fsharp
type Model =
    { // ...existing
      SyntaxHighlightingEnabled: bool }
```

Default `true`. Add a new command:

```
syntax on
syntax off
syntax toggle
```

Persisted to `~/.config/fedit/config.json` alongside theme and recent.

### Performance budgets

- First parse of a 10k-line F# file: < 25 ms (full parse target).
- Reparse on keystroke (Phase 1 full reparse) on a 1k-line file: < 5 ms.
- Query iteration over full tree: < 5 ms for 1k lines, < 20 ms for 10k.
- Cell-level style lookup (binary search): O(log n), negligible.

If Phase 1 reparse exceeds 16 ms (one frame at 60 fps) on a reasonable-sized file, Phase 2 (incremental) moves up in priority.

### Testing strategy

- **Unit:** capture-name → `HighlightCapture` resolution, language detection, span-overlap math.
- **Integration:** load F# grammar from native lib in repo, parse a fixture `.fs` file, assert specific spans (keyword at byte X, string at byte Y).
- **Per-RID CI smoke:** matrix job on osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64. Builds, loads F# grammar, parses, queries, asserts one keyword span. Catches missing/broken native libs.

### Open questions (defer)

- Phase 2 incremental parse triggers a `Buffer.fs` refactor to emit edit records. Worth doing inside this plan or after MVP ships?
- Caching strategy beyond MVP: per-viewport span cache vs. full-tree span array. Phase 1 stores full-tree; revisit when 100k-line files appear.
- Multi-language support: when we add C# / Markdown / JSON, do we extend `HighlightRegistry` with one entry per language, or unify with the plugin API and let plugins register grammars?
- Selection / search highlights overlaying syntax highlights — composition order needs care.
