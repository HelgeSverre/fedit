# fedit themes

Canonical brand spec for fedit's editor themes. The implementation lives in `src/Fedit/Themes.fs` and reads these values directly.

## Available themes

| Theme | Accent (ANSI) | Hex (truecolor) | Purpose |
|---|---|---|---|
| `green` | 35 | `#00B86B` | Default — the brand |
| `blue` | 33 | `#1F6FEB` | High-contrast, GitHub-adjacent |
| `orange` | 166 | `#D2691E` | Warm, retro-terminal |
| `cyan` | 81 | `#5FD7FF` | Cool, calm |
| `teal` | 80 | `#5FD7D7` | Cyan-green hybrid |
| `yellow` | 220 | `#FFD700` | Warm, dark text |
| `red` | 203 | `#FF5F5F` | Crimson |

Banned (per `brand/USAGE.md`): purple, magenta. The "AI purple aesthetic" — see `~/.claude/skills/brand-from-scratch/references/bans.md`.

## Schema

The fedit `Theme` record (see `src/Fedit/Themes.fs`):

```fsharp
type Theme =
    { Name: string
      Description: string
      Accent: int
      StatusFg: int
      StatusBg: int
      SelectedBg: int
      CurrentLine: int }
```

These JSON files mirror that shape exactly. The `hex` and `note` fields are documentation — they're not consumed at runtime but explain what the ANSI integer resolves to in modern terminals.

### Field meanings

| Field | What it controls |
|---|---|
| `accent` | Dock title, file tree highlight, status mode indicator, cursor |
| `statusFg` | Foreground of the status bar text |
| `statusBg` | Background of the status bar |
| `selectedBg` | Background of selected text and active file tree row |
| `currentLine` | Background of the line the cursor is on |

The grayscale chrome (borders, line numbers, body text, dim hints) is constant across all themes — only these five accent slots swap.

## Selecting a theme

From inside the editor, via the command bar (`Ctrl+P`):

```
theme green
theme blue
theme orange
```

Tab through the available themes; the UI live-previews each as you cycle. The selection persists to `~/.config/fedit/config.json` and is restored on next launch.

## Adding a new theme

1. Pick an accent ANSI 256 code. Verify it survives the brand bans (no purple/magenta/violet). Optionally validate the hex via `brand/preview-accents.html`.
2. Copy `green.json` to `<name>.json`. Update `name`, `description`, `accent`, `statusFg`, `statusBg`, `selectedBg`, `currentLine`, `hex`, `note`. Remove `"isDefault": true`.
3. Add a corresponding entry in `src/Fedit/Themes.fs` and append it to the `all` list.

A future evolution could load these JSONs directly at startup (the `merge`/`tryFindIn` functions already support user themes). For now they're spec.

## NO_COLOR

When `NO_COLOR=1` is set, themes are bypassed entirely. The brand survives this — the caret in the status bar still reads, neutral chrome carries hierarchy.
