# Laravel Docs — Design System (Dark Mode)

Extracted from <https://laravel.com/docs/11.x/mail#driver-prerequisites> on 2026-05-30
via Chrome DevTools computed-style inspection, with the site forced into dark mode
(`html.dark`, `prefers-color-scheme: dark`).

This documents the **dark theme** of the Laravel 11.x documentation site: a calm,
near-monochrome neutral surface with a single red brand accent and a Material-style
(Palenight-family) syntax highlighting palette in code blocks.

---

## 1. Design language at a glance

- **Mood:** quiet, editorial, high-legibility. Almost no chrome color — the page is
  built from warm neutral grays. Color is reserved for the red brand accent and for
  syntax highlighting.
- **One accent:** Laravel red (`#f61500`) is the only chrome accent — used for links,
  inline code text, and active navigation. Everything else is neutral.
- **Type-led hierarchy:** weight and size carry structure, not borders or boxes.
  Generous line-height (1.8) on body copy gives a relaxed reading rhythm.
- **Monospace for all code**, with a separate richly-colored palette inside code blocks.

---

## 2. Color tokens

Values captured as rendered. Where the site emits OKLCH, the approximate sRGB hex is
noted. The page sits on a warm neutral near-black (`oklch(0.205 0 0)`).

### Surfaces & neutrals

| Token | Value | Hex (approx) | Usage |
|---|---|---|---|
| `--bg-page` | `oklch(0.205 0 0)` | `#222221` | Body / page background |
| `--bg-code` | `rgb(34, 34, 33)` | `#222221` | Code block (`<pre>`) background |
| `--bg-inline-code` | `rgb(37, 42, 55)` | `#252a37` | Inline `code` background (cool blue-gray) |
| `--border-subtle` | `oklab(0.489 … / 0.25)` | `#7c7c7c` @ 25% | Code block border, hairlines |
| `--border-nav-rail` | `oklch(0.371 0 0)` | `#4b4b4a` | Left rail / divider lines |

### Text

| Token | Value | Hex (approx) | Usage |
|---|---|---|---|
| `--text-primary` | `oklch(0.97 0 0)` | `#f5f5f4` | Default body text base |
| `--text-heading` | `rgb(241, 240, 239)` | `#f1f0ef` | H1 / H3 headings |
| `--text-label` | `rgb(238, 238, 236)` | `#eeeeec` | H2 (small section labels) |
| `--text-body` | `rgb(181, 179, 173)` | `#b5b3ad` | Paragraph copy (muted warm gray) |
| `--text-muted` | `rgb(98, 96, 91)` | `#62605b` | Code line numbers, faint meta |

### Accent

| Token | Value | Hex | Usage |
|---|---|---|---|
| `--accent` | `rgb(246, 21, 0)` | `#f61500` | Links, inline-code text, active nav item, brand |

> **One-accent rule:** the entire chrome uses a single red. Links are
> `#f61500` + underline; inline code is `#f61500` text on `#252a37`; the active
> "On this page" / sidebar entry is also `#f61500`.

---

## 3. Typography

### Font families

| Role | Stack | Notes |
|---|---|---|
| UI / body / headings | `InstrumentSans, ui-sans-serif, system-ui, sans-serif, …emoji` | Primary typeface across the site |
| Code (block + inline) | `"Geist Mono", monospace` | All code, inline and block |
| Also loaded | `IBM Plex Mono 500`, `Merriweather 400` | Present in font set (secondary/legacy use) |

Base: `font-size: 16px`, `line-height: 24px` on `body`.

### Type scale (as rendered)

| Element | Size | Weight | Line-height | Color | Margin |
|---|---|---|---|---|---|
| `h1` | 40px | 400 | 45px (1.125) | `#f1f0ef` | mb 40px |
| `h2` | 15px | 500 | 22.5px | `#eeeeec` | — (small eyebrow-style label) |
| `h3` | 20px | 500 | 25px | `#f1f0ef` | mt 40px, mb 15px |
| paragraph | 16px | 400 | 28.8px (1.8) | `#b5b3ad` | — |
| link | 16px | 400 | — | `#f61500` | `text-decoration: underline` |
| inline code | 12.8px (0.8em) | 400 | — | `#f61500` | bg `#252a37`, radius 2px, padding 0 2px |

Notable: **H1 is light-weight (400) and large (40px)**; section headings (H2) are
unusually small (15px/500) — used as quiet labels rather than loud dividers. Hierarchy
comes from size jumps and the generous body leading, not from heavy weights or rules.

---

## 4. Code blocks

| Property | Value |
|---|---|
| Background | `rgb(34, 34, 33)` `#222221` |
| Font | `"Geist Mono", monospace`, 16px / 24px |
| Border | 1px, `~#7c7c7c` @ 25% opacity |
| Border radius | 4px |
| Padding | 24px top, 20px left |
| Line numbers | `rgb(98, 96, 91)` `#62605b`, weight 500 |
| Copy button | Top-right icon affordance per block |

### Syntax highlighting palette (Material / Palenight family)

Rendered by Torchlight with a Material-Theme-style dark scheme. All tokens render at
`font-weight: 500`, `font-style: normal`.

| Token type | Color | Hex | Examples |
|---|---|---|---|
| Line numbers / comments-faint | `rgb(98, 96, 91)` | `#62605b` | `1 2 3`, gutter |
| Doc comments | `rgb(105, 112, 152)` | `#697098` | `/** … */` |
| Keywords | `rgb(199, 146, 234)` | `#c792ea` (purple) | `namespace` `use` `class` `extends` `public` `function` |
| PHP open tag | `rgb(211, 66, 62)` | `#d3423e` (red) | `<?php` |
| Special variable | `rgb(255, 85, 114)` | `#ff5572` (pink-red) | `$this` |
| Class names / types | `rgb(255, 203, 139)` | `#ffcb8b` (orange) | `Order` `Mailable` `Content` |
| Class names (alt) | `rgb(255, 203, 107)` | `#ffcb6b` (orange) | `OrderShipped` |
| Inherited / constant green | `rgb(169, 199, 125)` | `#a9c77d` | `Mailable` (extends value) |
| Functions / methods (def) | `rgb(130, 170, 255)` | `#82aaff` (blue) | `content` |
| Operators / accessors | `rgb(137, 221, 255)` | `#89ddff` (cyan) | `->` `=>` `new` `:` |
| Strings | `rgb(195, 232, 141)` | `#c3e88d` (green) | `'mail.orders.shipped'` |
| Punctuation / namespace paths | `rgb(191, 199, 213)` | `#bfc7d5` | `App\Mail;` `;` |
| Variables | `rgb(190, 197, 212)` | `#bec5d4` | `$order` |
| Brackets / quotes | `rgb(217, 245, 221)` | `#d9f5dd` | `( ) ' ` |

**Palette summary (for reuse as a theme):**

```
bg          #222221
fg          #bfc7d5
comment     #697098
keyword     #c792ea   (purple)
string      #c3e88d   (green)
function    #82aaff   (blue)
operator    #89ddff   (cyan)
class/type  #ffcb6b   (orange)
variable    #bec5d4
constant    #ff5572   (pink-red)
tag         #d3423e   (red)
linenum     #62605b
```

---

## 5. Layout & chrome

- **Three-column shell:** left navigation sidebar (section tree) · center content
  column (constrained reading measure) · right "On this page" table of contents.
- **Top bar:** Laravel wordmark (left), centered search (`⌘K`), version selector
  (`Version 11.x ▾`) and a light/dark toggle (right).
- **Content affordances:** a "Copy as markdown" action above the article; per-code-block
  copy buttons; right-rail TOC with the active anchor marked in red `#f61500`.
- **Active nav indicator:** the current page/anchor uses red text; a subtle left rail
  line (`oklch(0.371 0 0)`) groups the tree.
- **Spacing rhythm:** large vertical gaps (h1 mb 40px, h3 mt 40px) and 1.8 body leading
  produce an airy, document-like feel.

---

## 6. Reuse notes

- This is essentially a **neutral-warm-gray UI + single red accent + Material Palenight
  code theme**. To reproduce: build chrome entirely from the neutral ramp
  (`#222221` → `#62605b` → `#b5b3ad` → `#f1f0ef`), reserve `#f61500` for links/active
  state only, and drop in the syntax palette above for code.
- Keep code surfaces (`#222221`) at the same value as the page so blocks read as recessed
  panels defined by their 25%-opacity hairline border rather than by contrast.
- Inline code is the one place a *cool* tone appears (`#252a37` bg) against the otherwise
  warm neutral page — a deliberate accent for code spans.
