# Docs Subsite, Changelog, Keybindings Grid + Style Sweep — Design

**Date:** 2026-05-30
**Status:** Approved (brainstorm), pending plan authoring
**Scope:** `website/` (Astro 5 + bun) plus one small `src/Fedit/` CLI addition.

Two phases, two implementation plans, one shared design:

- **Phase A — Docs subsite, changelog, keybindings grid.** New information
  architecture (`/docs/*`), a `/changelog` page, and an interactive searchable
  keybindings datagrid fed by a new `fedit keybinds --json` CLI dump.
- **Phase B — Style generalization sweep.** Refactor inline component `<style>`
  blocks into a shared utility vocabulary in `global.css`, keeping
  component-scoped CSS only where styling is genuinely unique. Runs after Phase
  A so the new docs pages fold into the same sweep.

The deeper "how it works" rewrite is **deferred**. The current `/how` content
(thin architecture overview) is relocated as-is into `/docs/architecture` and
left as a placeholder for that future work.

---

## Current state (established conventions to follow)

- Pages live in `website/src/pages/*.astro`, wrapped by
  `src/layouts/Site.astro` (header, footer, OG/meta, fonts).
- Page-specific styling lives in per-page/per-component `<style>` blocks; the
  shared design tokens + base typography live in `src/styles/global.css`
  (~410 lines: neutrals, one accent, semantic light/dark vars, spacing scale,
  `.section`/`.container`/`.stack`/`dl-grid` utilities).
- Tabular/listing data lives in hand-maintained TS modules under
  `src/data/` with a "keep in sync with F#" header comment
  (`themes.ts`, `plugins.ts`).
- The `/plugins` page is the reference pattern for an interactive datagrid:
  Alpine.js (`alpinejs` + `@alpinejs/focus`) drives client-side
  search/sort/filter over the embedded `plugins` array.
- The CLI has a structured subcommand framework in `src/Fedit/Cli.fs`
  (`CliSubcommandSpec`, `Cli.route`, `CliOptionSpec` with `--json`-style long
  flags). Adding a `keybinds` subcommand is idiomatic.
- Keybinding source of truth (post keybindings phases 1–3): the compiled-in
  `Keymap.defaults` DSL in `src/Fedit/Keymap.fs`.

---

## Phase A

### A1. Information architecture & nav

**New routes**

| Route                | Source                                         | Layout       |
| -------------------- | ---------------------------------------------- | ------------ |
| `/docs`              | new hub/landing (cards linking each doc)       | `DocsLayout` |
| `/docs/plugins`      | current `/developer` plugin guide, moved in    | `DocsLayout` |
| `/docs/keybindings`  | new interactive searchable datagrid            | `DocsLayout` |
| `/docs/architecture` | current `/how` content, moved in (placeholder) | `DocsLayout` |
| `/changelog`         | new page                                       | `Site`       |

**Header nav change**

Current: `install · commands · themes · plugins · how · brand`
New: `install · commands · themes · plugins · docs · changelog`

- `brand` → moves to the **footer** (`Footer.astro`).
- `how` → folds into `/docs/architecture` (content moved verbatim).
- `developer` (`/developer`) → folds into `/docs/plugins` (content moved verbatim).
- `commands` → **kept** in nav as the static at-a-glance cheatsheet. The
  _interactive/searchable_ grid is the separate `/docs/keybindings` page. (The
  two are deliberately distinct: one is a printable reference, one is a
  filterable tool.)

**Redirects:** `/developer` → `/docs/plugins` and `/how` → `/docs/architecture`
(Astro static redirects in `astro.config` or a thin redirecting page), so
existing links and OG cards keep resolving.

### A2. DocsLayout

New `src/layouts/DocsLayout.astro` that wraps `Site.astro` and adds:

- A persistent left sidebar nav listing the docs (Plugins guide · Keybindings ·
  Architecture), with `aria-current="page"` on the active entry.
- A content column for the page `<slot/>`.
- Mobile (`< ~720px`): the sidebar collapses into a top disclosure
  (`<details>` or a small Alpine toggle — prefer `<details>` for no-JS).
- Sidebar entries come from a single `src/data/docs-nav.ts` array
  (`{ href, label, summary }[]`) so adding a doc is one line; the `/docs` hub
  cards read the same array.

Props mirror `Site.astro` (`title`, `description`, `image`) and pass through.

### A3. Keybindings datagrid

**Data pipeline**

1. New CLI subcommand: `fedit keybinds --json`.
    - Registered as a `CliSubcommandSpec` (`Name = "keybinds"`), with a
      `--json` `CliOptionSpec` flag.
    - Serializes **`Keymap.defaults`** (the compiled-in defaults only — not any
      user `~/.config/fedit/keybinds` overlay) to a JSON array of:
        ```json
        {
            "stroke": "ctrl+s",
            "action": "save",
            "context": "global",
            "category": "file",
            "description": "Save the active buffer"
        }
        ```
    - `stroke` via `Chord.renderStroke`; `action` via a kebab name (reuse the
      Phase-3 `parseAction` name table inverted, or a small `Action -> string`);
      `context` from `Binding.Context`.
    - `description` + `category`: the F# DSL carries neither. Add a small
      `Action -> (category * description)` table (host-side, in the keybinds
      command module or a `KeybindsMeta` helper). This is the one piece of prose
      authored for the dump.
    - Non-JSON `fedit keybinds` (no flag) prints a human-readable table (nice to
      have; keep minimal).
2. `just website::gen-keybinds` recipe runs `./fedit keybinds --json >
website/src/data/keybindings.json`. The JSON is **committed** so web builds
   never require the binary. Recipe documented as the regen step when bindings
   change.

**UI** (`/docs/keybindings`, in `DocsLayout`)

- Mirrors the `/plugins` Alpine.js pattern: embed `keybindings.json`, drive
  client-side filtering.
- Controls: a fuzzy search box (matches stroke + action + description), context
  filter chips (Global / Editor / Sidebar / Prompt), optional category chips,
  sortable columns.
- Rows render the chord via `<kbd>`, monospace, and must read correctly without
  the accent color (NO_COLOR-equivalent: hierarchy from type/weight/borders).
- Empty-state and result-count line.

### A4. Changelog

- `src/data/changelog.ts` — hand-maintained mirror of `CHANGELOG.md`
  (matches the `themes.ts`/`plugins.ts` convention), shape roughly
  `{ version, date, sections: { type, items[] }[] }[]`.
- `/changelog` page (uses `Site` layout) renders entries grouped by version,
  newest first, with dates and change-type grouping.
- A header comment records the **deferred** follow-up: semi-automate draft
  entries from the GitHub `releases.atom` feed (already referenced in
  `Site.astro`). No build-time fetch in this phase.

---

## Phase B — Style generalization sweep

Runs after Phase A.

**Goal:** thin out per-component inline `<style>` blocks by extracting the
genuinely shared patterns into a documented utility vocabulary, keeping
component-scoped CSS only where the styling is truly unique.

**Method**

1. Audit every `*.astro` page/component (incl. the new docs pages/layout) for
   `<style>` blocks. Catalog recurring patterns: section heads, `dl-grid`,
   cards, `.stack`, `kbd`, code frames/`TuiFrame`, and the `!important`
   override clusters in `brand.astro`.
2. Promote shared patterns into `global.css` (or a small `src/styles/`
   partial set — e.g. `utilities.css`, `components.css` — imported by
   `global.css`). Name them as a coherent utility vocabulary and document it
   with a short comment block.
3. Remove the now-duplicated component CSS; leave only genuinely unique rules
   scoped to their component.
4. Resolve the `brand.astro` `!important` overrides where possible (they signal
   a specificity smell that shared utilities should fix).

**Constraints**

- **Zero visual regression**, verified page-by-page (the new docs pages plus
  every existing page). Mono alignment (`.tui` calt/liga rules), focus-ring
  conventions, and `NO_COLOR`/no-accent legibility must be preserved exactly.
- Follow `brand/USAGE.md` and `brand/voice.md`; no banned colors/patterns
  introduced.
- No unrelated refactoring — only what serves consolidating the styling.

**Deliverable:** a documented shared-utility vocabulary, materially thinner
component `<style>` blocks, and a verified no-regression pass.

---

## Out of scope / deferred

- The deeper "how it works"/architecture rewrite (the page is relocated as-is).
- A real plugin registry; `/plugins` stays a preview catalog.
- Build-time/runtime fetching for the changelog (hand-maintained now).
- Serializing user keymap overlays in the CLI dump (defaults only).
- Per-theme detail pages, a dedicated `/install` page (mentioned earlier, not
  in this scope).

---

## Testing & verification

- **CLI:** unit-test that `fedit keybinds --json` emits valid JSON covering
  every `Keymap.defaults` binding, with stroke/action/context fields populated;
  `just check` (F# lint+build+test) stays green.
- **Website:** `just website::check` + `just website::lint` clean; manual
  visual pass of each page (new and existing) for Phase B no-regression;
  keybindings grid search/filter/sort verified in-browser; redirects resolve.
- **Data sync:** `just website::gen-keybinds` reproduces the committed
  `keybindings.json` with no diff (drift check).

```

```
