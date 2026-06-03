# Regex-based syntax highlighting in terminal editors

Research input for `fedit` (F# / .NET 9 / MVU / piece-table / custom ANSI). Question: is a regex-rules approach enough for an MVP, and how do mature editors structure it?

Confidence: 🟢 well-sourced, 🟡 inferred, 🔴 weak / anecdotal.

## Vim/Neovim legacy `:syntax`

🟢 Vim's `:syntax` engine has three primitives — `keyword`, `match`, `region` — declared as Ex commands in a `.vim` file sourced when a filetype is detected ([syntax.txt](https://vimhelp.org/syntax.txt.html)). Keywords are O(1) exact-word matches, `match` runs a single-line regex, and `region` ties a `start=/.../` to an `end=/.../` (optionally `skip=`) for multi-line constructs like strings, comments, or heredocs. Regex flavour is Vim's own (BRE/ERE-ish with `\@=` lookarounds, atoms like `\<` / `\>`, and `\v` "very magic").

🟢 Multi-line state is reconstructed by `:syn sync`: `fromstart` rescans the file, `minlines=N` looks back N lines, plus filetype-specific hooks like C's `c_minlines`. This is the answer to "where am I in a multi-line region after a scroll" — Vim does **not** keep a persistent parse state, it re-derives it locally. Edit handling is incremental per redraw: visible window + sync lookback, not whole file.

🟢 Colors are decoupled: syntax groups (`cComment`) are aliased to standard highlight groups (`Comment`, `String`, `Type`...) which the colorscheme styles. This indirection is the single best idea to steal.

🟡 Performance pain points are well-documented: long lines blow up backtracking — mitigated by `synmaxcol` (default 3000) which truncates highlighting on the line, and `redrawtime` which disables syntax wholesale when exceeded ([vim/vim#555](https://github.com/vim/vim/issues/555), [ithy summary](https://ithy.com/article/enhancing-vim-syntax-for-large-files-wnznx95k)). Jumping past 10k lines can desync coloring ([vim/vim#2790](https://github.com/vim/vim/issues/2790)).

🟢 Neovim ships the same engine verbatim, but tree-sitter is the strategic direction: `vim.treesitter.start()` disables `:syntax` by default; co-existence requires `vim.bo.syntax = 'ON'` or `additional_vim_regex_highlighting = true` ([Neovim treesitter docs](https://neovim.io/doc/user/treesitter.html), [thevaluable.dev](https://thevaluable.dev/tree-sitter-neovim-overview/)). Highlight groups gained an `@`-prefixed namespace (`@function.builtin`).

## nano (.nanorc)

🟢 nano's grammar is plain text in `.nanorc` with three directives: `syntax`, `color`, `icolor` ([nanorc(5)](https://www.nano-editor.org/dist/latest/nanorc.5.html)). Regex flavour is **POSIX ERE** — no lookaround, no backrefs, ASCII-centric. A syntax block opens with `syntax name "fileregex"` and rules apply only inside it.

🟢 Single-line rules are `color fg,bg "regex"`; multi-line rules are `color fg,bg start="open" end="close"`. There is no other state machine — you cannot nest, you cannot define "in-string suppresses keywords" except by ordering and the implicit fact that later rules paint over earlier ones on the same span. Color is **bound directly** in the rule, not via a named scope, so themes are not pluggable without rewriting grammars.

🟢 Minimal example:

```nanorc
syntax mylang "\.ml$"
color brightblack "#.*$"
color green start="\"" end="\""
color yellow "\<[0-9]+\>"
color cyan "\<(let|in|if|then|else|match|with)\>"
```

🟡 Limitations: no lookbehind makes "match `=` but not `==`" awkward; no shared state means a string spanning a region boundary can mis-highlight following lines until a closing quote appears. nano just lives with it. Plugin story is "drop a file in `/usr/share/nano/`."

## micro (YAML)

🟢 micro adapted nano's model into structured YAML, with **named scopes** instead of direct colors ([colors.md](https://github.com/zyedidia/micro/blob/master/runtime/help/colors.md), [runtime/syntax](https://github.com/zyedidia/micro/tree/master/runtime/syntax)). A file declares `filetype`, `detect: { filename:, header: }`, then a `rules:` list. Each rule is either a single-line `scope: "regex"` or a region:

```yaml
filetype: mylang
detect: { filename: "\\.ml$" }
rules:
    - comment: "#.*$"
    - statement: "\\b(let|if|then|else)\\b"
    - constant.number: "\\b[0-9]+\\b"
    - constant.string:
          start: '"'
          end: '"'
          skip: "\\\\."
          rules:
              - constant.specialChar: "\\\\."
```

🟢 Scopes are dotted (`constant.string.specialChar`) and the colorscheme resolves with longest-prefix fallback — `color-link comment "cyan"` covers all `comment.*`. Inner `rules:` inside a region give you the "in-string highlight escapes" nesting nano can't do. Regex is Go's `regexp` package — **RE2** — so no backrefs, no lookaround, linear time.

🟡 Performance: micro highlights lazily on the visible viewport with a small lookback for region state; it's snappy on multi-MB files in my recollection but I didn't find a benchmark thread. Plugin story is excellent: drop a YAML into `~/.config/micro/syntax/`.

## Kakoune (`add-highlighter`)

🟢 Kakoune treats highlighters as composable nodes in a tree rooted at scopes (`global/`, `buffer/`, `window/`, `shared/`). They are added imperatively in `.kak` scripts ([highlighters.asciidoc](https://github.com/mawww/kakoune/blob/master/doc/pages/highlighters.asciidoc)). Primitives include `regex`, `regions`, `group`, `ranges`, `replace-ranges`, `show-matching`, plus `dynregex` for option-interpolated patterns.

```kak
add-highlighter shared/mylang/ regions
add-highlighter shared/mylang/string region '"' (?<!\\)" fill string
add-highlighter shared/mylang/comment region '#' '$' fill comment
add-highlighter shared/mylang/code default-region group
add-highlighter shared/mylang/code/ regex \b(let|if|then|else)\b 0:keyword
add-highlighter shared/mylang/code/ regex \b\d+\b 0:value
```

🟢 Capture groups feed faces per index (`0:keyword 1:operator`). Regions can carry their own default-region group so language code inside is highlighted by sub-rules. Faces (`keyword`, `string`, `value`...) are theme-mapped via `face global keyword red+b`.

🟢 The killer feature for an MVP-vs-future story: `ranges <option>` reads a `range-specs` option that **any process can populate** — Kakoune's LSP and tree-sitter plugins simply write spans into that option from a shell hook. Same renderer, external brain. This is the cleanest "regex now, real parser later" seam I've seen.

🟡 Performance is generally praised, partly because Kakoune renders selections-first and re-runs highlighters only for the displayed window.

## Comparison: file formats and regex flavors

| Editor        | Format           | Regex engine                                  | Multi-line                   | Theme indirection               |
| ------------- | ---------------- | --------------------------------------------- | ---------------------------- | ------------------------------- |
| Vim           | `.vim` Ex script | Vim's own (backtracking, lookarounds, `\<\>`) | `region` + `syn sync`        | named hl groups 🟢              |
| Neovim legacy | same as Vim      | same                                          | same                         | same                            |
| nano          | `.nanorc` text   | POSIX ERE                                     | `start=/end=` only           | none — colors inline 🔴         |
| micro         | YAML             | Go RE2                                        | regions with nested `rules`  | dotted scopes + `color-link` 🟢 |
| Kakoune       | `.kak` script    | Boost.Regex (PCRE-ish)                        | `regions` + `default-region` | named faces 🟢                  |

🟢 For porting to .NET, **micro's YAML** is the obvious winner: declarative, line-oriented, schema-stable, RE2-shaped (so it's already a subset of what .NET supports), and the scope→theme indirection drops in cleanly. nano's format is trivially parseable but has no theming layer. Vim's format is executable script — you'd be writing an Ex interpreter. Kakoune's is also script-shaped but smaller surface; portable if you implement just `regex` and `regions`.

## .NET regex implications

🟢 `System.Text.RegularExpressions` is a backtracking NFA with optional `RegexOptions.Compiled` (IL emit) and `RegexOptions.NonBacktracking` since .NET 7 (DFA-ish, RE2-class guarantees) ([devblogs](https://devblogs.microsoft.com/dotnet/regular-expression-improvements-in-dotnet-7/)). Source generators (`[GeneratedRegex]`) AOT-compile patterns at build time — fastest path, ideal for built-in grammars.

🟢 Features vs PCRE2/RE2: .NET **has** variable-length lookbehind, backreferences, named groups, balanced groups (unique), inline options, `\b`, Unicode categories. .NET **lacks** PCRE2's possessive quantifiers (workaround: atomic groups `(?>...)`), recursion (`(?R)`), and DEFINE blocks. Performance is competitive with PCRE2 for typical patterns once compiled; PCRE2-JIT still wins on heavy backtracking workloads. RE2 still wins on adversarial input — and `NonBacktracking` is .NET's answer.

🟡 For an editor: enable `RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ECMAScript` if you want a portable, predictable flavor close to micro/Kakoune. Set `Regex.MatchTimeout` (50ms is typical) so a pathological pattern can't freeze the redraw loop. If you ingest untrusted grammars, prefer `NonBacktracking`.

🟢 Porting micro YAML to .NET is almost mechanical: Go RE2 → .NET non-backtracking, both reject backrefs/lookaround, so existing micro grammars run unmodified barring a few `(?P<name>...)` → `(?<name>...)` rewrites.

## Bottom line for a .NET editor MVP

🟢 **Steal micro's grammar format.** YAML, dotted scopes, regions with nested rules, colorscheme `color-link` — this maps to F# records 1:1, parses with YamlDotNet, and you inherit 150+ existing grammars as bootstrap material (license-permitting). Pair it with Vim's **highlight-group indirection** (theme decoupled from grammar) and Kakoune's **`ranges` escape hatch** so a future tree-sitter or LSP semantic-tokens producer can write the same span list.

🟢 **Minimum viable rule set** for the MVP: line comments, block comments (region), single/double-quoted strings (region with escape skip), keywords (alternation with `\b`), numbers (`\b\d+(\.\d+)?\b`). That's five rules and covers a credible first impression for any C-family or ML-family file.

🟢 **Performance plan**, paraphrased from the Vim/Kakoune/micro consensus:

1. Highlight only the **visible viewport** plus a small lookback (start with 200 lines, à la `synmaxcol`/`minlines`).
2. Cache per-line "region-state-at-EOL" tokens in the piece-table's line index. On edit, invalidate from the changed line forward; rescan stops as soon as the state token matches the cached one (Vim/TextMate/Sublime do exactly this).
3. Cap per-line work: a `synmaxcol`-equivalent that bails on lines >2-4k chars.
4. Compile every regex once with `RegexOptions.Compiled` and a 50ms `MatchTimeout`.
5. Pre-merge keywords into one alternation per scope — one regex call per line per scope, not per keyword.

🟡 **What to skip for MVP**: nested includes (micro supports `include:`), embedded sub-languages (HTML-in-PHP), and semantic highlighting. Add a `ranges`-style external-spans channel from day one so these can land later without renderer changes.

Sources: [Vim syntax.txt](https://vimhelp.org/syntax.txt.html), [Vim runtime/syntax](https://github.com/vim/vim/tree/master/runtime/syntax), [nanorc(5)](https://www.nano-editor.org/dist/latest/nanorc.5.html), [nano syntax page](https://www.nano-editor.org/dist/latest/syntax.html), [micro repo](https://github.com/zyedidia/micro), [micro colors.md](https://github.com/zyedidia/micro/blob/master/runtime/help/colors.md), [Kakoune highlighters.asciidoc](https://github.com/mawww/kakoune/blob/master/doc/pages/highlighters.asciidoc), [Neovim treesitter docs](https://neovim.io/doc/user/treesitter.html), [.NET 7 regex improvements](https://devblogs.microsoft.com/dotnet/regular-expression-improvements-in-dotnet-7/), [RE2 vs PCRE rationale](https://groups.google.com/g/re2-dev/c/T-pkUDsDk3o), [Vim long-line perf #555](https://github.com/vim/vim/issues/555), [Vim 10k-line sync bug #2790](https://github.com/vim/vim/issues/2790).
