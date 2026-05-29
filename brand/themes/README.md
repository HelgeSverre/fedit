# fedit themes

Canonical brand spec for fedit's editor themes. The implementation lives in `src/Fedit/Themes.fs`; these JSON files mirror the bundled themes and the user-theme schema.

## Available themes

| Theme        | Accent (ANSI) | Hex (truecolor) | Purpose                        |
| ------------ | ------------- | --------------- | ------------------------------ |
| `green`      | 35            | `#00B86B`       | Default — the brand            |
| `blue`       | 33            | `#1F6FEB`       | High-contrast, GitHub-adjacent |
| `orange`     | 166           | `#D2691E`       | Warm, retro-terminal           |
| `cyan`       | 81            | `#5FD7FF`       | Cool, calm                     |
| `teal`       | 80            | `#5FD7D7`       | Cyan-green hybrid              |
| `yellow`     | 220           | `#FFD700`       | Warm, dark text                |
| `red`        | 203           | `#FF5F5F`       | Crimson                        |
| `graphite`   | 111           | `#8CB4FF`       | Blue-grey high readability     |
| `evergreen`  | 143           | `#A7C080`       | Soft forest green              |
| `mono-amber` | 214           | `#FFAF00`       | Deep amber phosphor            |

Banned (per `brand/USAGE.md`): purple, magenta. The "AI purple aesthetic" — see `~/.claude/skills/brand-from-scratch/references/bans.md`.

## Schema

The fedit `Theme` record (see `src/Fedit/Themes.fs`):

```fsharp
type Theme =
    { Name: string
      Description: string
      Accent: Color
      StatusBg: Color
      SelectedBg: Color
      CurrentLine: Color
      StatusFg: Color
      SyntaxKeyword: Color
      SyntaxKeywordControl: Color
      SyntaxKeywordOperator: Color
      SyntaxString: Color
      SyntaxStringSpecial: Color
      SyntaxNumber: Color
      SyntaxComment: Color
      SyntaxFunction: Color
      SyntaxFunctionCall: Color
      SyntaxType: Color
      SyntaxConstructor: Color
      SyntaxVariable: Color
      SyntaxParameter: Color
      SyntaxOperator: Color
      SyntaxPunctuation: Color
      SyntaxAttribute: Color }
```

Bundled JSON files use ANSI 256 integers for the five chrome slots so the brand spec can show terminal-safe fallbacks. User-theme JSON accepts color strings instead: `#RGB`, `#RRGGBB`, or named colors from `Color.fs`. User themes may also include an optional `syntax` object with capture keys such as `keyword`, `string`, `comment`, `function`, and `type`; omitted syntax keys fall back to the bundled default theme. The `hex` and `note` fields are documentation — they are not consumed at runtime but explain the truecolor accent target and ANSI fallback.

### Field meanings

| Field         | What it controls                                               |
| ------------- | -------------------------------------------------------------- |
| `accent`      | Dock title, file tree highlight, status mode indicator, cursor |
| `statusFg`    | Foreground of the status bar text                              |
| `statusBg`    | Background of the status bar                                   |
| `selectedBg`  | Background of selected text and active file tree row           |
| `currentLine` | Background of the line the cursor is on                        |

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

1. Pick an accent ANSI 256 code and truecolor hex. Verify it survives the brand bans (no purple/magenta/violet). Optionally validate the hex via `brand/preview-accents.html`.
2. Copy `green.json` to `<name>.json`. Update `name`, `description`, `accent`, `statusFg`, `statusBg`, `selectedBg`, `currentLine`, `hex`, `note`. Remove `"isDefault": true`.
3. Add a corresponding entry in `src/Fedit/Themes.fs` and append it to the `all` list.
4. Add the same entry to `website/src/data/themes.ts` so `/themes` and `/brand` render the bundled catalog in completion order.

At runtime, user themes load from `~/.config/fedit/themes/*.json`; the bundled JSON files remain the brand spec.

## NO_COLOR

When `NO_COLOR=1` is set, themes are bypassed entirely. The brand survives this — the caret in the status bar still reads, neutral chrome carries hierarchy.
