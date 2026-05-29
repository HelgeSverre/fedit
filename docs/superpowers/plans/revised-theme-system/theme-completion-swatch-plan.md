# Theme completion swatch plan

## Goal

When the command bar is completing `theme <name>`, show a compact color square at the right edge of each completion row. The square previews the theme's core UI colors without taking over the row.

## Current completion rendering

Completion rows are rendered in `View.fs` as:

- prefix (`> ` for selected rows),
- `CompletionItem.Label`,
- optional `CompletionItem.Detail`,
- selected row style derived from the current theme.

The renderer already has the row width and selected index. That is enough to reserve a few cells at the right.

## Proposed row layout

```text
> mono-amber  Deep amber phosphor                         [■]
  graphite    Blue-grey high readability                  [■]
  evergreen   Soft forest green                           [■]
```

In a real terminal, the right-side swatch is two or four cells:

```text
  ███
```

or, if multiple colors are worth showing:

```text
  ▌▌▌
```

## Minimal implementation

Use one square block floated right:

- Glyph: `■` or `██`.
- Foreground: `theme.Accent`.
- Background: completion row background.
- Only shown for completions whose `ApplyText` starts with `theme ` and resolves through `Themes.tryFindIn`.

This is the safest first pass and makes tab-cycling more legible.

## Better implementation

Use a three-cell segmented swatch:

| Cell | Color |
| --- | --- |
| 1 | `Accent` |
| 2 | `SelectionBg` |
| 3 | `StatusBg` |

Rendered as:

```text
▌▌▌
```

Each cell can use a space glyph with background color instead of a colored Unicode glyph:

```fsharp
Screen.setCell x row { style with Background = theme.Accent } ' ' screen
Screen.setCell (x + 1) row { style with Background = theme.SelectionBg } ' ' screen
Screen.setCell (x + 2) row { style with Background = theme.StatusBg } ' ' screen
```

Using background cells is less dependent on glyph rendering and works in plain monospace terminals.

## Data model options

### No model change

Detect theme rows in `View.fs` by parsing `item.ApplyText`.

Pros:

- No command/completion schema change.
- Small patch.

Cons:

- View logic knows command-specific string format.
- Already happens for live theme preview, so this is acceptable in the short term.

### Add completion metadata

Extend `CompletionItem`:

```fsharp
type CompletionDecoration =
    | NoDecoration
    | ThemeSwatch of Theme

type CompletionItem =
    { Label: string
      Detail: string
      ApplyText: string
      Decoration: CompletionDecoration }
```

Pros:

- Clean renderer.
- Plugins could eventually attach decorations.

Cons:

- Wider change across command completions and tests.

## Recommendation

Start with no model change. Reuse the existing `themeFromApplyText` helper in `View.fs` and reserve four rightmost columns for theme rows.

Rules:

- If `rowWidth < 24`, hide swatches.
- If detail text would collide with the swatch, crop detail earlier.
- On selected rows, keep the row foreground/background but paint the swatch cells with theme backgrounds.
- Do not show swatches for file, buffer, plugin, or normal command completions.

## Follow-up

If completions become richer later, introduce `CompletionDecoration`. The theme swatch then becomes the first concrete decoration and the renderer no longer needs to parse `ApplyText`.
