# Syntax highlighting

`fedit` highlights source files using
[tree-sitter](https://tree-sitter.github.io/tree-sitter/) — F# (via the
[ionide/tree-sitter-fsharp](https://github.com/ionide/tree-sitter-fsharp)
grammar), shell scripts, and the other languages listed under
[Supported languages](#supported-languages). This page covers the
implementation, how to update the grammar, how themes map onto capture
names, and how to troubleshoot when colors don't appear.

## How it works

The pipeline is:

```
buffer text  →  Parser.Parse  →  Tree
                                    ↓
                              Query.Execute  →  Captures
                                                    ↓
                                         Highlight.resolveCapture
                                                    ↓
                                       sorted HighlightSpan[]
                                                    ↓
                                   Layout.renderEditor (per cell)
                                                    ↓
                                   Style.Foreground = Theme.SyntaxXxx
```

- `Highlight.fs` owns `HighlightCapture` (the 16-case DU we paint),
  `HighlightSpan`, `HighlightState`, and `HighlightRegistry` (one
  language + query per supported language; parsers are per-buffer).
- `Model.HighlightRegistry` is built once by `Runtime.run` and lasts the
  process. `Model.HighlightStates : Map<int, HighlightState>` holds
  per-buffer parse state keyed by `BufferState.Id`.
- `Editor.update` reparses on every `EditTick` bump (mutating edits
  only — cursor / viewport moves don't trigger a reparse).
- `Layout.renderEditor` overlays foreground colors per cell after the
  text row is written and before selection/search overlays.

MVP is **Phase 1: full reparse on every edit**. Incremental parsing via
`Tree.Edit` + `GetChangedRanges` is a future enhancement (Phase 2 —
needs edit-records plumbed through `Buffer.fs`).

## Supported languages

`Highlight.detectLanguage` maps files to grammars by filename, then
extension, then `#!` shebang line (for extensionless scripts):

| Language     | Detected by                                                                                                                                 |
| ------------ | ------------------------------------------------------------------------------------------------------------------------------------------- |
| `fsharp`     | `.fs`, `.fsi`, `.fsx`                                                                                                                       |
| `javascript` | `.js`, `.mjs`, `.cjs`                                                                                                                       |
| `typescript` | `.ts`                                                                                                                                       |
| `tsx`        | `.tsx`                                                                                                                                      |
| `python`     | `.py`                                                                                                                                       |
| `json`       | `.json`                                                                                                                                     |
| `c-sharp`    | `.cs`                                                                                                                                       |
| `go`         | `.go`                                                                                                                                       |
| `rust`       | `.rs`                                                                                                                                       |
| `html`       | `.html`, `.htm`                                                                                                                             |
| `css`        | `.css`                                                                                                                                      |
| `c`          | `.c`, `.h`                                                                                                                                  |
| `php`        | `.php`, `.phtml`                                                                                                                            |
| `bash`       | `.sh`, `.bash`, `.zsh`, `.ksh`, `.command`; shell dotfiles (`.bashrc`, `.zshrc`, `.profile`, `PKGBUILD`, …); or a `#!/bin/sh`-style shebang |
| `markdown`   | `.md`, `.mdx`, `.markdown`                                                                                                                  |
| `xml`        | `.xml`, `.svg`, `.xsl`, `.xslt`                                                                                                             |
| `dart`       | `.dart`                                                                                                                                     |
| `just`       | `.just`, `Justfile`                                                                                                                         |
| `make`       | `.mk`, `Makefile`, `GNUmakefile`                                                                                                            |
| `astro`      | `.astro`                                                                                                                                    |

Grammars come from two places. Most are **bundled** inside
`TreeSitter.DotNet` (loaded via the string-id constructor); F# and a
handful of others are **vendored** natives under `runtimes/<rid>/native/`
(loaded via the `(library, function)` constructor). tree-sitter-bash is
the de-facto grammar for every POSIX-ish shell, so `.zsh`/`.ksh` map to
it too.

Adding a **bundled** language: add its id to the bundled list in
`HighlightRegistry.tryCreate`, embed a `highlights.scm`, add it to the
`_KeepGrammars` publish-trim list in `Fedit.fsproj`, and extend
`Highlight.detectLanguage`. A **vendored** language additionally needs:
vendor the grammar, build a per-RID native, and use the external loader.

## Updating the F# grammar

```bash
cd vendor/tree-sitter-fsharp
git fetch origin
git checkout <new-tag>
cd -

cp vendor/tree-sitter-fsharp/queries/highlights.scm \
   src/Fedit/Resources/queries/fsharp/highlights.scm

just build-grammar          # host RID
# or for cross-RID coverage:
just build-grammars-all     # requires `brew install zig`

git add vendor/tree-sitter-fsharp src/Fedit/Resources/queries
git commit -m "feat(highlight): bump tree-sitter-fsharp to <new-tag>"
```

The native libraries themselves are **not** tracked — they're ~11MB
each and CI builds them per matrix leg. Local builds land in
`src/Fedit/runtimes/<rid>/native/` (gitignored).

## Capture → theme mapping

Tree-sitter queries emit capture names like `keyword.control`,
`string.special.path`, `function.call`. `Highlight.resolveCapture`
collapses these into the 16-case `HighlightCapture` DU via longest-
prefix-first matching:

| Tree-sitter pattern      | `HighlightCapture` |
| ------------------------ | ------------------ |
| `keyword.control[.*]`    | `KeywordControl`   |
| `keyword.operator[.*]`   | `KeywordOperator`  |
| `keyword[.*]`            | `Keyword`          |
| `string.special[.*]`     | `StringSpecial`    |
| `string[.*]`             | `String`           |
| `function.call[.*]`      | `FunctionCall`     |
| `function[.*]`           | `Function`         |
| `type[.*]`               | `Type`             |
| `constructor[.*]`        | `Constructor`      |
| `variable.parameter[.*]` | `Parameter`        |
| `variable[.*]`           | `Variable`         |
| `number[.*]`             | `Number`           |
| `comment[.*]`            | `Comment`          |
| `operator[.*]`           | `Operator`         |
| `punctuation[.*]`        | `Punctuation`      |
| `attribute[.*]`          | `Attribute`        |

Unknown captures resolve to `None` and stay un-styled.

Each `HighlightCapture` maps to a `Color` field on `Theme`
(`SyntaxKeyword`, `SyntaxString`, …). `Color.Default` means "no
override; keep the surface foreground" — useful for captures you
don't want to paint (e.g. `Variable` and `Parameter` ship as
`Default` in the bundled themes).

User themes can override individual captures via an optional `syntax`
block. JSON keys use camelCase (e.g. `keywordControl`, `functionCall`).
Missing fields fall back to `Themes.defaultTheme`.

```json
{
    "name": "ocean",
    "accent": "#1F6FEB",
    "statusFg": "brightWhite",
    "statusBg": "midnightBlue",
    "selectedBg": "steelBlue",
    "currentLine": "electricBlue",
    "syntax": {
        "keyword": "#FF8FB1",
        "string": "#73D49C",
        "comment": "#8A8F98"
    }
}
```

## Troubleshooting

**No colors appear at all.**

Check the startup log (`fedit --log fedit.log .`) for one of:

- `highlight: failed to load tree-sitter — F# files will render plain`
  → the native lib isn't where the loader expects it. Confirm
  `src/Fedit/runtimes/<rid>/native/libtree-sitter-fsharp.{dylib|so|dll}`
  exists next to the binary, or run `just build-grammar` to build it.
- `highlight: loaded tree-sitter F# grammar` followed by no styling →
  check `:syntax` status. Run `:syntax on`.

**Colors look wrong / clash with the accent.**

Bundled themes share a single dark-friendly syntax baseline. If a
theme's accent collides with one of the syntax picks, override in
`~/.config/fedit/themes/<name>.json` via the `syntax` block.

**Performance feels sluggish on a large file.**

Phase 1 reparses on every text-mutating keystroke. For a 1k-line F#
file this is < 5 ms; for 10k+ lines it can approach a frame. If you
see lag, file an issue and we'll prioritize Phase 2 (incremental
parse via `Tree.Edit` + `GetChangedRanges`).

## Roadmap

- **Phase 2 — incremental parse.** Plumb edit records through
  `Buffer.fs` and call `tree.Edit(edit)` + `parser.Parse(text, oldTree)`
    - `GetChangedRanges` instead of full reparse.
- **More languages.** C#, JSON, Markdown, TOML are likely first
  additions. Architecture is set up — needs grammar vendoring + theme
  picks.
- **Plugin-installed grammars.** Extend the plugin API so plugins can
  ship their own `Language` + `highlights.scm`.
- **Semantic highlighting.** Eventually layer LSP semantic tokens on
  top of tree-sitter syntax.
