# Revised theme system plan

## Goal

Make fedit themes control the editor surfaces that users actually read: selected rows, active gutter text, active gutter background, active editor-line background, prompt/dock surfaces, and theme-completion swatches.

This is a planning artifact only. It does not change the runtime schema yet.

The companion `prototype.html` is intentionally broader than a static theme swatch. It uses a flex app shell with a top control navbar, a padded full-size editor stage, and a searchable right-side color inspector. The prototype chrome uses OKLCH CSS tokens, a 12px-derived JetBrains Mono scale, antialiased body text, and an actual JetBrains Mono Nerd Font for icon preview. It exercises theme switching, collapsible file-tree rows, left/right/off sidebar docking, Nerd Font glyph toggling, editable command prompt text, explicit command-bar scenarios, completion selection, pending/invalid/empty command states, right-aligned theme swatches, square color inputs, and editor-style code-pane scrolling. Treat it as a lightweight future sandbox for editor-surface design decisions.

## Current behavior

`Theme` currently owns:

```fsharp
Name
Description
Accent
StatusBg
SelectedBg
CurrentLine
StatusFg
Syntax*
```

The live UI maps those fields narrowly:

| Surface                       | Current source                                            | Gap                                                                 |
| ----------------------------- | --------------------------------------------------------- | ------------------------------------------------------------------- |
| Status bar                    | `StatusFg` + `StatusBg`                                   | Fine, but not enough for the rest of the chrome.                    |
| Sidebar selected row          | fixed command-bar foreground + `SelectedBg`               | No `SelectedFg`, so pale/high-chroma backgrounds can fail contrast. |
| Dock selected row             | fixed command-bar foreground + `SelectedBg`               | Same issue as sidebar selection.                                    |
| Active line number            | `CurrentLine` foreground only                             | Name implies background, but it is text color.                      |
| Active editor line background | fixed gray                                                | Theme cannot tune it.                                               |
| Prompt row                    | fixed foreground/background                               | New themes do not feel complete in the command surface.             |
| Dock panel                    | fixed foreground/background, themed title foreground only | Theme cannot tune the most visible command-palette surface.         |
| Search matches                | inverted default style                                    | Terminal-dependent, not themeable. Deferred by request.             |
| Theme completions             | text only                                                 | No color preview in the completion list.                            |

## Design principles

- Keep the default bundled themes calm. The current grayscale editor surface is good; do not turn fedit into a full-screen color skin by default.
- Add foregrounds for every non-default background. Background-only theme slots are easy to misuse.
- Keep syntax colors separate from UI chrome. Syntax scopes can remain a distinct `syntax` object.
- Prefer semantic slot names over component implementation names when the role is stable.
- Make the JSON authoring format nested, but the F# implementation can be flat if that keeps the renderer simpler.

## Minimal but sensible schema

This version covers suggestions 1-4 without making every pixel configurable.

```json
{
    "name": "mono-amber",
    "description": "Deep amber phosphor",
    "accent": "#FFAF00",
    "status": {
        "fg": "#0B0B0D",
        "bg": "#5F3B00"
    },
    "selection": {
        "fg": "#0B0B0D",
        "bg": "#875F00"
    },
    "gutter": {
        "fg": "#626872",
        "bg": "default",
        "activeFg": "#FFAF00",
        "activeBg": "#3A2A10"
    },
    "line": {
        "fg": "default",
        "bg": "default",
        "activeFg": "default",
        "activeBg": "#201A10"
    },
    "prompt": {
        "fg": "#FFF4D6",
        "bg": "#2A2114"
    },
    "dock": {
        "fg": "#B9B9B9",
        "bg": "#151515",
        "titleFg": "#FFAF00",
        "selectedFg": "#0B0B0D",
        "selectedBg": "#875F00"
    },
    "syntax": {}
}
```

### Minimal slots

| JSON path         | F# field         | Used by                                                                  |
| ----------------- | ---------------- | ------------------------------------------------------------------------ |
| `accent`          | `Accent`         | Dock title, file-tree emphasis, visible caret/brand accent.              |
| `status.fg`       | `StatusFg`       | Status bar text.                                                         |
| `status.bg`       | `StatusBg`       | Status bar background.                                                   |
| `selection.fg`    | `SelectionFg`    | Selected sidebar row, selected dock completion, text selection.          |
| `selection.bg`    | `SelectionBg`    | Selected sidebar row, selected dock completion, text selection.          |
| `gutter.fg`       | `GutterFg`       | Normal line numbers.                                                     |
| `gutter.bg`       | `GutterBg`       | Normal gutter background; `default` keeps editor surface.                |
| `gutter.activeFg` | `GutterActiveFg` | Current line number.                                                     |
| `gutter.activeBg` | `GutterActiveBg` | Current line number background.                                          |
| `line.fg`         | `LineFg`         | Normal editor text; `default` keeps terminal/editor surface foreground.  |
| `line.bg`         | `LineBg`         | Normal editor line background; `default` keeps terminal background.      |
| `line.activeFg`   | `LineActiveFg`   | Active editor line text; `default` preserves syntax/default foregrounds. |
| `line.activeBg`   | `LineActiveBg`   | Active editor line background.                                           |
| `prompt.fg`       | `PromptFg`       | Prompt row text.                                                         |
| `prompt.bg`       | `PromptBg`       | Prompt row background.                                                   |
| `dock.fg`         | `DockFg`         | Dock informational text and unselected completion rows.                  |
| `dock.bg`         | `DockBg`         | Dock panel background.                                                   |
| `dock.titleFg`    | `DockTitleFg`    | Dock panel title.                                                        |
| `dock.selectedFg` | `DockSelectedFg` | Selected dock completion text.                                           |
| `dock.selectedBg` | `DockSelectedBg` | Selected dock completion background.                                     |

### Minimal migration

1. Extend `Theme` with the fields above.
2. Keep old JSON fields accepted as a compatibility path:
    - `statusFg` -> `status.fg`
    - `statusBg` -> `status.bg`
    - `selectedBg` -> `selection.bg`
    - `currentLine` -> `gutter.activeFg`
3. Default new fields from the existing constants:
    - `selection.fg` from current command-bar foreground.
    - `gutter.fg` from fixed line-number color.
    - `gutter.bg` and `line.bg` as `Default`.
    - `line.activeBg` from fixed current-line gray.
    - `prompt.*` from fixed command-bar style.
    - `dock.*` from fixed chrome plus accent title.
4. Update `View.fs` helpers:
    - `selectedOf` uses `SelectionFg` + `SelectionBg`.
    - `currentLineNumber` uses `GutterActiveFg` + `GutterActiveBg`.
    - active editor line uses `LineActiveFg` + `LineActiveBg`.
    - prompt row uses `PromptFg` + `PromptBg`.
    - dock rows use `Dock*`.
5. Update docs and examples.

## Pretty much anything can be changed schema

This version makes all major UI regions independently configurable. It is useful if fedit wants a theme marketplace or imported themes later.

```json
{
    "name": "full-surface-example",
    "description": "Every visible editor surface has a named token",
    "tokens": {
        "editor.fg": "#DADADA",
        "editor.bg": "#0B0B0D",
        "editor.line.active.fg": "default",
        "editor.line.active.bg": "#151515",
        "editor.selection.fg": "#0B0B0D",
        "editor.selection.bg": "#875F00",
        "gutter.fg": "#626872",
        "gutter.bg": "#0B0B0D",
        "gutter.active.fg": "#FFAF00",
        "gutter.active.bg": "#3A2A10",
        "sidebar.fg": "#DADADA",
        "sidebar.bg": "#0F1011",
        "sidebar.border.fg": "#3A3A3A",
        "sidebar.selected.fg": "#0B0B0D",
        "sidebar.selected.bg": "#875F00",
        "status.fg": "#0B0B0D",
        "status.bg": "#5F3B00",
        "prompt.fg": "#FFF4D6",
        "prompt.bg": "#2A2114",
        "dock.fg": "#B9B9B9",
        "dock.bg": "#151515",
        "dock.title.fg": "#FFAF00",
        "dock.border.fg": "#3A3A3A",
        "dock.selected.fg": "#0B0B0D",
        "dock.selected.bg": "#875F00",
        "completion.theme.swatch.border": "#0B0B0D",
        "syntax.keyword.fg": "#87AFFF",
        "syntax.string.fg": "#87D787",
        "syntax.comment.fg": "#808080"
    }
}
```

### Full token groups

| Group                       | Purpose                                                               |
| --------------------------- | --------------------------------------------------------------------- |
| `editor.*`                  | Normal text surface, active line, text selection.                     |
| `gutter.*`                  | Line-number column and active line marker.                            |
| `sidebar.*`                 | File tree surface, border, selected row.                              |
| `status.*`                  | Status bar.                                                           |
| `prompt.*`                  | Bottom command/search/file prompt row.                                |
| `dock.*`                    | Completion/info dock panel.                                           |
| `completion.theme.swatch.*` | Theme preview square in completion rows.                              |
| `syntax.*`                  | Capture colors. Existing syntax fields can map into this group later. |

## Recommendation

Implement the minimal schema first. It solves the immediate mono-amber readability problem, makes `CurrentLine` honest, and gives each theme enough control over the command surface without creating a large compatibility burden.

Keep the full token schema as an import/export layer or future v2. It is better suited to a theme marketplace and live theme editor than to the first runtime refactor.

## Implementation notes

- Use `Color.Default` to mean "inherit the surface already written to the cell".
- Selection/search overlays should still win over syntax colors.
- For active line foreground, preserve syntax colors unless `line.activeFg` is explicitly non-default.
- Validate bundled themes with a small contrast helper before accepting new palettes. At minimum, check:
    - `status.fg` on `status.bg`
    - `selection.fg` on `selection.bg`
    - `dock.selectedFg` on `dock.selectedBg`
    - `prompt.fg` on `prompt.bg`
- User-theme loader should accept both nested and legacy flat keys for one release window.

## Validation plan

1. Unit-test JSON loading for nested minimal themes.
2. Unit-test legacy JSON fallback.
3. Snapshot or screen-cell tests for:
    - selected sidebar row uses `SelectionFg` and `SelectionBg`,
    - active gutter cell uses `GutterActiveFg` and `GutterActiveBg`,
    - active editor row uses `LineActiveBg`,
    - prompt/dock use the new theme fields.
4. Launch fedit and manually cycle `green`, `yellow`, `mono-amber`, `graphite`.
5. Verify `NO_COLOR=1` still strips theme colors as intended.
