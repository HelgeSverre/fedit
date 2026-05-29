# Stolen ideas — Textual, Zed, Helix, base16, Catppuccin, VS Code

A scan of how mature theme systems are built, mined for ideas the
`revised-theme-system` plan should adopt (or deliberately reject). The
existing plan (`README.md`) is sound for the immediate problem — mono-amber
readability and an honest `CurrentLine`. This memo is about what to borrow
_before_ that schema calcifies, so we don't repaint the same corner twice.

Nothing here changes runtime. It's a decision input for the schema.

## TL;DR — the five worth stealing

1. **Derive, don't hand-author, the shade ramp.** (Textual) One accent + a
   Lab-space `lighten`/`darken` step generates the StatusBg/SelectedBg/
   CurrentLine family. We currently hand-pick all four per theme and hardcode
   hex for graphite/evergreen/mono-amber.
2. **Compute `StatusFg`/`SelectionFg` by auto-contrast, not by hand.**
   (Textual) `brightness < 0.5 → white else black`. This _is_ the fix for the
   "phosphor green fails 4.5:1 on white" gotcha — encode the rule, don't
   re-derive it per palette.
3. **Add a neutral depth ramp: `background < surface < panel`.** (Textual /
   Catppuccin) We have accent shades but no principled neutral hierarchy for
   raised regions (dock, sidebar). Keeps chrome calm while letting panels lift
   off the void.
4. **Flatten alpha against the resolved surface before emitting ANSI.**
   (Textual / Zed) Author soft backgrounds as "accent at 10–25%", composite to
   opaque at generate time. Terminals have no alpha; this gives us "soft"
   selection/overlay tints for free and matches the website's existing
   `--accent-soft` discipline.
5. **Palette + role mapping with single-parent `inherits`.** (Helix) Adding a
   theme becomes a palette swap, not a 20-slot re-map — which is exactly what
   our `{ green with … }` records already approximate. Helix is the closest
   comparable (TUI editor, pure-data theme, TOML), so its shape is the safest
   to copy.

The current plan's "minimal schema first, full token schema as v2" staging is
correct. These ideas mostly change _what the minimal schema derives_ rather
than how many slots it exposes — so they keep the slot count low while making
each theme cheaper to author and harder to get wrong.

---

## Textual — the derivation engine

Source read: `textual/design.py` (`ColorSystem`) and `textual/color.py`.

The whole system hangs on **two constants** and a contrast rule:

```
NUMBER_OF_SHADES   = 3
luminosity_spread  = 0.15          # → step = spread/2 = 0.075
contrast threshold = brightness < 0.5   # white text below, black above
text alphas        = 0.87 / 0.60 / 0.38 # text / muted / disabled
```

### Shade ramp (steal this)

Each base color spawns 7 variants: `darken-3 … base … lighten-3`, where a
shade is just an L\* shift of `n × 0.075` (±0.225 across the ramp). Critically,
`lighten`/`darken` operate in **CIE L\*a\*b\*, not RGB**:

```
darken(amount):  l,a,b = rgb_to_lab(c); l -= amount*100; return lab_to_rgb(l,a,b)
lighten(amount): darken(-amount)
```

This is why the ramp stays perceptually even and doesn't drift saturation.
Naive RGB scaling (what `Color.ofHex` hand-picks currently dodge) would.

**Fedit application:** our `Theme` already documents Accent/StatusBg/
SelectedBg/CurrentLine as "four shades of the theme's primary color, from
brightest to softest" (`Themes.fs:21-23`) — but we then hand-author all four.
Replace with: author `Accent` (+ optional `dark` flag), derive the rest via
Lab darken/lighten. The one non-trivial port is `rgb_to_lab`/`lab_to_rgb` into
`Color.fs`. Worth it — it also unlocks `*-muted` (`blend(accent, bg, 0.7)`) for
inactive/unfocused panes in one line.

### Auto-contrast text (steal this — it's the 4.5:1 fix)

```
get_contrast_text(alpha=0.95):
    return (WHITE if brightness < 0.5 else BLACK).with_alpha(alpha)
    # brightness = (299r + 587g + 114b) / 1000   (Rec.601 luma, 0..1)
```

We manually set `StatusFg = Color.black` on yellow and mono-amber
(`Themes.fs:143,180`) and `brightWhite` everywhere else. That's a manual-error
class — exactly the gap the plan's "no background-only slots" principle is
trying to close. Compute `StatusFg`/`SelectionFg`/`DockSelectedFg` from their
backgrounds instead. The CLAUDE.md note "phosphor green fails 4.5:1 vs white →
text on accent uses neutral-900" becomes a derived guarantee rather than a
remembered rule.

For _colored_ legible text (a readable accent on the editor bg), Textual tints
the contrast color 66% toward the accent:
`contrast_text.tint(primary.with_alpha(0.66))`. Useful if we ever want accent
text (dock title) guaranteed-legible on arbitrary backgrounds.

### Depth layering (steal the concept)

`background` (the void) `< surface` (where content sits) `< panel` (raised:
headers/sidebars). `panel = surface.blend(primary, 0.1)` — panels pick up 10%
of the accent so they feel part of the theme without shouting. In dark mode a
4% white `boost` is composited onto panels to lift them.

Our plan keeps "calm grayscale chrome" as a principle — good — but has no token
for the neutral hierarchy itself; `dock.bg`/`sidebar.bg` are hand-picked
near-blacks. A 3-step neutral ramp (derived from `editor.bg`) would make
"calm" systematic and themeable in one knob.

### Theme format

Minimal viable Textual theme is literally `Theme(name="x", primary="#hex")` —
every other slot and all shades derive. `variables: dict` is the escape hatch:
any generated key can be pinned by name. **This is the model to aim for:** a
fedit theme should be `{ name, accent }` in the easy case, with explicit
overrides only where derivation isn't good enough.

---

## Zed — the token taxonomy

Source: schema `zed.dev/schema/themes/v0.2.0.json`, `one.json` (141 style keys).

Zed is GUI and ~10× our surface count, so we steal _naming discipline_, not the
key list. The valuable patterns:

### Flat dotted keyspace + fixed state-suffix vocabulary

`<role>.<state>` strings, with the _same_ suffixes reused everywhere:
`.hover .active .selected .disabled` + `.muted .placeholder .accent` +
`.background .border`. Our `canonical-elements.md` already trends this way
(`gutter.activeFg`, `dock.selectedBg`). The lesson: **lock the suffix
vocabulary now** so future tokens compose predictably instead of inventing
`activeFg` here and `selected.bg` there. (We currently have both spellings in
the two schema tiers — worth reconciling.)

### `element` vs `ghost_element` (steal — perfect TUI fit)

`element.*` = chrome with a resting fill; `ghost_element.background =
#00000000` = transparent at rest, paints only on hover/selection. In a TUI
_most_ chrome is ghost (tree rows, completion rows are invisible until
selected). This is a cleaner mental model than our current "fixed bg vs themed
selection bg" split and it explains exactly why selection needs fg+bg while
the resting row needs neither.

### Derive soft bg from bold fg via alpha

`error.background = error.foreground @ ~10%` (`#d072771a`), `.border` a
separate solid. Selections at `~24%` (`3d`) so text underneath stays legible.
Formalize: a "soft" surface is its foreground at a fixed alpha, flattened.
Matches takeaway #4.

### Family envelope + versioned schema

```json
{ "$schema": "…/v0.2.0.json", "name": "One", "author": "…",
  "themes": [ { "name": "One Dark", "appearance": "dark", "style": {…} } ] }
```

`appearance: dark|light` is metadata the host uses to slot a theme into the
user's preference; `$schema` version is how Zed migrates breaking key renames.
Our `brand/themes/*.json` mirrors could adopt the same envelope. `appearance`
could let `NO_COLOR`/terminal-bg drive selection. Defer, but reserve the shape.

### Syntax = scope → `{ color, font_style, font_weight }`

Keyed by tree-sitter capture names (`comment.doc`, `string.escape`,
`punctuation.bracket`). When the `docs/superpowers/` syntax-highlighting work
lands, **steal the scope vocabulary wholesale** — our `Syntax*` fields already
parallel it. For a TUI drop `font_style`, keep `color` + bold.

---

## Helix — the closest comparable, copy its shape

TUI editor, Rust, pure-data TOML theme. This is the design fedit should most
resemble.

### Palette + roles + `inherits`

```toml
inherits = "boo_berry"          # single parent, override-by-key
"ui.background" = "white"
"ui.linenr.selected" = { fg = "gold", modifiers = ["bold"] }
keyword = { fg = "berry" }

[palette]
berry = "#2A2A4D"
```

- **Named `[palette]`** + dotted role keys referencing palette names. Adding a
  theme = swap the palette, keep the role map.
- **`inherits`** = single-parent override. This is the TOML form of our
  `{ green with Name = …; Accent = … }` records — Helix proves it scales to
  user themes. Our user-theme loader should support it.
- **Value-as-string-or-record:** `key = "#fff"` or `key = { fg, bg,
underline = { color, style }, modifiers = [...] }`.
- 17 built-in terminal color names usable without defining them
  (`red`, `light-blue`, …) — relevant to our NO_COLOR / 256-color paths.

### UI scope vocabulary (directly maps to our chrome)

```
ui.background  ui.linenr  ui.linenr.selected  ui.gutter  ui.gutter.selected
ui.selection  ui.cursorline.primary  ui.statusline  ui.statusline.inactive
ui.menu  ui.menu.selected  ui.popup  ui.text  ui.text.focus  ui.virtual.*
```

Note `ui.linenr` / `ui.linenr.selected` is exactly our `gutter.fg` /
`gutter.activeFg`. Mode variants are suffixes (`.normal/.insert/.select`) —
fedit's modal status (`EDIT`/`CMD`/`FIND` in the prototype) could theme the
statusline per mode the same way, later.

### Modifiers + decoupled underline (steal)

Modifiers: `bold, dim, italic, underlined, reversed, crossed_out` (+ blink/
hidden). **Underline is its own sub-record** `underline = { color, style }`
where style ∈ `line, curl, dashed, dotted, double_line` — decoupled from the
modifier bag because terminals support colored/styled underlines
independently. We'll want this for diagnostics/spell underlines without
overloading fg.

---

## base16 — the "few colors derive everything" reference

16 colors with fixed roles: `base00–07` a **monochrome ramp** (bg→fg,
dark→light), `base08–0F` **8 accent hues** with assigned syntax meanings.

```
base00 Default bg     base04 Dark fg (status)    base08 Variables/diff-del
base01 Lighter bg     base05 Default fg/caret    base0B Strings/diff-ins
base02 Selection bg   base06 Light fg            base0D Functions/headings
base03 Comments       base07 Lightest fg         base0E Keywords/diff-changed
```

Two reasons to care:

1. **The ramp+accents split is the principled version of our "5 ANSI int
   slots, chrome constant."** `base00–07` = our neutral depth ramp (takeaway
   #3); `base08–0F` = syntax palette.
2. **ANSI interop for free.** base16 → 16 terminal colors is a solved mapping.
   Aligning our neutral ramp + syntax accents to base16 roles means
   NO_COLOR/256-color quantization has a canonical target, and users could
   import base16 themes.

Don't expose `base0X` _names_ (opaque) — use Catppuccin-style role names. But
keep the _structure_ (8-step neutral ramp + N accents) underneath.

## Catppuccin — role-named ramps + flavor/accent UX

26 named colors per flavor, **named by role-in-ramp not by hue**:

```
Crust < Mantle < Base   (backgrounds, darkest→primary editor bg)
Surface0/1/2            (raised UI)
Overlay0/1/2            (borders, comments, muted)
Subtext0/1 < Text       (muted→primary fg)
+ 14 hue accents (Mauve, Peach, Teal, …)
```

Four "flavors" (Latte/Frappé/Macchiato/Mocha) = **same role names, different
hex** → a port maps roles once and gets all four themes free. The **flavor +
accent** UX (pick a palette, then pick one accent threaded through interactive
elements) is precisely fedit's existing "one accent per surface" rule,
generalized. If we ever do light themes, this is the model: define roles once,
ship `dark`/`light` hex sets.

Steal the _naming_: `base/surface/overlay/text` reads far better for theme
authors than `base01/base02`.

## VS Code — the fallback lever

~500 flat dotted keys, three separate systems kept distinct:
`colors` (chrome) / `tokenColors` (TextMate syntax) / `semanticTokenColors`.
The one author-visible idea worth copying:

- **`type: dark|light|hc` + fall back unset chrome to that type's built-in
  default.** Author specifies a handful, inherits hundreds. This is the
  "minimal theme" guarantee: an incomplete theme is still valid because missing
  chrome slots resolve to sane neutral defaults.
- **Chrome falls back; syntax scopes do not** (unmatched scope = unstyled).
  Mirror this: derive/fall-back chrome aggressively, leave syntax explicit.

---

## Recommendation — what to fold into the plan

Keep the staged plan. Adjust the **minimal schema** so it derives more:

1. **Author surface:** `{ name, accent, [appearance], [overrides] }`. The
   common case is one accent. (Textual minimal-theme model.)
2. **Add a neutral depth ramp** (`bg`/`surface`/`panel`, or
   `editor.bg` + derived) so dock/sidebar/prompt backgrounds derive instead of
   hand-picked near-blacks. (Textual/Catppuccin.)
3. **Derive every `*Fg` on a colored `*Bg` via auto-contrast.** Drop manual
   `StatusFg = Color.black`. (Textual — and it closes the 4.5:1 gotcha.)
4. **Derive the accent shade family** (StatusBg/SelectedBg/CurrentLine) via
   Lab `lighten`/`darken`; keep explicit override per slot. Requires
   `rgb_to_lab`/`lab_to_rgb` in `Color.fs`. (Textual.)
5. **Author soft backgrounds as alpha-over-surface, flatten to opaque at
   generate time.** (Textual/Zed — and it matches `--accent-soft`.)
6. **Lock the dotted-key + state-suffix vocabulary** and reconcile the
   `activeFg` vs `active.fg` split between the two schema tiers. (Zed/Helix.)
7. **Support `inherits` in the user-theme loader.** (Helix.)
8. **Reserve the family envelope** (`{ name, author, $schema, themes: [...] }`
   with `appearance`) for the v2 import/export layer, but don't build it yet.
   (Zed.)
9. **When syntax highlighting lands,** adopt the tree-sitter scope vocabulary
   and Helix's decoupled `underline = { color, style }` + modifier set.

Items 1–5 are the high-value, low-surface-area changes: they shrink each
theme to a few authored colors and make the bundled-theme contrast checks in
the plan's validation section mostly unnecessary, because contrast becomes a
property of the derivation rather than a thing to test per palette.

The cost is one real port: CIE Lab conversion in `Color.fs`. Everything else
(auto-contrast, blend, alpha-flatten, inherits) is straightforward value-type
math that fits the pure-data MVU model cleanly.
