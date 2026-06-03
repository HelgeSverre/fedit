# Website Style Generalization Sweep Implementation Plan

> **STATUS: SHIPPED, 2026-06-03.** The Task 1 audit collapsed the 11-task plan
> to three real changes. Most candidate patterns were already shared in
> `global.css` (`section__head`/`section__lede`, `.dl-grid`, `.stack`, base
> `kbd`) with no local duplicates, and the chips/table chrome turned out to be
> single-use after Phase A (`/plugins` uses a `<select>`, not chips, and has no
> table). What actually shipped:
>
> - **`.card` surface** → `global.css`; home read-next grid, `PluginCard`,
>   `ThemeCard` compose it (`docs .doc-cards` stays scoped — intentionally
>   flat). Commit `6a8b51e`.
> - **`.frame` chrome** → `global.css`; `TuiFrame` and `developer.astro`'s
>   anatomy block (byte-identical chrome) compose it. Commit `644eb5d`.
> - **`brand.astro` `!important` cleanup** → the four overrides were dropped by
>   wrapping two base resets in `:where()`. Commit `566e951`.
>
> `.card`/`.frame` are self-documented inline in `global.css`, so the separate
> `STYLES.md` (Task 11) was intentionally skipped as over-engineering. Each
> change was verified by screenshot (zero visual regression). The task list
> below is the original plan, kept as history — the audit superseded its scope.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the website's per-component inline `<style>` blocks into a documented shared-utility vocabulary, keeping component-scoped CSS only where styling is genuinely unique — with zero visual regression.

**Architecture:** Extract recurring patterns (section heads, definition grids, cards, tables, kbd, chips, code frames) from page/component `<style>` blocks into a small set of CSS partials imported by `global.css` (`utilities.css`, `components.css`). Each extraction is paired with a before/after visual check. The audit drives the work — nothing is generalized until it appears in 2+ places.

**Tech Stack:** Astro 5 + bun, plain CSS (custom properties already in `global.css`). Build/lint via `just website::*`.

This plan implements **Phase B** of [`docs/superpowers/specs/2026-05-30-docs-subsite-and-style-sweep-design.md`](../specs/2026-05-30-docs-subsite-and-style-sweep-design.md). It runs **after** Phase A (the docs subsite plan), so the new docs pages are included in the sweep.

---

## Working principles

- **Audit before extracting.** Only promote a pattern to a shared utility when it appears in ≥2 components. A one-off stays scoped.
- **Zero visual regression.** Every task ends with a side-by-side visual check of the affected pages in `just website::dev`. If anything shifts, the extraction is wrong — fix the utility, don't accept the drift.
- **One pattern per task.** Small, reversible commits. If an extraction goes sideways, `git revert` touches one pattern.
- **Respect the brand rules.** `brand/USAGE.md` (banned colors/patterns), `brand/voice.md`, the `.tui` calt/liga alignment rules, `--accent-soft` focus rings, and `NO_COLOR`/no-accent legibility must survive every change.
- **Don't restructure tokens.** `global.css`'s `:root` token block (neutrals, accent, semantic vars, spacing, timing) is the source of truth and stays. We add utilities/components that _consume_ tokens; we don't rename tokens.

---

## File structure

| File                                                                | Change     | Responsibility                                                                                                         |
| ------------------------------------------------------------------- | ---------- | ---------------------------------------------------------------------------------------------------------------------- |
| `website/src/styles/utilities.css`                                  | **create** | Low-level reusable utilities (section head, stacks if not already, kbd, chips, card, table).                           |
| `website/src/styles/components.css`                                 | **create** | Slightly higher-level shared component classes that span multiple pages (e.g. code/TUI frame chrome, definition grid). |
| `website/src/styles/global.css`                                     | modify     | `@import` the two partials; keep the token block + base typography.                                                    |
| `website/src/pages/*.astro`, `website/src/pages/docs/*.astro`       | modify     | Remove now-duplicated rules from their `<style>` blocks; keep unique rules.                                            |
| `website/src/components/*.astro`                                    | modify     | Same — thin the `<style>` blocks.                                                                                      |
| `website/docs/STYLES.md` _(or a comment header in `utilities.css`)_ | **create** | Document the utility vocabulary so future pages reuse it.                                                              |

> Whether to split into `utilities.css` + `components.css` or fold everything into `global.css` is a judgment call made in Task 1 based on the audit's volume. Default: two partials, imported at the top of `global.css`. If the audit yields only a handful of utilities, fold into `global.css` and skip the partials — note the choice in the Task 1 commit.

---

## Task 1: Audit — catalog inline styles and decide the shared vocabulary

No code changes. Produce the worklist that drives Tasks 2+.

**Files:** none modified (analysis only).

- [ ] **Step 1: Establish the green baseline**

Run: `just website::check && just website::build`
Expected: PASS. Record that the site builds clean before any refactor.

- [ ] **Step 2: Capture baseline screenshots**

Run `just website::dev`, then for each page take a reference screenshot (or note exact visual state) at desktop + mobile widths:
`/`, `/commands`, `/themes`, `/plugins`, `/brand`, `/changelog`, `/docs`, `/docs/plugins`, `/docs/keybindings`, `/docs/architecture`.
These are the regression references for every later task.

- [ ] **Step 3: Inventory every `<style>` block**

Run: `grep -rln "<style>" website/src/pages website/src/components website/src/layouts`
For each file, list the selectors it defines. Build a table of **pattern → files it appears in**. Expected recurring patterns (confirm against the real output):

- `.section__head` / `.section__lede` (section intros)
- `.dl-grid` (definition grids — `commands.astro`, maybe docs)
- card surfaces (`.doc-cards`, `PluginCard`, `ThemeCard`, plugin grid)
- `kbd` styling (`commands.astro`, `/docs/keybindings`)
- filter chips (`/plugins`, `/docs/keybindings`)
- table chrome (`/plugins`, `/docs/keybindings`)
- code / TUI frame chrome (`CodeBlock`, `TuiFrame`, `developer`→`docs/plugins`)
- the `!important` override cluster in `brand.astro`

- [ ] **Step 4: Decide the extraction list**

For each pattern appearing in ≥2 files, mark it for extraction and assign it to `utilities.css` (low-level) or `components.css` (multi-page component). Patterns in exactly 1 file stay scoped. Write this decision list into the PR description / a scratch note — it is the task order for the rest of the plan.

- [ ] **Step 5: Check what's already shared**

Run: `grep -n "\.section\b\|\.container\|\.stack\|\.dl-grid\|kbd\|section__head\|section__lede" website/src/styles/global.css`
Some utilities (`.section`, `.container`, `.stack`, possibly `.dl-grid`, `section__head`) may already live in `global.css`. Do NOT re-extract those — note them as "already shared" so later tasks only remove _component-local duplicates_ of them.

- [ ] **Step 6: Commit the audit note** (if you wrote a `STYLES.md` draft)

```bash
git add website/docs/STYLES.md 2>/dev/null || true
git commit -m "docs(website): style audit + shared-utility extraction plan" --allow-empty
```

---

## Task 2: Scaffold the partials and wire them into global.css

**Files:**

- Create: `website/src/styles/utilities.css`
- Create: `website/src/styles/components.css`
- Modify: `website/src/styles/global.css`

- [ ] **Step 1: Create empty partials with a documenting header**

`website/src/styles/utilities.css`:

```css
/* fedit — shared utilities
 * Low-level, single-purpose classes that consume the tokens in global.css.
 * Add a class here only when a pattern appears in 2+ components.
 * Document each utility group with a one-line comment.
 */
```

`website/src/styles/components.css`:

```css
/* fedit — shared components
 * Multi-page component classes (cards, frames, definition grids, tables).
 * Higher-level than utilities.css; still token-driven, no hardcoded colors.
 */
```

- [ ] **Step 2: Import them from global.css**

At the **top** of `website/src/styles/global.css` (CSS `@import` must precede other rules), add:

```css
@import "./utilities.css";
@import "./components.css";
```

> Astro/Vite resolves relative `@import` in CSS. If the build complains about `@import` ordering, move them to the very first lines of the file (before the `/* fedit — global styles */` comment is fine; before any selector is required).

- [ ] **Step 3: Build check**

Run: `just website::check && just website::build`
Expected: PASS, no visual change (the partials are empty).

- [ ] **Step 4: Commit**

```bash
git add website/src/styles/utilities.css website/src/styles/components.css website/src/styles/global.css
git commit -m "chore(website): scaffold utilities.css + components.css partials"
```

---

## Tasks 3–N: Extract one pattern per task

> Each extraction follows the **identical loop** below. Repeat it once per pattern from the Task 1 list. The example uses filter chips (shared by `/plugins` and `/docs/keybindings`); substitute the pattern and files for each subsequent task. Do NOT skip the visual check — it is the regression proof.

### Extraction loop (template)

**Files (per pattern):**

- Modify: `website/src/styles/utilities.css` _or_ `components.css` (add the shared class)
- Modify: each `*.astro` that currently defines the pattern locally (remove the duplicate)

- [ ] **Step A: Add the canonical class to the partial**

Move the most complete/correct version of the rules into the partial under a commented group. Example (chips → `utilities.css`):

```css
/* Filter chips: a row of toggle buttons; .is-active fills with accent. */
.chips {
    display: flex;
    gap: 4px;
}
.chips button {
    padding: 6px 12px;
    border: 1px solid var(--border);
    border-radius: 2px;
    color: var(--fg-muted);
    font-size: 13px;
}
.chips button.is-active {
    color: var(--accent-fg);
    background: var(--accent);
    border-color: var(--accent);
}
```

- [ ] **Step B: Point markup at the shared class**

In each consuming page, rename the local class to the shared one (e.g. `.kb__chips` → `.chips`) in the markup, OR keep the existing class name and remove only the duplicated declarations if the name is already generic. Prefer the shared name; update `class="…"` and any `:class` Alpine bindings accordingly.

- [ ] **Step C: Delete the now-duplicated rules from each `<style>` block**

Remove the local declarations that the shared class now provides. Leave any genuinely page-unique rules (e.g. chip spacing specific to one layout) scoped, ideally as a modifier (`.chips--compact`).

- [ ] **Step D: Build + visual regression check**

Run: `just website::check`
Then `just website::dev` and compare each affected page against the Task 1 baseline screenshot at desktop + mobile. Expected: pixel-identical (or intentionally identical). If anything shifted — spacing, color, focus ring, hover — the shared class is missing a rule; fix the partial, not the page.

- [ ] **Step E: Commit**

```bash
git add website/src/styles website/src/pages website/src/components
git commit -m "refactor(website): extract <pattern> into shared <partial>"
```

### Recommended task order (one extraction each)

Do the safe, high-duplication patterns first:

- [ ] **Task 3 — `.section__head` / `.section__lede`** (if not already fully in `global.css`): consolidate any per-page copies. Touches most pages.
- [ ] **Task 4 — `kbd`**: unify the `<kbd>` chip styling used by `/commands` and `/docs/keybindings`. Put it in `utilities.css` as a bare `kbd { … }` base rule (it is an element, so this is global — confirm no page wants a different kbd look).
- [ ] **Task 5 — filter chips (`.chips`)**: shared by `/plugins` and `/docs/keybindings` (the template example above).
- [ ] **Task 6 — data table chrome (`.data-table`)**: the `<table>` border/padding/sort-header pattern shared by `/plugins` and `/docs/keybindings` → `components.css`.
- [ ] **Task 7 — card surface (`.card`)**: the bordered, hover-accent block shared by `/docs` hub cards, `PluginCard`, `ThemeCard` → `components.css` with modifiers for the differences.
- [ ] **Task 8 — definition grid (`.dl-grid`)**: ensure one canonical version; remove duplicates.
- [ ] **Task 9 — code / TUI frame chrome**: shared chrome between `CodeBlock.astro`, `TuiFrame.astro`, and the moved `/docs/plugins` anatomy code blocks → `components.css`.

> Each is one pass through the extraction loop (Steps A–E). If the audit (Task 1) finds a pattern listed here only appears once, **skip it** and note why. If it finds an additional ≥2-occurrence pattern not listed, add a task for it.

---

## Task 10: Resolve the `brand.astro` `!important` overrides

`brand.astro` carries `!important` rules (e.g. `margin-top: …px !important`, `color: var(--fg-muted) !important`) — a specificity smell. Now that shared utilities exist, most should be expressible without `!important`.

**Files:**

- Modify: `website/src/pages/brand.astro`
- Modify: `website/src/styles/utilities.css` or `components.css` (if a shared class is the right fix)

- [ ] **Step 1: Locate the overrides**

Run: `grep -n "!important" website/src/pages/brand.astro`
List each one and why it exists (usually fighting a more-specific global rule).

- [ ] **Step 2: Remove each `!important` by fixing the root cause**

For each: either (a) the element should use a shared utility class whose specificity is appropriate, or (b) the local selector needs to be made specific enough to win without `!important`. Replace, don't suppress.

- [ ] **Step 3: Visual regression check**

Run: `just website::dev` → `/brand`. Compare against the Task 1 baseline. Expected: identical. (The whole point is no visual change — only the CSS gets cleaner.)

- [ ] **Step 4: Confirm none remain**

Run: `grep -c "!important" website/src/pages/brand.astro`
Expected: `0` (or a documented, justified minimum if one genuinely cannot be removed — add a comment explaining why).

- [ ] **Step 5: Commit**

```bash
git add website/src/pages/brand.astro website/src/styles
git commit -m "refactor(website): remove brand.astro !important overrides via shared utilities"
```

---

## Task 11: Document the vocabulary

**Files:**

- Create/Modify: `website/docs/STYLES.md` (or finalize the header comments in the partials)

- [ ] **Step 1: Write the vocabulary doc**

`website/docs/STYLES.md` listing each shared utility/component class, what it does, and an example usage snippet. Lead with the verb, no emoji (brand voice rules). Sections: Tokens (point to `global.css`), Utilities (`utilities.css`), Components (`components.css`), and "when to scope locally instead."

- [ ] **Step 2: Format + commit**

Run: `just website::format` then `just website::lint`
Expected: clean.

```bash
git add website/docs/STYLES.md
git commit -m "docs(website): document the shared style vocabulary"
```

---

## Final verification

- [ ] **Step 1: Website gate** — Run: `just website::check && just website::build` → PASS.
- [ ] **Step 2: Lint/format** — Run: `just website::format && just website::lint` → clean (second `format` pass for `.astro` if the prettier gotcha bites).
- [ ] **Step 3: Full visual regression** — `just website::dev`, walk every page from the Task 1 list at desktop + mobile, compare to baselines. Zero intended-visual change.
- [ ] **Step 4: Thinness check** — Run: `grep -rc "<style>" website/src/pages website/src/components | sort` and spot-check that the big offenders (`plugins.astro`, `brand.astro`, the docs pages) have materially fewer local rules than before. Confirm shared classes are actually used (`grep -rn "\.chips\|\.card\|\.data-table" website/src`).
- [ ] **Step 5: Accessibility/brand invariants** — focus rings still `--accent-soft` 3px; `.tui` alignment intact; pages legible with accent removed (simulate by overriding `--accent` to a neutral in devtools).

---

## Self-review checklist (done while authoring)

- **Spec coverage:** Phase B method steps 1–4 map to Task 1 (audit), Task 2 (scaffold), Tasks 3–9 (extract per pattern), Task 10 (`!important` cleanup), Task 11 (documented vocabulary). Constraints (zero regression, brand rules, no unrelated refactoring) are enforced by the per-task visual check and the final verification.
- **No placeholders:** the extraction loop is fully specified with a concrete worked example (chips); subsequent extractions reuse the identical Steps A–E with named patterns/files. The audit (Task 1) deliberately precedes extraction so file lists are real, not guessed.
- **Ordering:** depends on Phase A being merged (docs pages exist) — stated up front. Safe high-duplication patterns first; `!important` cleanup after utilities exist.
- **Reversibility:** one pattern per commit, so any regression is revertable in isolation.
- **Judgment calls flagged:** partials-vs-fold-into-global decided post-audit (Task 1/file-structure note); element-level `kbd` base rule confirmed against per-page needs (Task 4).

```

```
