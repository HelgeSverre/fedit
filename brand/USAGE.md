# fedit — Brand Usage

The brand is `caret + Departure Mono + phosphor green`. If you reach for a second font or a second color, stop and reconsider.

## Files in this Directory

| File | What | When to use |
|---|---|---|
| `symbol.svg` | Caret, phosphor green stroke | Web, presentations, OG images |
| `symbol-mono.svg` | Caret, `currentColor` stroke | Anywhere the surrounding context dictates color |
| `favicon.svg` | Caret tuned for 16×16 | Browser tab, app icon at small sizes |
| `palette.json` | Canonical color source | Build tooling, regenerating CSS/F# |
| `palette.css` | CSS custom properties (light + dark) | Marketing site, docs |
| `palette.fs` | F# module | TUI rendering inside the editor |
| `typography.md` | Departure Mono (brand) + JetBrains Mono (code/CLI) | Reference when writing CSS or help output |
| `voice.md` | Five voice rules | Reference when writing copy |
| `themes/` | Editor color themes (green default, blue, orange) | User-selectable inside fedit |
| `USAGE.md` | This file | Onboarding contributors |

## Symbol Usage

The caret `^` is the mark. It echoes the editor's cursor position character, the upward chevron in the status bar, and the visual rhythm of the wordmark.

### Do

- Use `symbol-mono.svg` in headers, footers, and anywhere the brand sits next to other text — let `currentColor` pick up the surrounding palette.
- Use `symbol.svg` only when the accent color is the focal moment (loading screens, the one accent moment on a landing page).
- Use `favicon.svg` for any rendering at 24px or smaller — the stroke widths and proportions are hand-tuned for that range.
- Preserve the 2px safe padding (inside the 24-grid) around the mark.

### Don't

- Add a circular or rounded-square frame around it.
- Recolor outside the palette. The mark is green or it's `currentColor`. There is no third option.
- Apply effects (shadow, glow, outline, emboss).
- Use the mark as a divider, bullet, or repeated decoration. It appears once per surface.

## Color Usage

### One Accent Rule

In any single viewport (web), screen (TUI), or 12-character span (CLI output), the accent color appears AT MOST ONCE.

If the symbol is accent-green in your header AND the primary button is accent-green — pick one. Remove the other.

For the editor itself, the rule loosens slightly: the cursor and the active mode indicator can both be accent. Two on-screen accents in the TUI is the maximum, and they must be in different functional roles (position vs state).

### Contrast Note

Phosphor green `#00B86B` fails 4.5:1 contrast against white. **Text on accent backgrounds uses `--accent-fg` = neutral-900 (near-black), not white.** This is encoded in `palette.css` and `palette.fs` — do not override.

## Typography Usage

Departure Mono for branding surfaces; JetBrains Mono for code, CLI, and long-form prose. See [`typography.md`](typography.md) for sizes, weights, and lockup examples.

Inside the running editor: the terminal renders the user's configured font. Do not bundle or override fonts in the running TUI. Recommend JetBrains Mono in the README.

## Voice Usage

Five rules, applied uniformly: README, landing page, CLI help, error messages, release notes, commit messages. See [`voice.md`](voice.md).

Notable: commit messages are brand surface. `add search command` not `✨ feat: Add amazing new search functionality`.

## Editor Themes

`themes/` documents the brand-canonical theme set. Implementation lives in `src/Fedit/Themes.fs`. Seven themes ship: `green` (brand default), `blue`, `orange`, `cyan`, `teal`, `yellow`, `red`. Purple and magenta are banned per the brand.

Users switch themes via the command bar (`Ctrl+P` then `theme <name>`). The choice persists to `~/.config/fedit/config.json`.

Adding a new theme: see [`themes/README.md`](themes/README.md).

## Do-Not Gallery

These ship as AI-aesthetic tells. Refusing them is the brand's identity.

- Purple/blue gradient backgrounds anywhere
- Glassmorphism (`backdrop-blur`) on cards
- Centered hero with three feature cards under it
- "Trusted by" logo strip
- Sparkles, meteors, animated background lines
- "Powered by AI" badge
- Emoji in copy or commit messages
- Inter font (for anything — the brand is Departure + JetBrains, period)
- "Build the future of X", "Your all-in-one platform"

If something here ships in a fedit-branded surface, the brand has been violated.

## Extending the Brand

Need a new asset (an Open Graph image, a presentation template, a stickers sheet)?

1. The caret + Departure Mono + phosphor green (once).
2. Real terminal content if a visual is needed.
3. The do-not list applies.

If a use case genuinely needs to escape the constraints, that's a brand evolution and deserves a deliberate decision — not a one-off "this time only" exception.
