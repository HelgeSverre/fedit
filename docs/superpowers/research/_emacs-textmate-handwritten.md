# Non‑tree‑sitter / non‑regex‑rules Highlighters: Three Approaches

Research for fedit (F# / .NET 9, MVU, piece‑table, custom ANSI renderer).
Question: how do we ship credible F# syntax highlighting *without* embedding
tree‑sitter or hand‑rolling a generic regex‑rules engine?

---

## Emacs font-lock

Font‑lock is the original "industrial" approach to highlighting and is still
the workhorse for most non‑treesit modes. It is a three‑stage pipeline run
per visible region by `jit-lock` (lazy, demand‑driven):

1. **Parser‑based** (`treesit-font-lock-rules`, Emacs 29+) — optional, runs first.
2. **Syntactic fontification** — uses the buffer's *syntax table* (a per‑char
   class table: `"` opens strings, `(` is open paren, etc.) to find strings
   and comments. This is not regex; it is a small character‑class state
   machine in C, parameterised by the table. 🟢
3. **Search‑based fontification** — `font-lock-keywords`, a list of
   `(MATCHER . HIGHLIGHT)` rules where MATCHER is a regex *or an arbitrary
   Elisp function*. Anchored sub‑matchers let one outer match drive several
   inner matches (e.g. find a `defun`, then highlight each arg).

`font-lock-defaults` is the per‑mode tuple wiring keywords, "keywords only"
flag, case folding, syntax‑table overrides, and arbitrary buffer‑local
variables. Multi‑line constructs are handled by tagging regions with the
`font-lock-multiline` text property so edits inside force a full re‑scan, or
by `jit-lock-contextually` deferring re‑highlight after the edit settles.

`cc-mode` (C/C++/Java/ObjC) is the canonical "regex is not enough" mode: it
ships a hand‑written semi‑parser (`c-forward-sexp`, syntactic analysis of
brace nesting, label/case detection) that produces fontification decisions
the regex layer cannot. Other heavyweight modes do the same via custom
MATCHER functions. 🟡

**Effort to add a language:** small for trivial modes (~100 lines of
keywords + a syntax table), unbounded for cc‑mode‑class languages. Existing
modes are GPL‑3 Elisp — not directly portable. Performance is "good enough
because lazy"; correctness on partial code is excellent (the syntax table
is local and self‑healing). Doesn't map cleanly to a pure MVU function
because the design assumes mutable buffers + text properties.

Sources:
- [Font Lock Mode](https://www.gnu.org/software/emacs/manual/html_node/elisp/Font-Lock-Mode.html)
- [Search‑based Fontification](https://www.gnu.org/software/emacs/manual/html_node/elisp/Search_002dbased-Fontification.html)
- [Multiline Font Lock](https://www.gnu.org/software/emacs/manual/html_node/elisp/Multiline-Font-Lock.html)
- [Parser‑based Font Lock](https://www.gnu.org/software/emacs/manual/html_node/elisp/Parser_002dbased-Font-Lock.html)

---

## TextMate grammars (and vscode-textmate)

TextMate grammars are the de‑facto industry standard: TextMate, Sublime,
Atom, VS Code, GitHub Linguist all consume them. A grammar is a plist or
JSON document containing a `patterns` list of three rule shapes:

- `{ match, name, captures }` — single regex, single scope.
- `{ begin, end, beginCaptures, endCaptures, patterns }` — push a stack
  frame between two regexes; nested `patterns` apply inside.
- `{ include }` — reference into a `repository` of named rule sets, allowing
  recursion and grammar embedding (HTML‑in‑PHP, etc.).

Tokens are tagged with dotted **scope strings** (`source.fsharp
keyword.control.let`). Themes are a separate artefact mapping scope prefixes
to colours via a trie. Decoupling grammar and theme is the killer feature.

The regex dialect is **Oniguruma** (or its fork **Onigmo**) — not PCRE, not
.NET. It supports possessive quantifiers (`*+`, `++`), atomic groups
(`(?>…)`), the "absent operator" `(?~…)` (used to write `/\*(?~\*/)\*/` for
C comments without nested‑match pain), and `\G` anchors. vscode‑textmate's
core algorithm: line‑by‑line, carry an immutable **rule stack** between
lines, at each position try every active pattern (current rule + `include`s
+ active `end` pattern), pick the earliest match (ties broken by
declaration order), push/pop the stack on begin/end matches, emit tokens.
Crucially every pattern is matched against *a single line only* — that
constraint is what makes incremental re‑tokenisation cheap. 🟢

**Catastrophic backtracking** is a real problem; VS Code mitigates by
(a) running tokenisation off the UI thread and yielding,
(b) a per‑line timeout that abandons a runaway match,
(c) capping rule‑stack depth, and
(d) interning scope lists into a trie so theme resolution is O(stack depth).

**.NET port:** [`TextMateSharp`](https://github.com/danipen/TextMateSharp)
exists, MIT‑licensed, port of Eclipse's `tm4e` (itself a port of
vscode‑textmate). Ships `TextMateSharp.Grammars` with bundled grammars
including F#, plus ~20 themes. Wraps a native Oniguruma build. Used by
AvaloniaEdit in production. JSON grammars only. 🟢

**Building a minimal engine yourself:** the rule‑stack algorithm is ~500–800
lines of F#. The hard part is Oniguruma — .NET `System.Text.RegularExpressions`
lacks `\G`, the absent operator, and has different possessive semantics, so
a fraction of real‑world grammars fail. Either bind to native Oniguruma
(P/Invoke) or accept ~80 % grammar compatibility. 🟡

Sources:
- [vscode-textmate](https://github.com/microsoft/vscode-textmate)
- [VS Code: Optimizations in Syntax Highlighting](https://code.visualstudio.com/blogs/2017/02/08/syntax-highlighting-optimizations)
- [TextMate Language Grammars manual](https://manual.macromates.com/en/language_grammars)
- [TextMateSharp](https://github.com/danipen/TextMateSharp) · [NuGet](https://www.nuget.org/packages/TextMateSharp/)
- [Oniguruma SYNTAX.md](https://github.com/kkos/oniguruma/blob/master/doc/SYNTAX.md) · [Onigmo](https://github.com/k-takata/Onigmo)

---

## Hand-written tokenizers

The radically simple option: each supported language gets a bespoke
state‑machine tokeniser written directly in the editor's host language. No
DSL, no engine, no external assets.

**kilo** (antirez, BSD‑2) is the canonical example: ~1000 LOC total, of
which ~150 lines are syntax highlighting. Each language is a
`struct editorSyntax` holding `filematch[]`, `keywords[]` (with a `|`
suffix to mark "secondary" keywords / types), single‑line and multi‑line
comment delimiters, and feature flags. `editorUpdateSyntax()` walks the
rendered row character‑by‑character maintaining `in_string`, `in_comment`,
`prev_sep`, writing into a parallel `hl[]` byte array. Multi‑line comments
propagate by re‑calling the function on the next row when the
`hl_open_comment` flag flipped. 🟢

**dte** (Craig Barnes) uses a configurable but still hand‑rolled state
machine described in plain‑text `.syntax` files (states + conditions +
emits) — closer to a tiny custom DSL than to regex. **mle** uses PCRE rules
(so it's actually a regex‑rules family member). **nox** did not surface in
search and appears not to be a well‑known project. JetBrains Fleet's
"fallback lexer" pattern is alluded to in their blog posts but not
formally documented; the public surface is just "we lex on the UI thread
until the LSP wakes up". 🔴 (low confidence on Fleet specifics).

The general pattern in F# would be:

```fsharp
type Token =
  | Keyword | Ident | Number | StringLit | Comment | Operator | Whitespace
type LexState = Normal | InString | InBlockComment of depth:int
val tokenize : LexState -> string -> Token list * LexState
```

One `tokenize` per language, called per *line*, threading `LexState` through
the buffer the same way vscode‑textmate threads its rule stack — but the
state type is closed and trivially serialisable.

**F# tokeniser size estimate:** keywords list (~100 idents), operators,
identifier rule (incl. ``` `` ``` quoted idents), numeric literals with the
F# suffix zoo (`L`, `UL`, `lf`, `m`, `0x…`), char/string/triple‑string with
`@"…"` verbatim and `"""…"""` raw, `(* … *)` nestable block comments,
`//` line comments, attribute brackets. Realistic: **400–700 lines of
F#**, one weekend. Robust on partial code by construction (no
backtracking). Reusability of community grammars: zero. 🟢

Sources:
- [antirez/kilo](https://github.com/antirez/kilo) · [Build Your Own Text Editor tutorial](https://viewsourcecode.org/snaptoken/kilo/)
- [craigbarnes/dte](https://github.com/craigbarnes/dte)
- [F# Compiler tokenizer API](https://fsharp.github.io/fsharp-compiler-docs/fcs/tokenizer.html) — line‑oriented, `int64` state, exactly the shape we want.

---

## Approach comparison

| Axis | font-lock | TextMate | Hand‑written |
|---|---|---|---|
| Effort / new lang | Low–∞ (cc‑mode) | Near zero (grab `.tmLanguage`) | 1 weekend / language |
| Expertise | Elisp + syntax tables | Onig regex craft | Plain F# |
| Perf | Lazy, good | Good w/ guards; backtracking risk | Best; linear, no backtrack |
| Partial code robustness | Excellent | Good (line‑scoped) | Excellent |
| Reuse community assets | GPL Elisp only | Huge ecosystem (MIT‑ish) | None |
| MVU fit | Poor (mutable text props) | Good (`tokenizeLine : line × state → tokens × state`) | Excellent (same shape, closed state) |
| Theming | Faces, ad hoc | Scope strings + theme JSON | Roll your own Token→Style map |
| .NET availability | n/a | `TextMateSharp` (MIT) | Trivial — it's your own code |

---

## Bottom line for a .NET MVU editor

🟢 **Smallest credible "ship today" path, no tree‑sitter:** hand‑write an
F# tokeniser for F# only. ~500 LOC, a closed `LexState` DU, pure
`tokenize : LexState -> string -> Token list * LexState`. This is the
*identical shape* to vscode‑textmate's `tokenizeLine` and trivially
composes with a piece‑table: cache `LexState` at each line start, on edit
re‑tokenise from the dirtied line until the emitted state matches the
cached state (classic "lex‑state fixpoint" incremental scheme). For the
ANSI renderer, map `Token -> AnsiStyle` via a small record — no scope‑trie
machinery needed.

🟢 **If you want N languages cheaply:** depend on **TextMateSharp** + 
**TextMateSharp.Grammars** (MIT, NuGet, used by AvaloniaEdit). You get F#,
C#, Rust, TS, Python, ~40 more for free, plus 20 themes. Cost: native
Oniguruma binary, JSON grammars only, no cross‑grammar injections. Wrap
its `IGrammar.TokenizeLine(line, prevState)` and you have the same MVU
shape, themed via scope strings.

🟡 **If you want to build a minimal .NET TextMate engine yourself:**
budget ~1.5–2k LOC for the rule‑stack interpreter + theme trie, *plus* a
regex‑engine decision. Reusing .NET's built‑in regex fails on real
grammars; binding native Oniguruma re‑introduces the dependency you were
trying to avoid. Not recommended unless portability of grammars is a
hard requirement and TextMateSharp is unacceptable.

🔴 **Don't port font‑lock.** GPL‑3, Elisp‑shaped, assumes mutable text
properties and a syntax‑table primitive that doesn't exist in .NET.

**Recommendation for fedit:** start with a hand‑written F# tokeniser
(matches MVU purity, no native deps, ships in days). Keep
`tokenize : LexState -> string -> Token list * LexState` as the public
contract. If/when multi‑language demand appears, swap the implementation
for a `TextMateSharp` adapter behind the same signature — the MVU loop
and renderer don't need to change.
