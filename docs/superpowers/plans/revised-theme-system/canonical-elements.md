# Canonical editor theme element names

These names are the proposed vocabulary for docs, JSON schema, CSS prototypes, tests, and implementation comments.

## Minimal schema paths

| Path | Canonical element | Runtime surface |
| --- | --- | --- |
| `accent` | Accent foreground | Dock title, theme mark, small emphasis. |
| `status.fg` | Status foreground | Status bar text. |
| `status.bg` | Status background | Full status bar row. |
| `selection.fg` | Selection foreground | Selected file-tree row, selected completion, selected text. |
| `selection.bg` | Selection background | Selected file-tree row, selected completion, selected text. |
| `gutter.fg` | Gutter foreground | Normal line numbers. |
| `gutter.bg` | Gutter background | Normal line-number column background. |
| `gutter.activeFg` | Active gutter foreground | Current line number text. |
| `gutter.activeBg` | Active gutter background | Current line number cell background. |
| `line.fg` | Editor line foreground | Normal editor text when syntax is not overriding. |
| `line.bg` | Editor line background | Normal editor row background. |
| `line.activeFg` | Active line foreground | Current editor row text when explicitly overridden. |
| `line.activeBg` | Active line background | Current editor row background. |
| `prompt.fg` | Prompt foreground | Bottom prompt input text. |
| `prompt.bg` | Prompt background | Bottom prompt input row. |
| `dock.fg` | Dock foreground | Dock info text and unselected completion rows. |
| `dock.bg` | Dock background | Dock panel body. |
| `dock.titleFg` | Dock title foreground | Dock title/count row. |
| `dock.selectedFg` | Dock selected foreground | Selected dock completion text. |
| `dock.selectedBg` | Dock selected background | Selected dock completion row. |

## Full token names

| Token | Canonical element |
| --- | --- |
| `accent.fg` | Accent foreground |
| `editor.fg` | Editor foreground |
| `editor.bg` | Editor background |
| `editor.line.active.fg` | Active line foreground |
| `editor.line.active.bg` | Active line background |
| `editor.selection.fg` | Editor selection foreground |
| `editor.selection.bg` | Editor selection background |
| `gutter.fg` | Gutter foreground |
| `gutter.bg` | Gutter background |
| `gutter.active.fg` | Active gutter foreground |
| `gutter.active.bg` | Active gutter background |
| `sidebar.fg` | Sidebar foreground |
| `sidebar.bg` | Sidebar background |
| `sidebar.border.fg` | Sidebar border foreground |
| `sidebar.selected.fg` | Sidebar selected foreground |
| `sidebar.selected.bg` | Sidebar selected background |
| `status.fg` | Status foreground |
| `status.bg` | Status background |
| `prompt.fg` | Prompt foreground |
| `prompt.bg` | Prompt background |
| `dock.fg` | Dock foreground |
| `dock.bg` | Dock background |
| `dock.title.fg` | Dock title foreground |
| `dock.border.fg` | Dock border foreground |
| `dock.selected.fg` | Dock selected foreground |
| `dock.selected.bg` | Dock selected background |
| `completion.theme.swatch.border` | Theme swatch border |
| `syntax.keyword.fg` | Syntax keyword foreground |
| `syntax.string.fg` | Syntax string foreground |
| `syntax.comment.fg` | Syntax comment foreground |
| `syntax.function.fg` | Syntax function foreground |
| `syntax.type.fg` | Syntax type foreground |

## CSS prototype names

The prototype prefixes tokens with `--fedit-` and uses CSS-friendly hyphenation:

```css
--fedit-editor-line-active-bg
--fedit-gutter-active-fg
--fedit-dock-selected-bg
--fedit-completion-theme-swatch-border
```

The CSS names are not proposed as the user-theme JSON format; they exist so the prototype and future website showcase can bind every visible element to a named variable.
