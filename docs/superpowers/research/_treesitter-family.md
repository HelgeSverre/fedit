# Tree-sitter Family: Editors and .NET Bindings

Research scope: how Helix, Zed, and Neovim wire tree-sitter into a real editor, what tree-sitter's incremental model actually guarantees, and whether the .NET binding story is ready for an F# terminal editor (fedit) that ships single-file binaries.

## Helix 🟢

Helix is a tree-sitter-only editor — there is no legacy regex `:syntax` engine to fall back to. Configuration lives in `languages.toml`; each language entry points at a grammar repo and revision, and queries (`highlights.scm`, `injections.scm`, `locals.scm`, `indents.scm`) live under `runtime/queries/<lang>/`. Grammars are **not** statically linked: they're compiled to `runtime/grammars/<name>.so` via `hx --grammar fetch && hx --grammar build`, then `dlopen`'d at startup ([adding-languages docs](https://docs.helix-editor.com/guides/adding_languages.html)). Distros typically ship the runtime directory next to the binary; `HELIX_RUNTIME` overrides the path.

The architecture splits into a `Loader` (configs/queries), a `Syntax` object (parse trees + incremental updates), and the query runner ([DeepWiki overview](https://deepwiki.com/helix-editor/helix/4.2-tree-sitter-integration)). Edits become byte ranges, only affected regions reparse, and parse calls have a **500 ms timeout** to keep the UI responsive. Highlighting is not a per-line tree walk — Helix builds a layered highlight iterator over visible byte ranges, including injection layers (e.g. inline Markdown inside block Markdown).

Known pain: prior to Helix 25.07 the `tree-sitter-highlight` crate did full re-parses per highlight iterator. The new **Tree-house** crate makes both parsing and queries incremental per-injection-layer ([25.07 release notes](https://helix-editor.com/news/release-25-07-highlights/)). Earlier, PR [#4716](https://github.com/helix-editor/helix/pull/4716) replaced an O(N²) injection-layer linear search with a hashtable — a Linux-kernel header with ~28k comment injections went from "unusable" to fine. Open issues remain for files >100 MB ([#338](https://github.com/helix-editor/helix/issues/338)) and very dense 50k-line C++ files ([#3072](https://github.com/helix-editor/helix/issues/3072)). Lesson for fedit: incremental is necessary but not sufficient — injection bookkeeping has to be cheap too.

## Zed 🟢

Zed combines tree-sitter with LSP. Tree-sitter via `highlights.scm` is the default; a per-language `semantic_tokens` setting takes `"off" | "combined" | "full"` ([Zed docs](https://zed.dev/docs/extensions/languages)). Grammars ship via Zed's extension system, not bundled in the binary. Each extension's `extension.toml` declares grammars as `{ repository, rev }` pairs (Git URL + commit SHA), plus a `languages/<lang>/` directory with `config.toml` and `.scm` query files. Language servers and grammars can be packaged together in a single extension.

Rendering-wise Zed treats tree-sitter highlights and LSP semantic tokens as two streams to be merged or substituted. Performance is famously GPU-accelerated, but the highlight pipeline itself isn't particularly novel — it's tree-sitter + a polished extension story. The interesting transferable idea is the **extension-first grammar distribution**: don't try to bundle 50 grammars in your editor binary; let users opt in.

## Neovim (tree-sitter mode) 🟡

Neovim has a built-in tree-sitter runtime (`vim.treesitter.*`), but parsers are not bundled — `nvim-treesitter` is the de-facto installer. It downloads each grammar's source via `tar`/`curl`, then compiles it locally with whatever C compiler it finds: `M.compilers = { $CC, "cc", "gcc", "clang", "cl", "zig" }` ([nvim-treesitter](https://github.com/nvim-treesitter/nvim-treesitter)). Result lands in `stdpath('data')/site/parser/<lang>.so`. The new rewrite requires `tree-sitter-cli` (≥0.26.1) and Neovim 0.12+.

Tree-sitter highlighting **coexists** with the legacy `:syntax` engine — they're independent layers, and you turn TS highlighting on per-buffer via `vim.treesitter.start()`. The runtime is ABI-versioned (Neovim 0.12 needs ABI ≥13), and `:checkhealth nvim-treesitter` reports mismatches. The "compile on user's machine" model is the **opposite** of what an F# single-file editor wants — it assumes the user has a C toolchain. For fedit this is a non-starter as a primary distribution method, but useful as a fallback escape hatch ("bring your own grammar `.so`").

## Tree-sitter core model 🟢

Tree-sitter exposes a C API (`tree_sitter/api.h`); all bindings are P/Invoke shims on top ([using-parsers](https://tree-sitter.github.io/tree-sitter/using-parsers)). The incremental model is two calls: `ts_tree_edit(tree, &TSInputEdit{ start_byte, old_end_byte, new_end_byte, start_point, old_end_point, new_end_point })` to rewrite node positions, then `ts_parser_parse(parser, old_tree, new_input)` — the new tree **shares structure with the old one** for unchanged subtrees ([advanced parsing](https://tree-sitter.github.io/tree-sitter/using-parsers/3-advanced-parsing.html)). Reparse cost is roughly proportional to the size of the edit's "damaged" region, not the file. Per-keystroke incremental parses on normal source files are sub-millisecond; full parses of 10k-line files are typically single-digit ms, 100k lines tens of ms (grammar-dependent — `tree-sitter-haskell` famously had a 50× speedup from a malloc-heavy external scanner rewrite, [owen.cafe](https://owen.cafe/posts/tree-sitter-haskell-perf/)).

Queries (`.scm`, S-expression syntax) drive highlights, locals (scope analysis), and injections (embedded languages). A query against the tree yields `(node, capture-name)` pairs; the renderer turns capture names into theme colors. WASM grammars are now first-class: `tree-sitter build --wasm` produces a `.wasm` parser, and `libtree-sitter` built with the Wasmtime feature can load them via `ts_wasm_store_load_language` ([issue #1864](https://github.com/tree-sitter/tree-sitter/issues/930)).

## .NET binding survey 🟢

The landscape is suddenly clean. **[TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet)** by Marius Greuel ([mariusgreuel/tree-sitter-dotnet-bindings](https://github.com/mariusgreuel/tree-sitter-dotnet-bindings)) is the clear winner as of Jan 2026:

- **MIT license**, releases May 2025 → 1.3.0 (Jan 2026), ~44k downloads.
- Native binaries for **win-x86/x64/arm64, linux-x86/x64/arm/arm64, osx-x64/arm64** — every RID fedit cares about.
- **28+ grammars bundled** (C#, F#, JS, TS, Python, Rust, Go, Java, C/C++, Bash, HTML, CSS, JSON, TOML, Markdown, …).
- **Queries (.scm) and predicates supported**; passes the upstream WebAssembly bindings test suite.
- Works in C# and F# (including `#r` in F# Interactive). Custom grammars load via `new Language("path/to/lib.so", "tree_sitter_<lang>")`.

Caveats: the docs don't explicitly call out `Tree.Edit` / incremental reparse, and single-file-publish friendliness isn't documented. The package is **~26 MB** because it carries all native binaries for all RIDs — for a single-RID self-contained publish, this fattens the binary unless you trim per-RID with `runtimes/` filtering. RID-specific NuGet sub-packages would be cleaner but don't exist yet.

Everything else is effectively abandoned or experimental: [tree-sitter/csharp-tree-sitter](https://github.com/tree-sitter/csharp-tree-sitter) (official org, low activity), [profMagija/dotnet-tree-sitter](https://github.com/profmagija/dotnet-tree-sitter) (submodule + gcc build, UTF-16 byte-index quirk), [Cody-Duncan/tree-sitter-csharp-bindings](https://github.com/Cody-Duncan/tree-sitter-csharp-bindings) (CppSharp generator), the original `TreeSitter` NuGet (Linux-only, 2019). No production .NET editor is publicly known to ship tree-sitter; this is greenfield. The Wasmtime-via-Wasmtime.NET route exists but the practical path is "libtree-sitter built with WASM feature, P/Invoke" — not a packaged solution today.

## Licensing 🟢

The core `tree-sitter` library is **MIT**. Most grammars are **MIT or Apache-2.0**: tree-sitter-fsharp ([ionide/tree-sitter-fsharp](https://github.com/ionide/tree-sitter-fsharp), MIT), tree-sitter-c#, tree-sitter-rust, etc. Notable exceptions: tree-sitter-erlang and tree-sitter-elixir are Apache-2.0 (WhatsApp/elixir-lang). Permissive-license aggregators like [tree-sitter-language-pack](https://github.com/Goldziher/tree-sitter-language-pack) explicitly **exclude GPL/AGPL/LGPL/MPL** grammars, which suggests a few copyleft outliers exist in the wild (some R-language bindings ship GPL-3) but they're rare. **No GPL trap for fedit** as long as it pulls grammars from the main tree-sitter org or TreeSitter.DotNet's bundled set. Add a per-grammar `NOTICE` file if redistributing.

## Bottom line for a .NET editor 🟢

Adopting tree-sitter in fedit is now realistic, not speculative.

**Smallest credible "ship F# + a few languages" path:**
1. Add `TreeSitter.DotNet` 1.3.x. You get tree-sitter-fsharp and ~27 others for free.
2. Treat highlighting as a service that owns: `Parser`, `Tree?`, and a list of `Query` objects per language. On each buffer edit, build a `TSInputEdit` from the piece-table's edit record, call `tree.Edit(...)` then `parser.Parse(tree, newText)`. **This maps cleanly onto MVU** — the parse-tree is part of the model, edits are pure, the parse call is the one effect.
3. Highlight per visible viewport, not per buffer: run the highlights query over the byte range of currently-rendered lines, cache `(byte_range → captures)`, repaint on tree change. Don't walk the tree per ANSI cell.
4. Debounce only if you measure a problem; for files <50k lines, tree-sitter's incremental reparse is sub-frame on modern hardware. Adopt Helix's **500 ms parse timeout** as a defensive bound.

**Distribution gotchas:**
- The bundled NuGet is ~26 MB across all RIDs. For self-contained single-file publish, set `RuntimeIdentifier` and let `dotnet publish` strip non-matching runtimes; expect ~3–6 MB of native parser blobs added per RID, dominated by the long tail of grammars. If that's still too much, fork or repackage as `TreeSitter.DotNet.Core` + per-language packages — not pleasant but mechanical.
- F# grammar quality: ionide/tree-sitter-fsharp is the canonical one but historically lagged the compiler; verify against your test corpus before committing.
- Incremental API: confirm by smoke test that `TreeSitter.DotNet` exposes `Tree.Edit` and `Parser.Parse(oldTree, …)`. If it only exposes full-parse, file an issue — the upstream C API is there.
- Avoid the nvim model (compile-on-user-machine). Avoid the Helix model (separate `runtime/` directory) unless you want extension installability; bundled-via-NuGet is the simplest fit for a self-contained F# binary.

**Recommendation:** prototype on one buffer with F# + Markdown highlighting through `TreeSitter.DotNet`. If the edit→parse→query→render loop stays under a frame at 10k lines, commit. If not, the fallback is a simpler tokenizer (FSharp.Compiler.Service tokens, Markdown regex) — not the end of the world for an MVU editor.

