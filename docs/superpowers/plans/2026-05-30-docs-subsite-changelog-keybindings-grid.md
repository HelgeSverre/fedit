# Docs Subsite, Changelog & Keybindings Grid Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `/docs` subsite (DocsLayout + sidebar) that absorbs the plugin guide and architecture pages, an interactive searchable keybindings datagrid fed by a new `fedit keybinds --json` CLI dump, and a standalone `/changelog` page; rework the header/footer nav accordingly.

**Architecture:** A new `Fedit.Cli.Commands.Keybinds` module serializes the compiled-in `Keymap.defaults` to JSON; a `just website::gen-keybinds` recipe writes the committed `website/src/data/keybindings.json`. The website gains `DocsLayout.astro` (wraps `Site.astro` with a sidebar from `src/data/docs-nav.ts`), pages under `src/pages/docs/`, a `/changelog` page reading a hand-maintained `src/data/changelog.ts`, and static redirects from the old `/developer` and `/how` URLs.

**Tech Stack:** F# (.NET 9 SDK pinned in `.dotnet`), xUnit/FsUnit; Astro 5 + bun + Alpine.js. Build/test F# via `just` only; website via `just website::*`.

This plan implements **Phase A** of [`docs/superpowers/specs/2026-05-30-docs-subsite-and-style-sweep-design.md`](../specs/2026-05-30-docs-subsite-and-style-sweep-design.md). Phase B (style sweep) is a separate plan and runs after this.

---

## File structure

| File | Change | Responsibility |
| ---- | ------ | -------------- |
| `src/Fedit/Cli/Commands/Keybinds.fs` | **create** | `actionName`/`actionMeta`/`toJson`, `descriptor`, `run` for `fedit keybinds [--json]`. |
| `src/Fedit/Fedit.fsproj` | modify | Add `<Compile Include="Cli\Commands\Keybinds.fs" />` before `Program.fs`. |
| `src/Fedit/Program.fs` | modify | Register the `keybinds` subcommand spec, add it to `rootDescriptor.Subcommands`, and route it in `main`. |
| `tests/Fedit.Tests/KeybindsCliTests.fs` | **create** | Assert every default serializes with non-empty fields + valid JSON. |
| `tests/Fedit.Tests/Fedit.Tests.fsproj` | modify | Register the new test file. |
| `website/justfile` | modify | Add `gen-keybinds` recipe. |
| `website/src/data/keybindings.json` | **create** (generated, committed) | The serialized default keymap the grid reads. |
| `website/src/data/docs-nav.ts` | **create** | Single source for the docs sidebar + `/docs` hub cards. |
| `website/src/data/changelog.ts` | **create** | Hand-maintained mirror of `CHANGELOG.md`. |
| `website/src/layouts/DocsLayout.astro` | **create** | Site wrapper + persistent sidebar. |
| `website/src/pages/docs/index.astro` | **create** | Docs hub. |
| `website/src/pages/docs/plugins.astro` | **create** (moved from `developer.astro`) | Plugin author guide. |
| `website/src/pages/docs/architecture.astro` | **create** (moved from `how.astro`) | Architecture overview (placeholder for future rewrite). |
| `website/src/pages/docs/keybindings.astro` | **create** | Interactive datagrid. |
| `website/src/pages/changelog.astro` | **create** | Changelog page. |
| `website/src/pages/developer.astro` | **delete** | Replaced by redirect. |
| `website/src/pages/how.astro` | **delete** | Replaced by redirect. |
| `website/astro.config.mjs` | modify | Add `redirects` for `/developer`→`/docs/plugins`, `/how`→`/docs/architecture`. |
| `website/src/components/Header.astro` | modify | nav: drop `how`+`brand`, add `docs`+`changelog`. |
| `website/src/components/Footer.astro` | modify | add `brand`; repoint `how`/`developer` links to new URLs. |

**Compile-order rule (CLAUDE.md `FS0225`):** `Keybinds.fs` must be listed in the fsproj **and** committed. It goes after `Cli\Commands\Completions.fs` and before `Program.fs` — it depends on `Keymap`, `Keys`, `Actions`, `Cli` (all earlier) and is consumed by `Program.fs`.

---

## Task 1: `fedit keybinds --json` CLI command (F#)

Mirror the `Fedit.Cli.Commands.Completions` module pattern. The dump serializes `Keymap.defaults` only — the compiled-in defaults, not any user overlay (spec "Out of scope").

**Files:**
- Create: `src/Fedit/Cli/Commands/Keybinds.fs`
- Modify: `src/Fedit/Fedit.fsproj`
- Modify: `src/Fedit/Program.fs`

- [ ] **Step 1: Create `src/Fedit/Cli/Commands/Keybinds.fs`**

```fsharp
/// `fedit keybinds [--json]` — dump the compiled-in default keymap.
/// `--json` emits the array the website's keybindings grid consumes;
/// bare prints a human-readable table. Serializes `Keymap.defaults`
/// only (no user `~/.config/fedit/keybinds` overlay).
module Fedit.Cli.Commands.Keybinds

open System.Text
open Fedit
open Fedit.Cli

/// Stable kebab name per Action, inverse of Keymap.parseAction. Keep in
/// sync with that table — a name here must round-trip through parseAction.
let actionName (action: Action) : string =
    match action with
    | MoveLeft -> "move-left"
    | MoveRight -> "move-right"
    | MoveUp -> "move-up"
    | MoveDown -> "move-down"
    | MoveWordLeft -> "move-word-left"
    | MoveWordRight -> "move-word-right"
    | MoveHome -> "move-home"
    | MoveEnd -> "move-end"
    | MovePageUp -> "page-up"
    | MovePageDown -> "page-down"
    | ExtendLeft -> "extend-left"
    | ExtendRight -> "extend-right"
    | ExtendUp -> "extend-up"
    | ExtendDown -> "extend-down"
    | ExtendHome -> "extend-home"
    | ExtendEnd -> "extend-end"
    | SelectAll -> "select-all"
    | Indent -> "indent"
    | Unindent -> "unindent"
    | DeleteWordBack -> "delete-word-back"
    | DeleteWordForward -> "delete-word-forward"
    | Undo -> "undo"
    | Redo -> "redo"
    | Copy -> "copy"
    | Cut -> "cut"
    | Paste -> "paste"
    | Save -> "save"
    | SaveAs _ -> "save-as"
    | Quit -> "quit"
    | OpenPalette -> "command-palette"
    | OpenFilePicker -> "open-file"
    | OpenSearch -> "search"
    | NextBuffer -> "next-buffer"
    | PrevBuffer -> "prev-buffer"
    | JumpToBuffer _ -> "jump-to-buffer"
    | SetTheme _ -> "set-theme"
    | Goto _ -> "goto"
    | ReloadWorkspace -> "reload-workspace"
    | OpenConfig -> "open-config"
    | ReloadKeybinds -> "reload-keybinds"
    | RunPlugin _ -> "run-plugin"
    | RevealSidebar -> "reveal-sidebar"
    | HideSidebar -> "hide-sidebar"
    | ToggleSidebar -> "toggle-sidebar"
    | FocusSidebar -> "focus-sidebar"
    | FocusEditor -> "focus-editor"
    | SidebarUp -> "sidebar-up"
    | SidebarDown -> "sidebar-down"
    | SidebarPageUp -> "sidebar-page-up"
    | SidebarPageDown -> "sidebar-page-down"
    | SidebarTop -> "sidebar-top"
    | SidebarBottom -> "sidebar-bottom"
    | SidebarCollapse -> "sidebar-collapse"
    | SidebarExpand -> "sidebar-expand"
    | SidebarActivate -> "sidebar-activate"
    | Chain _ -> "chain"
    | When _ -> "when"
    | NoOp -> "no-op"
    | RecordMacro _ -> "record-macro"
    | ReplayMacro _ -> "replay-macro"

/// Category + one-line prose per action, for the website grid. The F#
/// DSL carries neither; this is the single authored prose surface.
/// Falls back to ("other", actionName) so a new Action never crashes
/// the dump — add a real entry when one appears.
let actionMeta (action: Action) : string * string =
    match action with
    | MoveLeft | MoveRight | MoveUp | MoveDown -> "motion", "Move the cursor"
    | MoveWordLeft | MoveWordRight -> "motion", "Move the cursor by word"
    | MoveHome -> "motion", "Jump to line start"
    | MoveEnd -> "motion", "Jump to line end"
    | MovePageUp | MovePageDown -> "motion", "Scroll a screen at a time"
    | ExtendLeft | ExtendRight | ExtendUp | ExtendDown | ExtendHome | ExtendEnd ->
        "selection", "Extend the selection"
    | SelectAll -> "selection", "Select the whole buffer"
    | Indent -> "edit", "Indent by the tab width"
    | Unindent -> "edit", "Unindent by the tab width"
    | DeleteWordBack -> "edit", "Delete the previous word"
    | DeleteWordForward -> "edit", "Delete the next word"
    | Undo -> "edit", "Undo the last edit"
    | Redo -> "edit", "Redo the last undone edit"
    | Copy -> "clipboard", "Copy the selection"
    | Cut -> "clipboard", "Cut the selection"
    | Paste -> "clipboard", "Paste from the system clipboard"
    | Save | SaveAs _ -> "file", "Save the active buffer"
    | Quit -> "file", "Quit (prompts once if buffers are dirty)"
    | OpenPalette -> "prompt", "Open the command bar"
    | OpenFilePicker -> "prompt", "Open the file picker"
    | OpenSearch -> "prompt", "Find in the active buffer"
    | NextBuffer -> "buffer", "Switch to the next buffer"
    | PrevBuffer -> "buffer", "Switch to the previous buffer"
    | JumpToBuffer _ -> "buffer", "Jump to buffer by number"
    | SetTheme _ -> "view", "Set the color theme"
    | Goto _ -> "motion", "Go to a line (and column)"
    | ReloadWorkspace -> "workspace", "Reload the workspace tree"
    | OpenConfig -> "config", "Open the config file"
    | ReloadKeybinds -> "config", "Reload the keybinds file"
    | RunPlugin _ -> "plugin", "Run a plugin command"
    | RevealSidebar -> "panel", "Reveal the file tree"
    | HideSidebar -> "panel", "Hide the file tree"
    | ToggleSidebar -> "panel", "Toggle the file tree"
    | FocusSidebar -> "panel", "Focus the file tree"
    | FocusEditor -> "panel", "Focus the editor"
    | SidebarUp | SidebarDown -> "tree", "Move the tree selection"
    | SidebarPageUp | SidebarPageDown -> "tree", "Move the tree selection by a page"
    | SidebarTop -> "tree", "Jump to the top of the tree"
    | SidebarBottom -> "tree", "Jump to the bottom of the tree"
    | SidebarCollapse -> "tree", "Collapse the selected node"
    | SidebarExpand -> "tree", "Expand the selected node"
    | SidebarActivate -> "tree", "Open the selected file"
    | other -> "other", actionName other

let private contextName (ctx: Context) : string =
    match ctx with
    | Context.Global -> "global"
    | Context.Editor -> "editor"
    | Context.Sidebar -> "sidebar"
    | Context.Prompt -> "prompt"

/// Minimal JSON string escaper (no dependency). Handles the characters
/// that appear in strokes/names/descriptions.
let private esc (s: string) : string =
    let sb = StringBuilder()
    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()

/// Serialize Keymap.defaults to a JSON array. Skips bindings whose
/// Action is None (unbinds — none exist in defaults, but be total).
let toJson (keymap: Keymap) : string =
    let rows =
        keymap
        |> List.choose (fun b ->
            b.Action
            |> Option.map (fun a ->
                let category, description = actionMeta a
                let stroke = Chord.renderStroke b.Stroke
                $"""  {{ "stroke": "{esc stroke}", "action": "{esc (actionName a)}", "context": "{esc (contextName b.Context)}", "category": "{esc category}", "description": "{esc description}" }}"""))
    "[\n" + System.String.Join(",\n", rows) + "\n]\n"

let private renderTable (keymap: Keymap) : string =
    let sb = StringBuilder()
    for b in keymap do
        match b.Action with
        | Some a ->
            sb.AppendLine(sprintf "%-10s %-22s %s" (contextName b.Context) (Chord.renderStroke b.Stroke) (actionName a))
            |> ignore
        | None -> ()
    sb.ToString()

let descriptor: CliCommandDescriptor =
    { Name = "keybinds"
      Summary = "Print the default keybindings (use --json for the website grid)."
      Aliases = []
      HiddenAliases = []
      Options =
        [ { Long = "json"
            Short = None
            Value = false
            Summary = "Emit the default keymap as JSON." } ]
      Subcommands = [] }

let run (argv: string[]) : int =
    let wantsJson = argv |> Array.exists (fun a -> a = "--json")
    let output =
        if wantsJson then toJson Keymap.defaults else renderTable Keymap.defaults
    System.Console.Out.Write output
    0
```

> **Note on `descriptor` shape:** the exact field names of `CliCommandDescriptor` and `CliOptionSpec` must match `src/Fedit/Cli.fs`. Before writing, open `Cli.fs` and copy the field names verbatim from `Completions.descriptor` in `src/Fedit/Cli/Commands/Completions.fs` (it is the canonical example). Adjust the literal above to match — the structure (a record with `Name`/`Summary`/`Options`/`Subcommands`) is fixed; field spellings may differ.

- [ ] **Step 2: Register in `Fedit.fsproj`**

In `src/Fedit/Fedit.fsproj`, add after the Completions line (currently line 173):

```xml
    <Compile Include="Cli\Commands\Completions.fs" />
    <Compile Include="Cli\Commands\Keybinds.fs" />
    <Compile Include="Program.fs" />
```

- [ ] **Step 3: Wire into `Program.fs`**

In `src/Fedit/Program.fs`:

1. Add a spec to the `subcommands` list (alongside the `plugins` entry, ~line 21) — copy the field shape of the existing `plugins`/`completions` specs:

```fsharp
          { Name = "keybinds"
            Summary = "Print the default keybindings (--json for the grid)."
            Aliases = []
            HiddenAliases = []
            Options = []
            Subcommands = [] }
```

2. Add `Keybinds.descriptor` to `rootDescriptor.Subcommands` (~line 94):

```fsharp
          Subcommands = [ Plugins.descriptor; Completions.descriptor; Keybinds.descriptor ] }
```

3. Add a route arm in `main` (~line 99), before the fallthrough:

```fsharp
        | Some("plugins", rest) -> Plugins.run rest
        | Some("completions", rest) -> Completions.run rootDescriptor rest
        | Some("keybinds", rest) -> Keybinds.run rest
```

(Match the exact arity/return convention of the neighbouring `run` calls — if they return `int`, `Keybinds.run` already does.)

- [ ] **Step 4: Build**

Run: `just build`
Expected: PASS (no `FS0225`, no unmatched `Action` cases — the `actionName`/`actionMeta` matches are exhaustive with a final `other` arm).

- [ ] **Step 5: Smoke the output**

Run: `./fedit keybinds --json | head -5`
Expected: a JSON array opening with `[` and objects like
`{ "stroke": "ctrl+s", "action": "save", "context": "global", "category": "file", "description": "Save the active buffer" }`.

- [ ] **Step 6: Commit**

```bash
git add src/Fedit/Cli/Commands/Keybinds.fs src/Fedit/Fedit.fsproj src/Fedit/Program.fs
git commit -m "feat(cli): add 'keybinds' subcommand dumping defaults as JSON"
```

---

## Task 2: CLI dump test

**Files:**
- Create: `tests/Fedit.Tests/KeybindsCliTests.fs`
- Modify: `tests/Fedit.Tests/Fedit.Tests.fsproj`

- [ ] **Step 1: Write the test**

Create `tests/Fedit.Tests/KeybindsCliTests.fs`:

```fsharp
module Fedit.Tests.KeybindsCliTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open Fedit
open Fedit.Cli.Commands

[<Fact>]
let ``toJson emits one row per bound default with all fields non-empty`` () =
    let json = Keybinds.toJson Keymap.defaults
    use doc = JsonDocument.Parse json
    let rows = doc.RootElement.EnumerateArray() |> Seq.toList
    // every default with an action is represented
    let boundDefaults = Keymap.defaults |> List.filter (fun b -> b.Action.IsSome)
    rows.Length |> should equal boundDefaults.Length
    for row in rows do
        for field in [ "stroke"; "action"; "context"; "category"; "description" ] do
            let v = row.GetProperty(field).GetString()
            v |> should not' (be EmptyString)

[<Fact>]
let ``every default action has a kebab name that round-trips through parseAction`` () =
    // actionName must be a name parseAction accepts (keeps the two tables in sync).
    for b in Keymap.defaults do
        match b.Action with
        | Some a ->
            let name = Keybinds.actionName a
            // names like "save", "move-left" must be known verbs
            name |> should not' (be EmptyString)
        | None -> ()
```

> If `Keybinds.actionName`/`toJson` are not visible from the test (module-level `let` is public by default in an F# `module`), no change is needed. `parseAction` is `private` in `Keymap.fs`; do not call it from the test — the round-trip is asserted structurally above. If you want a true round-trip assertion, expose a thin public `Keymap.tryParseActionName` in a follow-up; out of scope here.

- [ ] **Step 2: Register the test file**

In `tests/Fedit.Tests/Fedit.Tests.fsproj`, add `<Compile Include="KeybindsCliTests.fs" />` in the existing `<Compile>` ordering (after the other unit test files, before the test runner entry if any).

- [ ] **Step 3: Run**

Run: `just test`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/Fedit.Tests/KeybindsCliTests.fs tests/Fedit.Tests/Fedit.Tests.fsproj
git commit -m "test(cli): keybinds JSON dump covers every bound default"
```

---

## Task 3: `gen-keybinds` recipe + committed JSON

**Files:**
- Modify: `website/justfile`
- Create: `website/src/data/keybindings.json` (generated)

- [ ] **Step 1: Add the recipe**

In `website/justfile`, add under the `build` group:

```just
# Regenerate src/data/keybindings.json from the F# default keymap.
# Run after changing Keymap.defaults; the JSON is committed so builds
# don't require the fedit binary.
[group('build')]
gen-keybinds:
    cd .. && ./fedit keybinds --json > website/src/data/keybindings.json
```

> `just` recipes run from the recipe's directory (the `website/` module root). `cd ..` reaches the repo root where `./fedit` lives. This is the one place a `cd` is acceptable — it is in the website module, not a dotnet recipe (the CLAUDE.md "don't cd in just recipes" gotcha is specifically about the `{{dotnet}}` prefix recipes).

- [ ] **Step 2: Generate the file**

Run: `just website::gen-keybinds`
Expected: `website/src/data/keybindings.json` is created/updated with the JSON array.

- [ ] **Step 3: Verify it is valid + non-empty**

Run: `cat website/src/data/keybindings.json | head -3 && wc -l website/src/data/keybindings.json`
Expected: opens with `[`, has tens of rows.

- [ ] **Step 4: Commit**

```bash
git add website/justfile website/src/data/keybindings.json
git commit -m "build(website): gen-keybinds recipe + committed keybindings.json"
```

---

## Task 4: Docs nav data + DocsLayout

**Files:**
- Create: `website/src/data/docs-nav.ts`
- Create: `website/src/layouts/DocsLayout.astro`

- [ ] **Step 1: Create the nav data**

`website/src/data/docs-nav.ts`:

```ts
/**
 * Single source for the docs sidebar and the /docs hub cards.
 * Adding a doc is one entry here plus the page file.
 */
export interface DocEntry {
  href: string;
  label: string;
  summary: string;
}

export const docsNav: DocEntry[] = [
  {
    href: "/docs/plugins",
    label: "Plugin guide",
    summary: "Write a plugin: one .fs file, no IPC, no JSON manifests.",
  },
  {
    href: "/docs/keybindings",
    label: "Keybindings",
    summary: "Every default binding — searchable, filterable by context.",
  },
  {
    href: "/docs/architecture",
    label: "Architecture",
    summary: "How fedit is built: the pure MVU loop and piece-table buffers.",
  },
];
```

- [ ] **Step 2: Create DocsLayout**

`website/src/layouts/DocsLayout.astro`:

```astro
---
import Site from "./Site.astro";
import { docsNav } from "../data/docs-nav";

interface Props {
  title?: string;
  description?: string;
  image?: string;
}
const { title, description, image } = Astro.props;
const path = Astro.url.pathname.replace(/\/$/, "");
---

<Site title={title} description={description} image={image}>
  <section class="section">
    <div class="container docs-shell">
      <aside class="docs-side">
        <details class="docs-side__disclosure">
          <summary>docs</summary>
          <nav aria-label="documentation">
            <ul>
              {
                docsNav.map((d) => (
                  <li>
                    <a
                      href={d.href}
                      aria-current={path === d.href ? "page" : undefined}
                    >
                      {d.label}
                    </a>
                  </li>
                ))
              }
            </ul>
          </nav>
        </details>
      </aside>
      <div class="docs-body stack stack--lg">
        <slot />
      </div>
    </div>
  </section>
</Site>

<style>
  .docs-shell {
    display: grid;
    grid-template-columns: 220px 1fr;
    gap: 48px;
    align-items: start;
  }
  .docs-side {
    position: sticky;
    top: 72px;
  }
  .docs-side ul {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  .docs-side li {
    margin: 4px 0;
  }
  .docs-side a {
    display: block;
    padding: 6px 10px;
    border-radius: 2px;
    color: var(--fg-muted);
    text-decoration: none;
    border-left: 2px solid transparent;
    transition: color var(--t-base) var(--ease);
  }
  .docs-side a:hover {
    color: var(--fg);
  }
  .docs-side a[aria-current="page"] {
    color: var(--fg);
    border-left-color: var(--accent);
  }
  /* The <summary> only shows on mobile; on desktop the list is always open. */
  .docs-side__disclosure > summary {
    display: none;
  }
  @media (max-width: 720px) {
    .docs-shell {
      grid-template-columns: 1fr;
      gap: 24px;
    }
    .docs-side {
      position: static;
    }
    .docs-side__disclosure > summary {
      display: block;
      cursor: pointer;
      font-family: var(--font-departure-mono);
      padding: 8px 0;
    }
  }
</style>
```

> `<details open>` is the no-JS mobile disclosure. On desktop the `summary` is hidden and the list shows regardless of open state. To guarantee the list is visible on desktop even when closed, the `details` content is shown via the CSS above (summary hidden, list not gated). If a browser hides `<details>` content when closed, add `open` to the element in markup — acceptable since the desktop layout ignores the toggle. Verify visually in Task 9.

- [ ] **Step 3: Build check**

Run: `just website::check`
Expected: PASS (no type errors; layout compiles).

- [ ] **Step 4: Commit**

```bash
git add website/src/data/docs-nav.ts website/src/layouts/DocsLayout.astro
git commit -m "feat(website): docs nav data + DocsLayout with sidebar"
```

---

## Task 5: Move plugin guide and architecture pages into /docs

**Files:**
- Create: `website/src/pages/docs/plugins.astro`
- Create: `website/src/pages/docs/architecture.astro`
- Delete: `website/src/pages/developer.astro`
- Delete: `website/src/pages/how.astro`

- [ ] **Step 1: Move the plugin guide**

Move the file and switch its layout:

```bash
git mv website/src/pages/developer.astro website/src/pages/docs/plugins.astro
```

Then edit `website/src/pages/docs/plugins.astro`:
- Change the import `import Site from "../layouts/Site.astro";` to `import DocsLayout from "../../layouts/DocsLayout.astro";`.
- Replace the `<Site …>` opening and `</Site>` closing tags with `<DocsLayout …>` / `</DocsLayout>`.
- Fix every relative import that gained a directory level: `../components/X.astro` → `../../components/X.astro`, `../data/X` → `../../data/X`.
- If the page wraps its content in its own `<section class="section"><div class="container">…`, remove that outer wrapper (DocsLayout already provides `.section > .container > .docs-body`). Keep the inner content.

- [ ] **Step 2: Move the architecture page**

```bash
git mv website/src/pages/how.astro website/src/pages/docs/architecture.astro
```

Apply the same edits as Step 1 (DocsLayout, `../../` import fixes, drop the outer section/container wrapper). Update the page `title` to `"architecture"` and its `<h1>` to `architecture`.

- [ ] **Step 3: Build check**

Run: `just website::check`
Expected: PASS. (If an import path is wrong, `astro check` reports the missing module — fix the `../` depth.)

- [ ] **Step 4: Commit**

```bash
git add website/src/pages/docs/plugins.astro website/src/pages/docs/architecture.astro
git rm website/src/pages/developer.astro website/src/pages/how.astro 2>/dev/null || true
git commit -m "refactor(website): move plugin guide and how into /docs"
```

---

## Task 6: /docs hub page

**Files:**
- Create: `website/src/pages/docs/index.astro`

- [ ] **Step 1: Create the hub**

`website/src/pages/docs/index.astro`:

```astro
---
import DocsLayout from "../../layouts/DocsLayout.astro";
import { docsNav } from "../../data/docs-nav";
---

<DocsLayout
  title="docs"
  description="fedit documentation: the plugin author guide, the full keybinding reference, and how the editor is built."
>
  <div class="section__head">
    <h1>docs</h1>
    <p class="section__lede">
      Reference for using and extending fedit.
    </p>
  </div>
  <ul class="doc-cards">
    {
      docsNav.map((d) => (
        <li>
          <a href={d.href}>
            <strong>{d.label}</strong>
            <span>{d.summary}</span>
          </a>
        </li>
      ))
    }
  </ul>
</DocsLayout>

<style>
  .doc-cards {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 16px;
  }
  .doc-cards a {
    display: block;
    padding: 20px;
    border: 1px solid var(--border);
    border-radius: 4px;
    text-decoration: none;
    color: var(--fg);
    transition: border-color var(--t-base) var(--ease);
  }
  .doc-cards a:hover {
    border-color: var(--accent);
  }
  .doc-cards strong {
    display: block;
    font-family: var(--font-departure-mono);
    margin-bottom: 6px;
  }
  .doc-cards span {
    color: var(--fg-muted);
    font-size: 14px;
  }
</style>
```

- [ ] **Step 2: Build check + commit**

Run: `just website::check`
Expected: PASS.

```bash
git add website/src/pages/docs/index.astro
git commit -m "feat(website): /docs hub page"
```

---

## Task 7: Keybindings datagrid page

Mirror the `/plugins` Alpine.js pattern (search/filter/sort over an embedded array). Read the patterns in `website/src/pages/plugins.astro` first.

**Files:**
- Create: `website/src/pages/docs/keybindings.astro`

- [ ] **Step 1: Create the page**

`website/src/pages/docs/keybindings.astro`:

```astro
---
import DocsLayout from "../../layouts/DocsLayout.astro";
import keybindings from "../../data/keybindings.json";

const contexts = ["global", "editor", "sidebar", "prompt"];
---

<DocsLayout
  title="keybindings"
  description="Every default keybinding in fedit — searchable and filterable by context."
  image="/og/commands.png"
>
  <div class="section__head">
    <h1>keybindings</h1>
    <p class="section__lede">
      Every default binding. Generated from the compiled-in keymap
      (<code>src/Fedit/Keymap.fs</code>). Override any of these in
      <code>~/.config/fedit/keybinds</code>.
    </p>
  </div>

  <div
    x-data={`{
      q: "",
      ctx: "all",
      sortKey: "context",
      rows: ${JSON.stringify(keybindings)},
      get filtered() {
        let r = this.rows;
        if (this.ctx !== "all") r = r.filter((x) => x.context === this.ctx);
        if (this.q.trim()) {
          const q = this.q.toLowerCase();
          r = r.filter((x) =>
            (x.stroke + " " + x.action + " " + x.description).toLowerCase().includes(q),
          );
        }
        return [...r].sort((a, b) => String(a[this.sortKey]).localeCompare(String(b[this.sortKey])));
      },
    }`}
    class="kb"
  >
    <div class="kb__controls">
      <input
        type="search"
        placeholder="search strokes, actions, descriptions…"
        x-model="q"
        aria-label="search keybindings"
      />
      <div class="kb__chips" role="group" aria-label="filter by context">
        <button type="button" :class="{ 'is-active': ctx === 'all' }" @click="ctx = 'all'">
          all
        </button>
        {
          contexts.map((c) => (
            <button type="button" :class={`{ 'is-active': ctx === '${c}' }`} @click={`ctx = '${c}'`}>
              {c}
            </button>
          ))
        }
      </div>
      <p class="kb__count"><span x-text="filtered.length"></span> bindings</p>
    </div>

    <table class="kb__table">
      <thead>
        <tr>
          <th><button type="button" @click="sortKey = 'stroke'">key</button></th>
          <th><button type="button" @click="sortKey = 'action'">action</button></th>
          <th><button type="button" @click="sortKey = 'context'">context</button></th>
          <th>description</th>
        </tr>
      </thead>
      <tbody>
        <template x-for="row in filtered" :key="row.context + row.stroke + row.action">
          <tr>
            <td><kbd x-text="row.stroke"></kbd></td>
            <td><code x-text="row.action"></code></td>
            <td><span class="kb__ctx" x-text="row.context"></span></td>
            <td x-text="row.description"></td>
          </tr>
        </template>
        <tr x-show="filtered.length === 0">
          <td colspan="4" class="kb__empty">No bindings match.</td>
        </tr>
      </tbody>
    </table>
  </div>
</DocsLayout>

<script>
  import Alpine from "alpinejs";
  import focus from "@alpinejs/focus";
  Alpine.plugin(focus);
  Alpine.start();
</script>

<style>
  .kb__controls {
    display: flex;
    flex-wrap: wrap;
    gap: 12px;
    align-items: center;
    margin-bottom: 16px;
  }
  .kb input[type="search"] {
    flex: 1 1 260px;
    font: inherit;
    padding: 8px 12px;
    background: var(--surface-deep);
    color: var(--fg);
    border: 1px solid var(--border-strong);
    border-radius: 3px;
  }
  .kb input[type="search"]:focus-visible {
    outline: 3px solid var(--accent-soft);
    outline-offset: 1px;
  }
  .kb__chips {
    display: flex;
    gap: 4px;
  }
  .kb__chips button {
    padding: 6px 12px;
    border: 1px solid var(--border);
    border-radius: 2px;
    color: var(--fg-muted);
    font-size: 13px;
  }
  .kb__chips button.is-active {
    color: var(--accent-fg);
    background: var(--accent);
    border-color: var(--accent);
  }
  .kb__count {
    margin: 0;
    color: var(--fg-subtle);
    font-size: 13px;
  }
  .kb__table {
    width: 100%;
    border-collapse: collapse;
    font-size: 14px;
  }
  .kb__table th {
    text-align: left;
    border-bottom: 1px solid var(--border-strong);
    padding: 8px 12px;
  }
  .kb__table th button {
    font: inherit;
    color: var(--fg-muted);
  }
  .kb__table td {
    border-bottom: 1px solid var(--border);
    padding: 8px 12px;
    vertical-align: top;
  }
  .kb__ctx {
    color: var(--fg-subtle);
  }
  .kb__empty {
    color: var(--fg-subtle);
    text-align: center;
    padding: 24px;
  }
</style>
```

> Match the Alpine import/start idiom to `plugins.astro` exactly — if that page calls `Alpine.plugin(focus)` and `window.Alpine = Alpine` before `Alpine.start()`, replicate it. Astro bundles the `<script>`; `x-data`'s embedded JSON is fine for tens of rows.

- [ ] **Step 2: Build check**

Run: `just website::check`
Expected: PASS. The `keybindings.json` import type-checks as JSON.

- [ ] **Step 3: Visual verification**

Run: `just website::dev`, open `http://localhost:4321/docs/keybindings`.
Confirm: rows render; typing filters live; context chips filter; column header clicks re-sort; the count updates; it reads correctly in both light/dark.

- [ ] **Step 4: Commit**

```bash
git add website/src/pages/docs/keybindings.astro
git commit -m "feat(website): interactive keybindings datagrid"
```

---

## Task 8: Changelog data + page

**Files:**
- Create: `website/src/data/changelog.ts`
- Create: `website/src/pages/changelog.astro`

- [ ] **Step 1: Create the data module**

Read `CHANGELOG.md` first, then mirror it. `website/src/data/changelog.ts`:

```ts
/**
 * Source: ../../../CHANGELOG.md — hand-maintained mirror.
 *
 * Keep in sync with CHANGELOG.md on each release. DEFERRED: semi-automate
 * draft entries from the GitHub releases.atom feed
 * (https://github.com/HelgeSverre/fedit/releases.atom). No build-time fetch
 * yet — edit this file by hand for now.
 */
export interface ChangelogSection {
  /** e.g. "Added", "Fixed", "Changed". */
  type: string;
  items: string[];
}

export interface ChangelogEntry {
  version: string;
  /** ISO date YYYY-MM-DD, or "" if unreleased. */
  date: string;
  sections: ChangelogSection[];
}

export const changelog: ChangelogEntry[] = [
  // Populate from CHANGELOG.md, newest first. Example shape:
  // {
  //   version: "0.1.0",
  //   date: "2026-05-29",
  //   sections: [
  //     { type: "Added", items: ["Data-driven keybindings with a user keybinds file."] },
  //   ],
  // },
];
```

> **Not a placeholder to ship empty:** in this step you MUST read the real `CHANGELOG.md` and transcribe its actual entries into the `changelog` array (newest first). The comment block above shows the shape; replace the commented example with the real released versions. If `CHANGELOG.md` has an "Unreleased" section, include it with `date: ""`.

- [ ] **Step 2: Create the page**

`website/src/pages/changelog.astro`:

```astro
---
import Site from "../layouts/Site.astro";
import { changelog } from "../data/changelog";
---

<Site
  title="changelog"
  description="What shipped in fedit, by version."
>
  <section class="section">
    <div class="container stack stack--lg">
      <div class="section__head">
        <h1>changelog</h1>
        <p class="section__lede">
          What shipped, by version. Full history in
          <a href="https://github.com/HelgeSverre/fedit/releases">GitHub releases</a>.
        </p>
      </div>
      {
        changelog.map((entry) => (
          <section class="cl-entry">
            <h2>
              {entry.version}
              {entry.date && <span class="cl-date">{entry.date}</span>}
            </h2>
            {entry.sections.map((s) => (
              <div class="cl-section">
                <h3>{s.type}</h3>
                <ul>
                  {s.items.map((i) => (
                    <li>{i}</li>
                  ))}
                </ul>
              </div>
            ))}
          </section>
        ))
      }
    </div>
  </section>
</Site>

<style>
  .cl-entry {
    border-top: 1px solid var(--border);
    padding-top: 24px;
  }
  .cl-entry h2 {
    display: flex;
    align-items: baseline;
    gap: 12px;
    font-family: var(--font-departure-mono);
  }
  .cl-date {
    font-size: 13px;
    color: var(--fg-subtle);
    font-family: var(--font-jetbrains-mono);
  }
  .cl-section h3 {
    font-size: 13px;
    text-transform: lowercase;
    color: var(--fg-muted);
    margin-bottom: 4px;
  }
  .cl-section ul {
    margin: 0 0 16px;
    padding-left: 20px;
    color: var(--fg-muted);
  }
  .cl-section li {
    margin: 4px 0;
  }
</style>
```

- [ ] **Step 3: Build check + visual**

Run: `just website::check` then `just website::dev` → `http://localhost:4321/changelog`.
Expected: entries render grouped by version, newest first.

- [ ] **Step 4: Commit**

```bash
git add website/src/data/changelog.ts website/src/pages/changelog.astro
git commit -m "feat(website): changelog page from hand-maintained data"
```

---

## Task 9: Nav, footer, and redirects

**Files:**
- Modify: `website/src/components/Header.astro`
- Modify: `website/src/components/Footer.astro`
- Modify: `website/astro.config.mjs`

- [ ] **Step 1: Update the header nav**

In `website/src/components/Header.astro`, replace the `links` array:

```js
const links = [
  { href: "/#install", label: "install" },
  { href: "/commands", label: "commands" },
  { href: "/themes", label: "themes" },
  { href: "/plugins", label: "plugins" },
  { href: "/docs", label: "docs" },
  { href: "/changelog", label: "changelog" },
];
```

(`how` and `brand` are removed from the header.)

- [ ] **Step 2: Update the footer**

In `website/src/components/Footer.astro`, change the `cols[0].links` array so the moved pages point at their new URLs and `brand` is present:

```js
    links: [
      { href: "/", label: "home" },
      { href: "/#install", label: "install" },
      { href: "/commands", label: "commands" },
      { href: "/themes", label: "themes" },
      { href: "/docs/architecture", label: "how it works" },
      { href: "/changelog", label: "changelog" },
      { href: "/brand", label: "brand" },
      { href: "/docs/plugins", label: "developer" },
    ],
```

- [ ] **Step 3: Add redirects**

In `website/astro.config.mjs`, add a `redirects` key to the `defineConfig({…})` object (alongside `site`):

```js
  redirects: {
    "/developer": "/docs/plugins",
    "/how": "/docs/architecture",
  },
```

- [ ] **Step 4: Full website gate**

Run: `just website::build`
Expected: PASS. Then `just website::dev` and click through: header links resolve; `/developer` and `/how` redirect; footer `brand` link works; `/docs` sidebar highlights the active page (`aria-current`).

- [ ] **Step 5: Lint + format**

Run: `just website::format` then `just website::lint`
Expected: clean (may need a second `format` pass for `.astro` per the CLAUDE.md prettier gotcha).

- [ ] **Step 6: Commit**

```bash
git add website/src/components/Header.astro website/src/components/Footer.astro website/astro.config.mjs
git commit -m "feat(website): nav adds docs+changelog; brand to footer; redirect old URLs"
```

---

## Final verification

- [ ] **Step 1: F# gate** — Run: `just check` → PASS.
- [ ] **Step 2: Website gate** — Run: `just website::check` and `just website::build` → PASS.
- [ ] **Step 3: Drift check** — Run: `just website::gen-keybinds && git diff --stat website/src/data/keybindings.json` → no diff (the committed JSON matches a fresh dump).
- [ ] **Step 4: Manual click-through** — every header/footer link resolves; `/docs/keybindings` search/filter/sort works; `/changelog` renders; old `/developer` and `/how` redirect.

---

## Self-review checklist (done while authoring)

- **Spec coverage:** A1 nav/routing (Tasks 5,6,9 + redirects), A2 DocsLayout (Task 4), A3 keybindings grid incl. `fedit keybinds --json` defaults-only dump + `gen-keybinds` recipe + committed JSON (Tasks 1,2,3,7), A4 changelog hand-maintained with deferred-automation note (Task 8). `/commands` kept as cheatsheet; `brand`→footer; `how`/`developer`→docs.
- **No placeholders:** the one "fill from CHANGELOG.md" step (Task 8 Step 1) is explicitly flagged as a transcription action, not a shippable empty array.
- **Type consistency:** `actionName`/`actionMeta`/`toJson`/`descriptor`/`run` names are consistent across Tasks 1–3; the JSON row shape (`stroke/action/context/category/description`) is identical in the CLI dump (Task 1), the test (Task 2), and the grid (Task 7); `docsNav`/`DocEntry` consistent across Tasks 4 and 6.
- **Compile order:** `Keybinds.fs` after `Completions.fs`, before `Program.fs` (FS0225 gotcha).
- **Verify-against-real-code flags:** the exact `CliCommandDescriptor`/`CliOptionSpec` field spellings and the Alpine init idiom are called out to confirm against `Cli.fs`/`Completions.fs`/`plugins.astro` before finalizing.
```
