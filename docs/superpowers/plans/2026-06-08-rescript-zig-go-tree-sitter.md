# ReScript, Zig, and Go Tree-Sitter Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add ReScript and Zig syntax highlighting support, and verify the existing Go support remains covered.

**Architecture:** Go stays on the bundled TreeSitter.DotNet grammar path that already supports `.go`. ReScript and Zig join the existing external grammar path used by markdown, XML, Dart, Just, Make, Astro, TOML, Sema, and AppleScript: vendored grammar submodules, host/release native build recipe entries, embedded highlight queries, registry entries, extension detection, and focused highlight tests.

**Tech Stack:** F#, xUnit, TreeSitter.DotNet, git submodules, clang/zig native grammar builds.

---

### Task 1: Add Regression Tests First

**Files:**

- Modify: `tests/Fedit.Tests/HighlightTests.fs`

- [ ] **Step 1: Add language detection cases**

Add these cases:

```fsharp
[<InlineData("component.res", "rescript")>]
[<InlineData("interface.resi", "rescript")>]
[<InlineData("main.zig", "zig")>]
[<InlineData("build.zig", "zig")>]
```

Keep the existing `.go` bundled-language test as the Go coverage point.

- [ ] **Step 2: Add registry and parse cases**

Add `rescript` and `zig` to `registry loads language without throwing`.

Add parse samples:

```fsharp
[<InlineData("rescript", "let name = \"fedit\"\nlet answer = 42")>]
[<InlineData("zig", "const std = @import(\"std\");\npub fn main() void {\n    std.debug.print(\"hi\", .{});\n}")>]
```

- [ ] **Step 3: Verify RED**

Run:

```bash
PATH="$PWD/.dotnet:$PATH" dotnet test tests/Fedit.Tests/Fedit.Tests.fsproj --nologo --filter "FullyQualifiedName~HighlightTests"
```

Expected: FAIL because ReScript and Zig are not detected or registered yet.

### Task 2: Vendor Grammars and Queries

**Files:**

- Modify: `.gitmodules`
- Add: `vendor/tree-sitter-rescript`
- Add: `vendor/tree-sitter-zig`
- Add: `src/Fedit/Resources/queries/rescript/highlights.scm`
- Add: `src/Fedit/Resources/queries/zig/highlights.scm`

- [ ] **Step 1: Add submodules pinned to current upstream heads**

Run:

```bash
git submodule add https://github.com/rescript-lang/tree-sitter-rescript.git vendor/tree-sitter-rescript
git -C vendor/tree-sitter-rescript checkout 990214a83f25801dfe0226bd7e92bb71bba1970f
git submodule add https://github.com/tree-sitter-grammars/tree-sitter-zig.git vendor/tree-sitter-zig
git -C vendor/tree-sitter-zig checkout 6479aa13f32f701c383083d8b28360ebd682fb7d
```

- [ ] **Step 2: Copy highlight queries**

Copy upstream queries:

```bash
mkdir -p src/Fedit/Resources/queries/rescript src/Fedit/Resources/queries/zig
cp vendor/tree-sitter-rescript/queries/highlights.scm src/Fedit/Resources/queries/rescript/highlights.scm
cp vendor/tree-sitter-zig/queries/highlights.scm src/Fedit/Resources/queries/zig/highlights.scm
```

If either upstream query uses unsupported captures, keep the query syntax but rely on `Highlight.resolveCapture` to ignore unknown captures.

### Task 3: Wire Runtime Loading

**Files:**

- Modify: `src/Fedit/Highlight.fs`
- Modify: `src/Fedit/Fedit.fsproj`
- Modify: `justfile`

- [ ] **Step 1: Embed query resources**

Add embedded resources for `rescript` and `zig` in `src/Fedit/Fedit.fsproj`.

- [ ] **Step 2: Register external grammars**

Add tuples:

```fsharp
"rescript", "tree-sitter-rescript", "tree_sitter_rescript"
"zig", "tree-sitter-zig", "tree_sitter_zig"
```

- [ ] **Step 3: Add extension detection**

Map:

```fsharp
| ".res"
| ".resi" -> Some "rescript"
| ".zig" -> Some "zig"
```

- [ ] **Step 4: Update native build recipes**

Add `rescript` and `zig` entries to `build-grammars` and every `_build-grammar-*` loop. Include `scanner.c` only if present.

### Task 4: Build and Verify

**Files:**

- Generated ignored sidecars under `src/Fedit/runtimes/<host-rid>/native/`

- [ ] **Step 1: Build host grammars**

Run:

```bash
just build-grammars
```

Expected output includes `rescript OK` and `zig OK`.

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

Expected: PASS. Full `just test` may still fail on unrelated theme assertions in this dirty checkout.

- [ ] **Step 4: Check whitespace**

Run:

```bash
git diff --check
```

Expected: no output.
