# Syntax Highlighting Approaches for fedit

**Status:** Research synthesis
**Date:** 2026-05-19
**Audience:** Decision input — three viable approaches with concrete tradeoffs, intended to feed a `/swarm` decision pass

## Objective

Survey how terminal and desktop editors implement syntax highlighting, then recommend 2–3 viable paths for fedit (F# / .NET 9, Elm-style MVU, piece-table buffer, ANSI renderer). Per-family findings are in companion files:

- `_treesitter-family.md` — Helix, Zed, Neovim TS, tree-sitter core, .NET binding survey
- `_regex-editors.md` — Vim, Neovim legacy `:syntax`, nano, micro, Kakoune
- `_emacs-textmate-handwritten.md` — Emacs font-lock, TextMate / vscode-textmate, kilo-style tokenizers

This document distills the cross-family findings and the implications for fedit's architecture.

---

## Comparison matrix

| Approach                           | Used by                                          | Parser model                                         | Grammar source                                      | Incremental edit story                                       | Multi-lang scaling               | .NET ready?                                | MVU fit                           | Native deps                                 |
| ---------------------------------- | ------------------------------------------------ | ---------------------------------------------------- | --------------------------------------------------- | ------------------------------------------------------------ | -------------------------------- | ------------------------------------------ | --------------------------------- | ------------------------------------------- |
| **Tree-sitter**                    | Helix, Zed, Neovim TS                            | Incremental GLR, queries (`.scm`)                    | Bundled NuGet (28+ grammars) or per-machine compile | True incremental via `ts_tree_edit` + `parse(old_tree, ...)` | Excellent                        | `TreeSitter.DotNet` v1.3 (Jan 2026, MIT)   | Good — tree lives in model        | libtree-sitter + per-grammar `.so`/`.dylib` |
| **TextMate grammars**              | VS Code, Sublime, Atom, GitHub                   | Per-line regex rule stack                            | `.tmLanguage` JSON, ~thousands available            | Per-line state token cached, dirty-line forward              | Excellent (largest ecosystem)    | `TextMateSharp` (MIT, NuGet)               | Good — `tokenizeLine` is pure     | Native Oniguruma                            |
| **Regex rules (micro-style YAML)** | micro, Kakoune (variant), nano, Vim `:syntax`    | Single-line regex + region nesting                   | YAML rules per language, ~150 community grammars    | Per-line state cache, forward invalidation                   | Good for ~10 langs; tails off    | Pure .NET regex works (RE2 subset)         | Excellent — pure F# data          | None                                        |
| **Hand-written tokenizers**        | kilo, dte (partial), JetBrains "fallback lexers" | Bespoke state machine in host language               | None — write per language                           | Per-line lex-state fixpoint                                  | Painful past 3–4 langs           | n/a — it's your code                       | Best — pure F# function           | None                                        |
| **Emacs font-lock**                | Emacs (non-treesit modes)                        | Regex + syntax-table state machine + Elisp callbacks | Per-mode Elisp files                                | `jit-lock` lazy + `font-lock-multiline` text properties      | Excellent in Emacs, not portable | n/a — GPL Elisp                            | Poor — assumes mutable text props | n/a                                         |
| **LSP semantic tokens**            | Zed, VS Code (overlay)                           | Server-driven, off-the-shelf per language            | LSP server provides                                 | Server emits on change, edit deltas                          | Excellent                        | Possible via `LanguageServerProtocol` libs | Async fits MVU effects            | LSP server binaries                         |

The bottom three rows are reference points; the top three are the actual candidate paths for fedit.

---

## Family findings (1-paragraph each)

### Tree-sitter family

Helix, Zed, and Neovim's TS mode converged on the same shape: a small native runtime (`libtree-sitter`) loads per-language grammars as dynamic libraries, parses incrementally, and runs `.scm` queries to project tokens onto theme scopes. Helix bundles grammars in a `runtime/` directory; Zed ships them via extensions; Neovim compiles them on the user's machine. Incremental parsing is two C calls — `ts_tree_edit` to rewrite node positions, then `ts_parser_parse(old_tree, new_input)`. Reparse cost is proportional to the damaged region, sub-millisecond on typical edits and single-digit milliseconds for 10k-line full parses. Helix learned two hard lessons: adopt a **500 ms parse timeout** for safety, and don't do O(N²) injection bookkeeping (their hashtable PR turned a Linux-kernel header from "unusable" to fine). Queries map `(node, capture-name)` → theme color; the renderer asks "what captures apply to this byte range" rather than walking the whole tree per line. WASM grammars are now first-class via `tree-sitter build --wasm`.

### Regex-rules family

Vim, Neovim legacy `:syntax`, nano, micro, and Kakoune all share a pattern: per-language declarative rules combining single-line regex matches with `region` / `begin`-`end` constructs for multi-line state. Vim uses Ex script (`syntax keyword/match/region`), nano uses POSIX-ERE in `.nanorc`, micro uses YAML with dotted scopes and **nested rules inside regions**, Kakoune uses a composable `add-highlighter` tree where any consumer (LSP, tree-sitter plugin) can write into a `ranges` option using the same rendering path. Multi-line state is reconstructed locally (`:syn sync`) or by caching a per-line state token and invalidating forward from the edit. Vim's pain points are well-documented: `synmaxcol` truncation on long lines, `redrawtime` disabling highlighting wholesale, jump-past-10k-lines desync — all arguments for designing cleaner state caching than `:syn sync` heuristics. **Micro's YAML format is the cleanest port target**: declarative, RE2-shaped (already a subset of .NET regex), scope-based theming via `color-link`, and ~150 existing grammars under permissive licenses.

### Hand-written / Emacs / TextMate

Emacs font-lock is a three-stage lazy pipeline (`jit-lock` → syntax table → regex keywords) with mode-specific Elisp callbacks for languages like C++ where regex isn't enough. Powerful, but GPL-3 Elisp and assumes mutable text properties — **not portable to MVU**. TextMate grammars are the de-facto industry format (VS Code, Sublime, Atom, GitHub Linguist all consume them) with a per-line `tokenizeLine(line, ruleStack) → (tokens, newStack)` algorithm that maps perfectly onto MVU. `TextMateSharp` is the MIT-licensed .NET port used by AvaloniaEdit, ships `TextMateSharp.Grammars` with F# + ~40 languages + ~20 themes via NuGet. The catch: TextMate grammars depend on Oniguruma's specific regex dialect (possessive quantifiers, `\G` anchors, the absent operator `(?~...)`) which .NET's `System.Text.RegularExpressions` lacks — a self-built engine fails on real grammars; `TextMateSharp` solves this by P/Invoke to native Oniguruma. The fallback for the simplest case is a **hand-written tokenizer**: kilo's full highlighting is ~150 lines of C; an F# tokenizer for F# itself is realistically **400–700 lines of F#**, has the same `tokenize : LexState -> string -> Token list * LexState` shape that vscode-textmate exposes, and is trivially MVU-friendly.

---

## Cross-cutting answers

### Q: What is the cheapest viable MVP for one or two languages?

**A hand-written F# tokenizer.** ~500 LOC, no dependencies, no native binaries, no extra compile pipeline. The signature `tokenize : LexState -> string -> Token list * LexState` matches vscode-textmate's `tokenizeLine` and threads cleanly through MVU. It also avoids the question of "which grammar source do we trust" entirely — for F# we know the language definition first-hand. A second language (Markdown, JSON, F# scripts) is another 100–300 LOC each.

### Q: What scales to "dozens of languages, 100k-line files"?

**Tree-sitter.** It's the only approach with true incremental parsing — per-edit cost proportional to damaged region, not file size. Helix and Zed already prove this works at scale. The .NET binding story turned a corner with `TreeSitter.DotNet` 1.3 (Jan 2026): MIT-licensed, every RID we ship to (osx-arm64/x64, linux-x64/arm64, win-x64), 28+ grammars bundled including F#, queries supported. The remaining question is "is the incremental `Tree.Edit` API actually exposed" — needs a 30-minute smoke test before committing.

TextMate-via-`TextMateSharp` is the runner-up: 40+ languages out of the box, but the parser model is regex-per-line, not incremental in the tree-sitter sense — it scales by caching per-line state tokens rather than sharing parse-tree subtrees. Fine up to ~50k lines, less elegant beyond.

### Q: How do these editors stay coherent with the edit stream?

Three patterns, all of which are easy to express in MVU:

| Pattern                                        | Used by                                    | How                                                                                                                                          |
| ---------------------------------------------- | ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------- |
| **Per-line state cache, forward invalidation** | Vim/Neovim legacy, micro, TextMate engines | Cache `LexState` at every line boundary. On edit at line N, re-lex from N forward until the emitted state matches the cached one, then stop. |
| **Incremental parse-tree edit**                | Tree-sitter (Helix, Zed, Neovim TS)        | Tree stored in model. Edit → `ts_tree_edit(...)` to shift node positions, then `parse(old_tree, new_input)` reuses unchanged subtrees.       |
| **Lazy on demand**                             | Emacs `jit-lock`, Kakoune                  | Highlight is computed lazily as the renderer pulls visible regions. Edits invalidate; the renderer requests fresh data.                      |

For fedit's MVU loop, the first two are equally clean: the cache (or tree) lives in `Model`, edits produce a new cache (or tree), and rendering reads it. Lazy-on-demand is also fine but harder to reason about purity-wise — we'd push it into a memoization layer.

### Q: Does highlight live in the buffer, the view, or async?

Consensus across editors: **a separate layer**, with the data flow:

1. Buffer change → highlighter recomputes (incrementally) → produces spans
2. Spans are addressed by `(byte_range, scope_or_token)` and stored alongside the buffer
3. Renderer reads spans for the visible viewport, looks up theme color per scope, writes ANSI

In MVU terms: highlighter is a function `Buffer -> HighlightState`, stored in `Model.Highlight`. Edit messages update both the buffer **and** the highlight cache (incrementally). The renderer consumes both. This is the pattern Helix uses (`Syntax` object alongside the rope) and what Kakoune's `ranges` option formalizes.

Async is optional. For tree-sitter, parses under ~5 ms are fine on the UI thread; longer ones (or first parses of huge files) should be effects dispatched off the main loop with results posted as `Msg`s. VS Code mandates off-thread tokenization for TextMate because Oniguruma's catastrophic backtracking risk; for fedit's MVU we'd start sync and add an effect only if we measure pain.

### Q: Licensing concerns?

Mostly clean.

- **Tree-sitter core:** MIT.
- **Most tree-sitter grammars** (including `ionide/tree-sitter-fsharp`, C#, Rust, etc.): MIT or Apache-2.0. A few outliers (some R bindings) are GPL-3; the `tree-sitter-language-pack` aggregator explicitly excludes copyleft. **No GPL trap** if we curate grammars or rely on `TreeSitter.DotNet`'s bundled set.
- **TextMate grammars:** mostly MIT (vscode-textmate, the bundled VS Code grammars). A handful of upstream grammars carry their own licenses — needs per-grammar `NOTICE` curation if we redistribute.
- **TextMateSharp:** MIT, includes Eclipse contributions (EPL also possible — check before bundling). Native Oniguruma is BSD-2.
- **micro YAML grammars:** MIT bulk of the corpus, individual files vary — auditable.
- **nano `.nanorc`:** GPL-3 (nano itself). Grammars are typically standalone files and often MIT, but the format is so trivial we'd write our own.
- **Emacs font-lock:** GPL-3 Elisp — **don't port**.

For redistribution: ship a `NOTICES.md` or `THIRD_PARTY.md` aggregating grammar licenses. Standard practice.

### Q: Smallest credible path to "real F# highlighting", ideally without native deps?

**Hand-written F# tokenizer.** No native deps, no NuGet additions, no pre-build steps. ~500 LOC covering keywords (~100 idents), operators, identifiers (including `` `quoted` ``), numeric literals with the F# suffix zoo (`L`/`UL`/`lf`/`m`/`0x...`), char and string literals (regular, verbatim `@"..."`, triple-quoted `"""..."""`), nestable block comments `(* ... *)`, line comments, attribute brackets. Pair with F# Compiler Services' `FSharpSourceTokenizer` (line-oriented, `int64` state — exactly the same shape as our tokenizer) **if** we want to outsource the spec — but that drags FCS into the binary (~30 MB).

Without FCS: ~500 LOC, weekend-scale. With FCS: ~50 LOC of glue + a heavyweight dependency.

---

## Fit for fedit: three viable paths

Three options, ordered by ambition. Any one is shippable; the question is what we want fedit to grow into.

### Option A — Hand-written F# tokenizer (MVP, conservative)

**Shape:** Add `Highlight.fs` between `Buffer.fs` and `Model.fs`. Define:

```fsharp
type Token =
    | Keyword | Identifier | Number | StringLit | CharLit
    | Comment | Operator | Punctuation | Whitespace | Attribute

type LexState =
    | Normal
    | InBlockComment of depth:int
    | InVerbatimString
    | InTripleString

val tokenize : LexState -> string -> (Token * int * int) list * LexState
//                                     ^token  ^start ^len
```

Model holds `LineStates: int -> LexState` (lex-state at each line start) and `LineTokens: int -> (Token * int * int) list` (tokens per rendered line). Edits invalidate from the changed line forward; re-lex until the emitted `LexState` matches the cached one, then stop.

Renderer maps `Token -> AnsiStyle` from the active theme. Two new theme fields (one foreground color per token kind).

**Cost:** ~500 LOC + a `Token -> AnsiStyle` mapping in `Themes.fs`. No new NuGet dependencies. No native binaries.

**Tradeoffs:**

- ✅ Best MVU fit — pure function in, pure data out
- ✅ Zero binary bloat
- ✅ No grammar-license question
- ✅ Robust on partial / broken code (no backtracking)
- ❌ F# only — adding Markdown or JSON is another ~200 LOC each
- ❌ No future ecosystem leverage; every language is a manual port
- ❌ Doesn't scale past 3–4 languages without restructuring

**When this is the right answer:** if fedit's identity is "a small F# editor for F# people", hand-written is correct and a tree-sitter dep is overkill. If we later want more, the `tokenize` signature is the same one TextMate and tree-sitter expose, so a swap is mechanical.

### Option B — TextMate grammars via `TextMateSharp` (multi-language, low risk)

**Shape:** Add NuGet refs for `TextMateSharp` (MIT) and `TextMateSharp.Grammars`. Implement an adapter:

```fsharp
val tokenizeWith :
    grammar: IGrammar ->
    LexState -> string ->
    (TextMateScope list * int * int) list * LexState
```

where `LexState` wraps `TextMateSharp`'s `StateStack`. Highlight cache stores `StateStack` per line; per-edit invalidation is the same forward-from-dirty-line pattern. Theme is the active TextMate theme JSON; renderer resolves scopes to ANSI colors via the theme's scope trie. F# grammar ships in `TextMateSharp.Grammars`.

**Cost:** Two NuGet packages (~5–10 MB native Oniguruma + grammars). Adapter: ~200 LOC. Theme integration: ~100 LOC (parse `.json-tmTheme`, populate fedit's theme record from VS Code-style themes).

**Tradeoffs:**

- ✅ 40+ languages out of the box (F#, C#, TS, Python, Rust, Markdown, JSON, YAML, TOML, ...)
- ✅ 20+ themes that match what users see in VS Code
- ✅ Used by AvaloniaEdit in production — proven on .NET
- ✅ Familiar grammar format for plugin authors (`.tmLanguage.json`)
- ⚠️ Native Oniguruma binary required — single-file publish needs RID-specific bundling
- ⚠️ Per-line regex risks catastrophic backtracking; mitigate with `TextMateSharp`'s timeout knobs + off-UI-thread mode
- ⚠️ Grammars are uneven quality; F# grammar from VS Code lags ionide's tree-sitter version
- ❌ Not truly incremental at the tree level — per-line state cache only

**When this is the right answer:** if we want "looks like VS Code" out of the box with minimal engineering. Fastest path to "supports the long tail of languages users actually open."

### Option C — Tree-sitter via `TreeSitter.DotNet` (ambitious, future-proof)

**Shape:** Add `TreeSitter.DotNet` NuGet. Define:

```fsharp
type SyntaxState =
    { Parser: TreeSitter.Parser
      Tree: TreeSitter.Tree option
      Language: TreeSitter.Language
      HighlightsQuery: TreeSitter.Query }

val applyEdit : EditRecord -> SyntaxState -> SyntaxState
val highlightRange : byteStart:int -> byteEnd:int -> SyntaxState -> Capture list
```

Model holds `SyntaxState option`. Edits translate to `TSInputEdit` (built from the piece-table's edit record) → `tree.Edit(...)` then `parser.Parse(tree, newText)`. The renderer asks for captures over the visible byte range; theme maps capture names (`keyword.control`, `string.special.path`, ...) to ANSI colors.

**Cost:** One NuGet (~26 MB pre-trim, ~3–6 MB after RID-specific publish trimming). Adapter + caching: ~400 LOC. Theme integration: ~150 LOC (resolve capture-name prefixes to colors via a small trie). One smoke test up front to confirm `Tree.Edit` is exposed by `TreeSitter.DotNet`.

**Tradeoffs:**

- ✅ True incremental parsing — sub-millisecond per keystroke on edits
- ✅ Scales to 100k-line files (Helix proves this; only injection-heavy pathologies hurt)
- ✅ 28+ grammars bundled including F# (ionide's grammar)
- ✅ Queries (`.scm`) are the lingua franca of modern editors — directly reusable from Helix/Zed/Neovim configs
- ✅ Future-proof: matches the industry direction
- ⚠️ ~3–6 MB of native binaries added to fedit's self-contained publish
- ⚠️ Greenfield on .NET — no production editor publicly ships this stack today; we'd be early
- ⚠️ `TreeSitter.DotNet` incremental API needs a 30-min smoke test before commit
- ⚠️ ionide/tree-sitter-fsharp historically lags the F# compiler on bleeding-edge features
- ❌ More moving parts than (A) or (B); higher debugging surface area

**When this is the right answer:** if we believe fedit grows into a multi-language editor that users open large files in. Also the right answer if we want to align with how the rest of the editor world is moving.

---

## Recommendation framework (for the swarm)

The decision turns on **scope ambition**, not technical risk — all three options work.

| If fedit's north star is...                                    | Pick             |
| -------------------------------------------------------------- | ---------------- |
| "A polished F# editor for F# people"                           | A (hand-written) |
| "Visually identical to VS Code on the long tail of files"      | B (TextMate)     |
| "A real general-purpose editor; F# is just the first language" | C (tree-sitter)  |

A useful **two-step plan** if undecided: start with (A) for F# only, ship in days, and treat the `tokenize : LexState -> string -> Token list * LexState` signature as a contract. When multi-language pressure arrives, slot a `TextMateSharp` or `TreeSitter.DotNet` adapter in behind that same signature — the MVU loop and renderer don't change. This makes (A) a strict subset of (B) and (C) rather than a sunk cost.

Open questions for swarm to resolve:

1. **Scope target.** Is fedit "F# editor" or "general editor that happens to be in F#"? This is the single most important input.
2. **Binary-size budget.** Self-contained publish is currently small; (B) adds ~5 MB, (C) adds ~3–6 MB per RID. Acceptable?
3. **Plugin authoring story.** If our plugin API (separate spec) ever supports "register a syntax", which format do we want plugins to write in — F# code, YAML, TextMate JSON, or tree-sitter queries?
4. **`TreeSitter.DotNet` smoke test.** Before committing to (C), confirm `Tree.Edit` and `Parser.Parse(oldTree, ...)` are exposed. If not, a 1-week detour to upstream the binding gap, or fall back to (B).
5. **Theme story.** Today fedit themes are 5 fields. (A) needs ~10 more (per token kind). (B) ingests `.json-tmTheme`. (C) maps capture names. All workable; pick the one that matches the format we want users to author themes in.

---

## Sources

Companion reports with full citations:

- `docs/superpowers/research/_treesitter-family.md`
- `docs/superpowers/research/_regex-editors.md`
- `docs/superpowers/research/_emacs-textmate-handwritten.md`

Headline links: [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet), [TextMateSharp](https://github.com/danipen/TextMateSharp), [vscode-textmate](https://github.com/microsoft/vscode-textmate), [tree-sitter docs](https://tree-sitter.github.io/tree-sitter/), [Helix tree-sitter integration](https://deepwiki.com/helix-editor/helix/4.2-tree-sitter-integration), [VS Code syntax highlighting optimizations](https://code.visualstudio.com/blogs/2017/02/08/syntax-highlighting-optimizations), [Vim `syntax.txt`](https://vimhelp.org/syntax.txt.html), [micro colors.md](https://github.com/zyedidia/micro/blob/master/runtime/help/colors.md), [Kakoune highlighters](https://github.com/mawww/kakoune/blob/master/doc/pages/highlighters.asciidoc), [kilo source](https://github.com/antirez/kilo).
