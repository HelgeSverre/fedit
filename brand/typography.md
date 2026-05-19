# Typography

Two fonts, each with a single job. No mixing.

## Departure Mono — branding

Used everywhere the brand has a voice: marketing site, README, presentations, social cards, logo lockups.

- **Source:** [departuremono.com](https://departuremono.com/)
- **License:** Free, OFL
- **Web (`@fontsource`):** `https://cdn.jsdelivr.net/npm/@fontsource/departure-mono/index.css`
- **Self-host:** download from the official site, serve woff2 from `/fonts/`
- **Character:** pixel-tuned, retro-CRT, distinctive. Reads as a terminal artifact, not a UI font.

### When to use

- Hero headlines on the marketing site
- The wordmark (`fedit`)
- README headings (Markdown can't, but rendered HTML can)
- Slide decks, OG images, social posts

### When NOT to use

- Inside the editor (terminal renders user's configured font; defer)
- Long-form documentation prose at body sizes — Departure is great at 24px+, harder to read at 14px for long stretches

## JetBrains Mono — code, CLI, body fallback

Used everywhere code is shown, in CLI output when the renderer needs a recommendation, and as the body font for documentation where Departure would tire the reader.

- **Source:** [jetbrains.com/mono](https://www.jetbrains.com/lp/mono/)
- **License:** Free, OFL
- **Web (`@fontsource`):** `https://cdn.jsdelivr.net/npm/@fontsource/jetbrains-mono/index.css`
- **Character:** friendly humanist mono, designed for code reading at small sizes. Safe across light and dark.

### When to use

- All inline `code` and `<pre>` blocks on the marketing site
- Documentation body text (where Departure is too loud)
- Help screens / `--help` output
- The terminal editor itself — **but only as the recommended font**; the terminal will use whatever the user has configured. Do not try to override.

### Defer to terminal

Inside the TUI, the editor renders using the terminal's configured font. Do not bundle or load fonts inside the running editor. Document JetBrains Mono as the recommendation in the README; trust users to configure their terminal.

## Sizing Scale

Use this scale. Do not invent new sizes.

```
xs:   12px / 0.75rem   meta, captions, badge text
sm:   14px / 0.875rem  body alternative, dense lists, code in prose
base: 16px / 1rem      body default
lg:   18px / 1.125rem  Departure body when used at all
xl:   20px / 1.25rem   subheadings
2xl:  24px / 1.5rem    section heads
3xl:  32px / 2rem      page heads, wordmark in nav
4xl:  48px / 3rem      hero — and the largest you should ever use
```

## Weights

Departure Mono ships in a single weight — use it everywhere, no fallback to bold. The pixel character carries the emphasis.

JetBrains Mono allowed weights:

- `400` body
- `500` emphasis (use sparingly)
- `700` headings (only when needed; usually `500` is enough)

Never use `300` (light) — readability tell, low contrast.

## Tracking & Leading

- Departure Mono headings (≥20px): `letter-spacing: -0.02em`, `line-height: 1.1`
- Departure Mono body (rare): `letter-spacing: 0`, `line-height: 1.5`
- JetBrains Mono code: `letter-spacing: 0`, `line-height: 1.5`
- JetBrains Mono prose: `letter-spacing: 0`, `line-height: 1.625`
- Body width: `max-width: 65ch`. Always.

## Example Lockup

```html
<div class="brand">
    <svg
        class="symbol"
        viewBox="0 0 24 24"
        fill="none"
        stroke="#00B86B"
        stroke-width="3"
        stroke-linecap="square"
    >
        <path d="M4 16 L12 8 L20 16" />
    </svg>
    <span class="word">fedit</span>
</div>

<style>
    .brand {
        display: flex;
        align-items: baseline;
        gap: 10px;
    }
    .brand .symbol {
        width: 24px;
        height: 24px;
        align-self: center;
    }
    .brand .word {
        font-family: "Departure Mono", monospace;
        font-size: 32px;
        letter-spacing: -0.02em;
        line-height: 1;
    }
</style>
```
