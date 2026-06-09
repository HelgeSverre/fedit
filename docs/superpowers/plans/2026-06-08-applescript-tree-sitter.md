# AppleScript Tree-Sitter Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add AppleScript syntax highlighting support backed by `https://github.com/helgesverre/tree-sitter-applescript`.

**Architecture:** AppleScript joins the existing external grammar path used by markdown, XML, Dart, Just, Make, Astro, TOML, and Sema. `Highlight.fs` detects `.applescript`, loads the vendored native grammar with `tree_sitter_applescript`, and compiles an embedded first-party highlight query.

**Tech Stack:** F#, xUnit, TreeSitter.DotNet, git submodules, clang/zig native grammar builds.

---

### Task 1: Add Regression Tests First

**Files:**

- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Add failing detection, registry, and parse tests**

Add AppleScript cases to the existing vendored-language tests:

```fsharp
[<InlineData("script.applescript", "applescript")>]
```

Add `applescript` to `registry loads language without throwing`.

Add a parse case:

```fsharp
[<InlineData("applescript", "(* hello *)\ntell application \"Finder\"\n  set x to 42\nend tell")>]
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
PATH="$PWD/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --nologo --filter "FullyQualifiedName~HighlightTests"
```

Expected: FAIL because `detectLanguage` does not map `.applescript`, registry does not load `applescript`, and/or parse returns `None`.

### Task 2: Vendor and Register the Grammar

**Files:**

- Modify: `.gitmodules`
- Add: `vendor/tree-sitter-applescript`
- Modify: `src/Fedit/Highlight.fs`
- Modify: `src/Fedit/Fedit.fsproj`
- Add: `src/Fedit/Resources/queries/applescript/highlights.scm`

- [ ] **Step 1: Add the submodule**

Run:

```bash
git submodule add https://github.com/helgesverre/tree-sitter-applescript.git vendor/tree-sitter-applescript
git -C vendor/tree-sitter-applescript checkout 7a5dce5ee820a820d6fabf1f0b46fb83f75ab855
```

- [ ] **Step 2: Add a minimal first-party highlight query**

Create `src/Fedit/Resources/queries/applescript/highlights.scm` with captures for comments, strings, numbers, booleans, identifiers, function-like names, keywords, and operators. Use capture names already handled by `Highlight.resolveCapture`. Do not capture punctuation unless the grammar exposes those literal tokens; the first-party query should compile cleanly through `tree-sitter query`.

- [ ] **Step 3: Embed the query resource**

Add this item to `src/Fedit/Fedit.fsproj`:

```xml
<EmbeddedResource Include="Resources/queries/applescript/highlights.scm">
  <LogicalName>fedit.queries.applescript.highlights.scm</LogicalName>
</EmbeddedResource>
```

- [ ] **Step 4: Register the external grammar**

Add this tuple to the external grammar list in `HighlightRegistry.tryCreate`:

```fsharp
"applescript", "tree-sitter-applescript", "tree_sitter_applescript"
```

- [ ] **Step 5: Detect `.applescript`**

Add this extension mapping in `Highlight.detectLanguage`:

```fsharp
| ".applescript" -> Some "applescript"
```

Do not map `.scpt`; it is commonly a compiled binary script format.

### Task 3: Update Native Build Recipes

**Files:**

- Modify: `justfile`

- [ ] **Step 1: Add AppleScript to host grammar builds**

Add this entry to the `build-grammars` loop:

```bash
"applescript|vendor/tree-sitter-applescript/src|parser.c scanner.c"
```

- [ ] **Step 2: Add AppleScript to all RID cross-build loops**

Add the same entry to the loops in `_build-grammar-osx-arm64`, `_build-grammar-osx-x64`, `_build-grammar-linux-x64`, `_build-grammar-linux-arm64`, and `_build-grammar-win-x64`.

### Task 4: Build and Verify

**Files:**

- Generated: `src/Fedit/runtimes/<host-rid>/native/libtree-sitter-applescript.<ext>`

- [ ] **Step 1: Build the host native grammar**

Run:

```bash
just build-grammars
```

Expected: output includes `applescript OK`.

- [ ] **Step 2: Run focused highlight tests**

Run:

```bash
PATH="$PWD/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --nologo --filter "FullyQualifiedName~HighlightTests"
```

Expected: PASS.

- [ ] **Step 3: Run broad non-theme tests**

Run:

```bash
PATH="$PWD/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --nologo --filter "FullyQualifiedName!~ThemesTests"
```

Expected: PASS. Full `just test` is expected to keep failing on the pre-existing theme assertions until those unrelated changes are fixed.

- [ ] **Step 4: Check whitespace**

Run:

```bash
git diff --check
```

Expected: no output.
