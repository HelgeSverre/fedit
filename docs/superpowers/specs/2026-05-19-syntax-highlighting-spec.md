# Syntax Highlighting Design Spec

**Status:** Draft (revised 2026-05-21 to track code drift)
**Date:** 2026-05-19 (revised 2026-05-21)
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
- Targets `net10.0` to match the rest of the project (fedit moved off net9 in 2026-04).
- Risks (see verification doc): bus factor 1, issue #11 heap corruption under heavy batch parsing (not our use case), no source-link symbols. Mitigations baked into this plan.

### Grammar

- **`ionide/tree-sitter-fsharp` v0.3.0** (MIT, actively maintained as of 2026-04-27).
- Vendored as a git submodule at `vendor/tree-sitter-fsharp/`.
- Built per-RID by a justfile recipe (`just build-grammar` for host, `just build-grammar-<rid>` per target).
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

`HighlightState` lives on `Model`, keyed by buffer id — **not** on `BufferState`. The original draft put it on `BufferState`, but that would force `Highlight.fs` to compile before `Buffer.fs` (because the record would carry `TreeSitter.Parser`/`Tree`). The current source order has `Buffer.fs` near the top, and pushing TreeSitter types into the buffer record would also force every test that touches a `BufferState` to take a transitive dependency on the native library. Keeping highlight state outside the buffer leaves `Buffer.fs` and `BufferState` as pure data, and lets `Highlight.fs` slot in next to the other rendering-adjacent modules (after `Themes.fs`).

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
      StartByte: int       // .NET char index (TreeSitter.DotNet uses UTF-16 char indices)
      EndByte: int }

type HighlightState =
    { Language: string
      Parser: TreeSitter.Parser         // owned; disposed when state is replaced or dropped
      Tree: TreeSitter.Tree option      // owned; disposed when replaced
      Spans: HighlightSpan array }      // last computed; sorted by StartByte
```

`Model.HighlightStates : Map<int, HighlightState>` (key = `BufferState.Id`). Buffers without a recognised language have no entry; lookups return `None` and rendering falls back to plain text.

### Singleton language/query registry

A `HighlightRegistry` type holds:

- `Language` per language name (constructed once, never disposed during app life)
- `Query` per language name (constructed once, never disposed)
- `Parser` per buffer — NOT shared; created in `Highlight.parse` and stored on the per-buffer `HighlightState`

The registry is initialized at startup by `Runtime.run` and stashed on `Model.HighlightRegistry : HighlightRegistry option`. If the F# native library is not findable, `HighlightRegistry.tryCreate` returns `None`; F# buffers fall back to unhighlighted rendering and a one-time warning notification surfaces at startup.

### Edit handling

**Phase 1 (MVP):** on any buffer change, dispose old `Tree`, call `parser.Parse(newText, null)` for a fresh full parse, run highlights query over the whole tree, store sorted span array.

**Phase 2 (later):** add `EditRecord` to `Buffer.fs` operations; convert to `TreeSitter.Edit`; call `tree.Edit(edit)` then `parser.Parse(newText, tree)`; only re-query the changed range from `GetChangedRanges`. This plan covers Phase 1 only; Phase 2 is a follow-up plan.

### Rendering integration

The per-cell renderer is `View.fs`'s `Layout.renderEditor` — that's where rows from the active buffer are written into `Screen.Cells` with their `Style` (foreground `Color`, background `Color`, bold, inverted). `Renderer.fs` only does the ANSI diff/emit pass over an already-built `Screen`, so the highlight overlay belongs in `Layout.renderEditor`, not `Renderer.fs`.

`Highlight.spanAt spans charIndex` does a binary search over the sorted span array. The render loop already knows each row's start char index (`lineStarts[lineIndex]`); for each visible column it computes `lineStart + col` and looks up the span. When a span is present and `model.Config.SyntaxHighlightingEnabled`, the cell's foreground is set to `Highlight.colorFor theme span.Capture`. Selection and search-highlight overlays continue to win over syntax color (they overwrite the cell afterwards, as today).

### Theme integration

`Theme` (in `src/Fedit/Themes.fs`) gains 16 new fields of type `Color` (the existing `Default | Indexed | Rgb` DU from `Color.fs`):

```fsharp
type Theme =
    { Name: string
      Description: string
      Accent: Color
      StatusBg: Color
      SelectedBg: Color
      CurrentLine: Color
      StatusFg: Color
      // Syntax palette — one Color per HighlightCapture case.
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

Bundled themes pick `Color.indexed N` defaults from the ANSI 256 cube that fit each palette. `Color.Default` is a valid value meaning "no override — keep the surface foreground"; a theme that wants e.g. `Variable` not to take any syntax color can set it to `Default`.

User theme JSON (`~/.config/fedit/themes/*.json`) gains an optional `syntax` block:

```json
{
  "name": "ocean",
  "accent": "#1F6FEB",
  "syntax": {
    "keyword": "#FF8FB1",
    "string": "#73D49C",
    "comment": "#8A8F98"
  }
}
```

Each missing syntax field falls back to the corresponding `Themes.defaultTheme` value, so a user theme can override `accent`+`statusBg` without redefining all 16 syntax colors.

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
        match (Path.GetExtension p).ToLowerInvariant() with
        | ".fs" | ".fsi" | ".fsx" -> Some "fsharp"
        | _ -> None)
```

Called on `FileOpened`. Buffers without a path (the initial `scratch`) get no language and no highlighting.

### Lifecycle

| Event                        | Action                                                                                                                              |
| ---------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| Startup (`Runtime.run`)      | `HighlightRegistry.tryCreate()`. Stash on `Model.HighlightRegistry`. Log + warn if `None`.                                          |
| Buffer opened (`FileOpened`) | Detect language. If supported and registry exists, run `Highlight.parse`; insert `HighlightState` into `Model.HighlightStates`.     |
| Buffer mutated               | Reparse from scratch via `Highlight.parse`; previous state's `Tree` (and `Parser` if recreated) are disposed before the swap.       |
| Buffer closed                | Dispose `Parser` + `Tree`; remove entry from map.                                                                                   |
| App shutdown                 | Dispose every `HighlightState`, then dispose the registry. Languages/queries leak (cheap; OS reclaims).                             |

### Failure modes

- Native F# library missing → `HighlightRegistry.tryCreate` returns `None`; one-time warning notification at startup; buffers render without highlighting.
- Parse returns null → keep previous spans, log to `--log` if active.
- Query construction fails (bad `.scm`) → log; registry treats language as unavailable.

### Configuration

`Config` (the record in `src/Fedit/Model.fs`, owned by `Config.defaults` / `ConfigIO.load` / `ConfigIO.save`) gains:

```fsharp
type Config =
    { // ...existing fields
      SyntaxHighlightingEnabled: bool }
```

`Config.defaults` sets it to `true`. `ConfigIO.load` reads the optional `"syntaxHighlighting"` boolean (defaults to `true` when absent or malformed). `ConfigIO.save` writes it back alongside the existing keys.

A new built-in command:

```
syntax on
syntax off
syntax toggle
```

…flips `model.Config.SyntaxHighlightingEnabled` and emits `Effect.SaveConfig model.Config` (the effect already carries the whole `Config`).

### Performance budgets

- First parse of a 10k-line F# file: < 25 ms (full parse target).
- Reparse on keystroke (Phase 1 full reparse) on a 1k-line file: < 5 ms.
- Query iteration over full tree: < 5 ms for 1k lines, < 20 ms for 10k.
- Cell-level style lookup (binary search): O(log n), negligible.

If Phase 1 reparse exceeds 16 ms (one frame at 60 fps) on a reasonable-sized file, Phase 2 (incremental) moves up in priority.

### Testing strategy

- **Unit:** capture-name → `HighlightCapture` resolution, language detection, span-overlap math (`spanAt`).
- **Integration:** load F# grammar from native lib in repo, parse a fixture `.fs` file, assert specific spans (keyword at byte X, string at byte Y).
- **Per-RID CI smoke:** matrix job on osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64. Builds, loads F# grammar, parses, queries, asserts one keyword span. Catches missing/broken native libs.

### Open questions (defer)

- Phase 2 incremental parse triggers a `Buffer.fs` refactor to emit edit records. Worth doing inside this plan or after MVP ships?
- Caching strategy beyond MVP: per-viewport span cache vs. full-tree span array. Phase 1 stores full-tree; revisit when 100k-line files appear.
- Multi-language support: when we add C# / Markdown / JSON, do we extend `HighlightRegistry` with one entry per language, or unify with the plugin API and let plugins register grammars?
- Selection / search highlights overlaying syntax highlights — composition order needs care. Today the spec keeps selection/search winning over syntax (they overwrite the cell after the syntax pass).
