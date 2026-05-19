# TreeSitter.DotNet verification for fedit

Date: 2026-05-19
Investigator: research subagent
Sources: NuGet page, `mariusgreuel/tree-sitter-dotnet-bindings` repo source tree
(read commit by commit via `gh api`), `ionide/tree-sitter-fsharp` repo.

## Verdict

**Commit-with-caveats. Confidence: high on the binding, medium on the
deployment story, F# grammar is NOT bundled and must be solved
separately.** The .NET binding itself is clean, idiomatic, has a real
test suite that exercises incremental edits and unicode, and wraps
tree-sitter v0.26.3 (one minor version behind upstream's v0.26.8 as of
2026-03-31). API surface for `Tree.Edit`, `Parser.Parse(source, oldTree)`,
and `Query.Execute(...).Captures` is all present and exactly what we
need. The blockers for fedit are practical: (1) the package is 50.9 MB
because it bundles every grammar and every RID — single-file publish
will need MSBuild surgery to trim, (2) F# is not in the 28-grammar
bundle, so we have to ship our own `libtree-sitter-fsharp.{dylib,so,dll}`
per RID from `ionide/tree-sitter-fsharp`, and (3) issue #11 reports
heap corruption after ~45k tree alloc/free cycles which the maintainer
has acknowledged but not root-caused. Bus factor is 1 (sole contributor
`mariusgreuel`, 25 commits, 23 stars).

## Critical API surface

All signatures pasted from `src/*.cs` at HEAD (commit `8cae484b`, v1.3.0).

**Incremental edit** — `src/Tree.cs`:

```csharp
public void Edit(Edit edit) => ts_tree_edit(Self, ref edit._self);

public IReadOnlyList<Range> GetChangedRanges(Tree other) => ...;
```

`Edit` is a managed wrapper around `TSInputEdit` exposing
`StartIndex`, `OldEndIndex`, `NewEndIndex` (int, byte-converted via
`IndexToByte`), and `StartPosition`/`OldEndPosition`/`NewEndPosition`
(`Point` = row/column).

**Reparse with old tree** — `src/Parser.cs`:

```csharp
public Tree? Parse(string source);
public Tree? Parse(string source, Tree? oldTree);
// Internal: ts_parser_parse_string_encoding(Self, oldTreePtr, source,
//          (uint)source.Length * 2, InputEncoding.UTF16LE);
```

The string is passed straight through as UTF-16LE — no internal
UTF-8 round-trip. Byte offsets returned by tree-sitter are converted
back to .NET char indices via `ByteToIndex` (divide by 2). This means
indices in `Node.StartIndex` are .NET `string` char indices, which
matches how a piece table or rope buffer over `string` would work.

**Queries** — `src/Query.cs` and `src/QueryCursor.cs`:

```csharp
public Query(Language language, string source);   // .scm source
public QueryCursor Execute(Node node);
public QueryCursor Execute(Node node, QueryOptions options);

// On QueryCursor:
public IEnumerable<QueryCapture> Captures { get; }
public IEnumerable<QueryMatch>   Matches  { get; }
public void SetRange(int startIndex, int endIndex);     // byte-range scoping
public void SetRange(Point startPoint, Point endPoint);
public uint MatchLimit { get; set; }
```

`QueryCapture` exposes `.Node` and an index into `_captureNames` on
the query; capture-name lookup is on the `Query` object. Predicates
(`#match?`, `#eq?`, etc.) are supported — `QueryPattern` has
`MatchesPredicates(...)` and the README mentions
"Support for predicates queries".

**Language loading** — `src/Language.cs`:

```csharp
public Language(IntPtr self);
public Language(string id);                       // "JavaScript" -> tree-sitter-javascript.dll / tree_sitter_javascript
public Language(string library, string function); // explicit dylib path + symbol
```

The string-id constructor expands to `tree-sitter-<id>` library name
and `tree_sitter_<id>` exported function. So `new Language("F#")`
would look for `libtree-sitter-f#` — we'd want
`new Language("tree-sitter-fsharp", "tree_sitter_fsharp")` instead.

The loader (`src/NativeLibrary.cs`) explicitly resolves
`AppContext.BaseDirectory/runtimes/{rid}/native/<lib>` per platform
(Windows `LoadLibraryEx`, Linux `libdl.so.2`, macOS `libdl`). This is
hand-rolled — it does not use .NET's `NativeLibrary` resolver — so it
will work under single-file publish as long as the `runtimes/` folder
is preserved next to the executable.

## Package metadata

| Field | Value |
|---|---|
| Latest version | 1.3.0 (2026-01-22) |
| Total downloads | ~67.7K across 6 versions |
| License | MIT |
| Target framework | netstandard2.0 (single TFM) |
| Dependencies | none |
| Package size | 50.93 MB |
| RuntimeIdentifiers in csproj | `win-x86;win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64` |
| Additional runtimes built | `linux-x86`, `linux-arm` (added Dec 2025) |
| tree-sitter C lib version | v0.26.3 (Jan 2026); upstream is v0.26.8 |

Releases: 1.0.0 (May 2025) → 1.0.1 → 1.1.0 (Oct 2025, macOS added)
→ 1.1.1 → 1.2.0 (Dec 2025, OCaml + Razor added) → 1.3.0 (Jan 2026).
Cadence is roughly monthly with point fixes.

**All fedit RIDs are covered:** osx-arm64, osx-x64, linux-x64,
linux-arm64, win-x64 are all in the official RuntimeIdentifiers list.

## Bundled grammars

From `.gitmodules` and `tree-sitter-native/Makefile`:

agda, bash, c, cpp, c-sharp, css, embedded-template, go, haskell, html,
java, javascript, jsdoc, json, julia, ocaml, ocaml-type, php, python,
ql, razor, ruby, rust, scala, swift, toml, tsq, typescript, tsx,
verilog.

That is 30 grammars including the `ocaml-type` and `tsx` variants.
All sourced from the official `tree-sitter/...` GitHub org except
`razor` which uses `tris203/tree-sitter-razor` (a community fork —
the upstream razor grammar is dead).

**F# is not in this list.** The README's "Work with all .NET languages
such as C#, F#, and VB.NET" refers to the *consumer* language, not the
*parsed* language. For fedit's F# parsing we'd have to:

1. Build `libtree-sitter-fsharp.{dylib,so,dll}` from
   `ionide/tree-sitter-fsharp` (MIT, 91 stars, last release v0.3.0 on
   2026-04-16, last commit 2026-04-27 — actively maintained), and
2. Ship it per-RID and load it via the explicit
   `Language("tree-sitter-fsharp", "tree_sitter_fsharp")` constructor.

This is doable but adds a non-trivial native build step to fedit's
release pipeline.

## Maintenance signals

- **Solo project.** `gh api .../contributors` returns one author:
  `mariusgreuel` with 25 commits. 2 watchers, 23 stars, 7 forks. Bus
  factor 1.
- **Commit cadence is real.** Activity in Sep, Oct, Nov, Dec 2025 and
  Jan 2026. Last commit 2026-01-22. Last release 1.3.0 same day.
- **Issues:** 5 total, 4 closed, 1 open. Closure ratio is excellent
  but the sample is tiny. The maintainer responds — issue #11 has an
  owner reply within 2 weeks.
- **No CI badge** in the README; the repo does have `.github/` but I
  did not inspect workflow status.
- **MS Test suite** in `tests/` covering tree edits, unicode, cursor
  walks, queries, lookahead iteration — `TreeTests.cs` exercises the
  exact incremental-reparse + `GetChangedRanges` flow fedit needs.

## Known risks

**1. Heap corruption under load — issue #11 (open as of 2026-04-19).**
A user repro-ed `STATUS_HEAP_CORRUPTION` after ~45–60k
`TSTree` alloc/free cycles parsing the Spring framework (~5000 Java
files) on Windows 11 / .NET 10. The maintainer suggested looking at
disposal patterns and said *"`ts_free()` is just CRT `free()`, there
is no ref-counting. If you dispose something that is still in use I
suppose you can trigger an access violation."* Not root-caused. For
an editor parsing one file at a time this is unlikely to fire — but
if we batch-parse a workspace this could bite.

**2. Tree.Edit takes `Edit` by value through a `ref` to internal
struct.** It's safe in the binding but a caller bug (calling `Edit`
on a tree that's already been used in a `Parser.Parse(source,
oldTree)` reparse — i.e. editing a "stale" tree) will silently
desync. Standard tree-sitter sharp edge, not binding-specific.

**3. Single-file publish bloat.** Package is 50.9 MB. We don't need
30 grammars — fedit only needs F# (plus a few "view-only" ones like
JSON/markdown/toml later). The `TreeSitter.DotNet.targets` file only
copies runtimes for `.NETFramework` targets; for .NET 9 the copy
happens via `..\build\runtimes\$(RuntimeIdentifier)\native\*` items in
the csproj's `None Include`. This means by default, building for a
single RID gives you just that RID's natives — but *all 30 grammars*
within that RID. We'd want to filter the natives at publish time or
fork the package to strip grammars we don't ship.

**4. UTF-16 char-index encoding everywhere.** `IndexToByte(int) =>
(uint)i * 2` and `ByteToIndex(uint) => (int)b / 2`. This assumes BMP-only
strings — surrogate pairs will give correct byte offsets at the .NET
char level but a "character" position will be one unit per UTF-16 code
unit. The `HandlesNonAsciiCharacters` test confirms emoji (👍) is
handled at the char-index level. For our piece table this is the
correct invariant — char indices, not Rune/grapheme indices.

**5. Disposal contract is sharper than docs suggest.** The
maintainer's comment in issue #11: *"I should probably drop the `using`
keywords from the example. It suggests that you need to call
`Dispose()` manually, but there is really no need since the garbage
collector will do that for you."* — concerning. Using `using`
deterministically is the F#-idiomatic thing to do, and `Tree`,
`Parser`, `Query`, and `QueryCursor` all implement `IDisposable` with
finalizers. We should keep `use` bindings and own our lifetimes.

**6. Lagging tree-sitter slightly.** Wraps v0.26.3 (Jan 2026); upstream
is v0.26.8 (March 2026). Five patch releases behind. No major API
breakage at risk but bug fixes may be missing.

**7. No source-link or symbol packages** documented.

## Alternative bindings — quick check

- `tree-sitter` NuGet by `Summpot` — v0.4.19, last published 2023-11-05,
  4k downloads. **Effectively abandoned.**
- `Summpot.TreeSitter.Runtime.*` per-language runtimes — also v1.0.0,
  same Nov 2023 timestamp. **Abandoned.**
- I did not find the older `csharp-tree-sitter` org repo or
  `Cody-Duncan/tree-sitter-csharp-bindings` mentioned in NuGet's top
  search results; the `mariusgreuel` package is unambiguously the
  active option in 2026.

## Recommendation

**Commit, with these caveats baked into the plan:**

1. **Vendor or fork the F# grammar build.** Build
   `libtree-sitter-fsharp` from `ionide/tree-sitter-fsharp` v0.3.0 for
   our 5 RIDs and ship them alongside our `fedit` executable in
   `runtimes/{rid}/native/`. Document the build steps in our justfile.
2. **Strip unused grammars at publish time.** Add an MSBuild target
   that, after `dotnet publish -r <rid>`, deletes
   `runtimes/<rid>/native/libtree-sitter-{agda,julia,verilog,...}.*`
   from the output. Target footprint: ~5 MB per RID instead of ~50 MB.
3. **Don't share `Parser`/`Query` across threads.** Use one parser
   per editor buffer; the type isn't documented thread-safe.
4. **Watch issue #11.** If we ever batch-parse a workspace
   (find-symbol-in-workspace, multi-file query), put a budget on it
   and recycle parsers periodically.
5. **Stick with `use` for `Tree`/`Parser`/`Query`** despite the
   maintainer's comment. F# `use` is the correct lifetime hammer here.
6. **Plan to keep an escape hatch.** Bus factor 1, 23 stars. If the
   project goes dormant we can either pin v1.3.0 and patch native libs
   ourselves, or replicate the (small, well-written) C# binding code
   into our own assembly — it's ~50 KB of P/Invoke source under MIT.

Net: this is the only viable option in 2026, and it's actually pretty
good — clean code, good tests, real incremental-parse support, all RIDs
we need. Just budget two days for the F# grammar packaging and the
publish-trim MSBuild work, and put a smoke test in CI that round-trips
`Tree.Edit` + reparse on an F# buffer for each RID.
