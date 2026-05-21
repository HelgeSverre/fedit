# Fedit Keybinding Audit & Comparison Matrix

> Cross-referenced against Helix, Neovim, Emacs, and Zed (non-vim mode, Linux defaults where applicable).

## 1. Where keybindings live in the fedit codebase

The fedit keymap is **flat, non-modal, and almost entirely hard-coded**. There is no user keymap file; every chord lives
in F# source.

| Layer                     | File                                                                                      | Lines     | Role                                                                                                                                                                                                                          |
| ------------------------- | ----------------------------------------------------------------------------------------- | --------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Key abstraction           | [src/Fedit/Primitives.fs](file:///Users/helge/code/fedit/src/Fedit/Primitives.fs#L23-L51) | 23–51     | The `KeyInput` DU — the universe of keys fedit understands (no `Ctrl-Shift-X` combos exist as variants).                                                                                                                      |
| OS key decode             | [src/Fedit/Input.fs](file:///Users/helge/code/fedit/src/Fedit/Input.fs)                   | 1–66      | `Input.tryMap : ConsoleKeyInfo -> KeyInput option`. Strict precedence: `Shift+_` arrows/Home/End/Tab → Alt+arrows → Ctrl+(whitelist of letters)/Backspace/Delete → plain keys. **Anything outside the whitelist is dropped.** |
| Global chords (always-on) | [src/Fedit/Editor.fs](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L1090-L1192)     | 1090–1192 | `update` for `KeyPressed`: `Ctrl+Q/P/O/F/B/E/S/R/Z/Y/A/C/X/V`. Intercepted before per-focus routing.                                                                                                                          |
| Per-focus routing         | [src/Fedit/Editor.fs](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L1194-L1197)     | 1194–1197 | Dispatch to `runSidebar` / `runEditor` / `runPrompt` based on `model.Focus`.                                                                                                                                                  |
| Editor focus map          | [src/Fedit/Editor.fs](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L723-L801)       | 723–801   | Plugin chord lookup first; then typing, arrows, Shift-arrows for selection, Alt-arrows for word motion, Ctrl-Backspace/Delete for word delete, Tab/Shift-Tab for (un)indent.                                                  |
| Sidebar focus map         | [src/Fedit/Editor.fs](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L654-L713)       | 654–713   | Arrows, PgUp/PgDn, Home/End, Left/Right collapse/expand, Enter activate, Escape returns focus to editor. Plain characters incrementally narrow the tree.                                                                      |
| Prompt focus map          | [src/Fedit/Editor.fs](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L837-L930)       | 837–930   | Single-line edit, **Tab autofills** the highlighted completion, Shift-Tab / Up / Down cycle (Up/Down navigate matches in Search mode), Alt-Up/Down walks history, Enter runs the parsed command.                              |
| Prompt mode dispatch      | [src/Fedit/Prompt.fs](file:///Users/helge/code/fedit/src/Fedit/Prompt.fs)                 | 1–24      | First char of the prompt text picks the mode: `:` → Command, `/` → Search, `@` → Buffers, otherwise → FilePicker.                                                                                                             |
| Command palette verbs     | [src/Fedit/Commands.fs](file:///Users/helge/code/fedit/src/Fedit/Commands.fs#L78-L221)    | 78–221    | The `:`-style verbs (`open`, `write`, `writeas`, `quit`, `config`, `sidebar`, `tree`, `editor`, `reload`, `next`, `prev`, `theme`, `recent`, `buffers`, `plugin`, plus a bare `<line>[:<col>]` goto).                         |

### Architecture in one diagram

```diagram
╭──────────────╮      ╭──────────────╮      ╭─────────────────────────╮
│ Console keys │ ───▶ │  Input.tryMap│ ───▶ │ KeyInput (Primitives.fs)│
╰──────────────╯      ╰──────────────╯      ╰──────────┬──────────────╯
                                                       │
                                                       ▼
                                            ╭──────────────────────╮
                                            │ Editor.update        │
                                            │   global Ctrl+letter │  ← Ctrl+Q/P/O/F/B/E/S/R/Z/Y/A/C/X/V
                                            ╰─────────┬────────────╯
                                                      │ unmatched
                                                      ▼
                                  ╭───────────┬───────┴────────┬─────────────╮
                                  ▼           ▼                ▼
                              runSidebar   runEditor       runPrompt
                              (tree nav)   (text + sel,    (`:`cmd / `/`search /
                                            plugin chords)  `@`buffers / picker)
```

### Notable mechanics

- **One unified prompt.** `Focus` is now `Sidebar | Editor | Prompt`
  ([Primitives.fs#L18-L21](file:///Users/helge/code/fedit/src/Fedit/Primitives.fs#L18-L21)).
  The command bar, file picker, buffer picker, and in-buffer search all
  share one input box; mode comes from the first character.
- **Quit is two-stage** when buffers are dirty. First `Ctrl+Q` arms `QuitArmed`; second `Ctrl+Q`
  quits ([Editor.fs#L1098-L1121](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L1098-L1121)).
- **`Ctrl+B` is a three-state toggle** (Zed-style): hidden → show+focus → focused → hidden+editor
  ([Editor.fs#L1140-L1158](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L1140-L1158)). The
  palette `sidebar` verb is still wired but redundant in practice.
- **`Ctrl+F` opens `/` in the prompt** for in-buffer search; `Up`/`Down`
  step through matches, `Enter` advances forward, `Escape` closes
  ([Editor.fs#L1134-L1139](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L1134-L1139),
  [Editor.fs#L877-L889](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L877-L889)).
- **`Ctrl+P` opens `:` (command palette); `Ctrl+O` is wired to open a bare file-picker prompt** —
  but `Ctrl+O` is **not in `Input.tryMap`'s Ctrl whitelist** (see
  [Input.fs#L33-L45](file:///Users/helge/code/fedit/src/Fedit/Input.fs#L33-L45)), so the chord is
  currently unreachable and the handler at
  [Editor.fs#L1128-L1133](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L1128-L1133) is dead.
- **Tab in the prompt autofills** the highlighted completion in one shot (so `:o<Tab>` →
  `:open `) rather than cycling. Cycling is `Shift+Tab` or `Up`/`Down`
  ([Editor.fs#L867-L884](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L867-L884)).
- **Plugin keybindings dispatch first** in editor focus ([Editor.fs#L729-L746](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L729-L746)).
  Plain `Character` chords are intentionally not mappable; only `Ctrl+_` chords are surfaced via
  `toKeyChord`. Plugin handlers may invoke a plugin command or fall through to a parsed built-in
  (e.g. binding `Ctrl+S`-like behavior to `:write`).
- **Selection model is shift-anchored.** Any non-shift motion goes through `Buffer.clearSelection`
  first ([Editor.fs#L757-L759](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L757-L759)).
  Shift-motions extend; matches Zed/VS Code, not Helix/Vim.
- **Word motion** uses `Alt+←/→` (Mac-style) rather than the Linux `Ctrl+←/→` convention.
- **Ctrl-Shift-\* combos do not exist.** `Input.tryMap` only treats `Shift+(arrows|Home|End|Tab)`; the moment Ctrl is
  held the Shift bit is ignored. So Zed-style `Ctrl+Shift+P` cannot be added without expanding `KeyInput`.
- **No multi-cursor, no macros, no register/clipboard ring** — clipboard is single-slot via OS effect `ClipboardCopy` /
  `ClipboardPaste`.
- **No remapping.** Only `Config.WordMotion` (changes the _semantics_ of word-right delete/move) and the plugin
  keybinding system bend the defaults; the keys themselves are not user-configurable.

## 2. Comparison matrix

Legend: `—` = no default binding for that action. `palette` = available only inside the unified prompt (opened via
`Ctrl+P` in fedit, `:` in Helix/Nvim, `M-x` in Emacs, `Cmd/Ctrl-Shift-P` in Zed). In fedit, the prompt's first
character picks the mode: `:` command, `/` buffer search, `@` buffer switcher, no prefix = file picker.

### 2.1 File operations

| Action                | **fedit**                       | Helix               | Neovim        | Emacs             | Zed (Linux)                       |
| --------------------- | ------------------------------- | ------------------- | ------------- | ----------------- | --------------------------------- |
| Open file (palette)   | `Ctrl+P` → `open <path>`        | `Space f` / `:o`    | `:e {file}`   | `C-x C-f`         | `Ctrl+P` (file finder) / `Ctrl+O` |
| Open file (picker)    | `Ctrl+O` _(wired, unreachable)_ | `Space f`           | `:e`          | `C-x C-f`         | `Ctrl+O`                          |
| Save                  | `Ctrl+S`                        | `:w`                | `:w`          | `C-x C-s`         | `Ctrl+S`                          |
| Save as               | palette `writeas <path>`        | `:w <path>`         | `:saveas`     | `C-x C-w`         | `Ctrl+Shift+S`                    |
| Save all              | —                               | `:wa`               | `:wa`         | `C-x s`           | `Ctrl+Alt+S`                      |
| Quit                  | `Ctrl+Q` (re-press if dirty)    | `:q`                | `:q` / `ZZ`   | `C-x C-c`         | `Ctrl+Q`                          |
| Force quit (discard)  | second `Ctrl+Q`                 | `:q!`               | `:q!` / `ZQ`  | (prompt)          | (prompt)                          |
| Recent files          | palette `recent <name>`         | `Space f` (history) | —             | (history list)    | `Ctrl+R`                          |
| Open config           | palette `config`                | `:config`           | `:e $MYVIMRC` | `C-x C-f` to file | `Ctrl+,`                          |
| Reload workspace tree | `Ctrl+R`                        | manual              | manual        | `g` in dired      | (auto / file watcher)             |

### 2.2 Cursor motion

| Action          | **fedit**                  | Helix               | Neovim              | Emacs             | Zed (Linux)         |
| --------------- | -------------------------- | ------------------- | ------------------- | ----------------- | ------------------- |
| ←/→/↑/↓         | arrows                     | `h j k l` or arrows | `h j k l` or arrows | `C-b/C-f/C-p/C-n` | arrows              |
| Word left/right | `Alt+←` / `Alt+→`          | `b` / `w`           | `b` / `w`           | `M-b` / `M-f`     | `Ctrl+←` / `Ctrl+→` |
| Line start      | `Home`                     | `gh` / `Home`       | `0` / `Home`        | `C-a` / `Home`    | `Home`              |
| Line end        | `End`                      | `gl` / `End`        | `$` / `End`         | `C-e` / `End`     | `End`               |
| File start      | —                          | `gg`                | `gg`                | `M-<` / `C-Home`  | `Ctrl+Home`         |
| File end        | —                          | `ge`                | `G`                 | `M->` / `C-End`   | `Ctrl+End`          |
| Page up / down  | `PgUp` / `PgDn`            | `Ctrl-b/f`          | `Ctrl-B/F`          | `M-v` / `C-v`     | `PgUp` / `PgDn`     |
| Half page       | —                          | `Ctrl-u/d`          | `Ctrl-U/D`          | —                 | —                   |
| Go to line      | palette `<N>` or `<N>:<C>` | `Ngg` / `:goto N`   | `Ngg` / `:N`        | `M-g g`           | `Ctrl+G`            |

### 2.3 Selection

| Action                   | **fedit**            | Helix                  | Neovim            | Emacs                | Zed (Linux)                |
| ------------------------ | -------------------- | ---------------------- | ----------------- | -------------------- | -------------------------- |
| Extend char left/right   | `Shift+←/→`          | (Select mode + motion) | (Visual + motion) | `C-SPC` then `C-f/b` | `Shift+←/→`                |
| Extend line              | `Shift+↑/↓`          | Select mode            | Visual            | `C-SPC` + `C-n/p`    | `Shift+↑/↓`                |
| Extend to line start/end | `Shift+Home/End`     | `gh` / `gl` in select  | `v0` / `v$`       | `C-SPC` + `C-a/e`    | `Shift+Home/End`           |
| Select all               | `Ctrl+A`             | `%`                    | `ggVG`            | `C-x h`              | `Ctrl+A`                   |
| Select line              | —                    | `x`                    | `V`               | —                    | `Ctrl+L`                   |
| Multi-cursor             | —                    | `C` / `Alt-C`          | — (plugin)        | —                    | `Ctrl+D` / `Shift+Alt+↑/↓` |
| Clear selection          | any non-shift motion | `;`                    | `<Esc>`           | `C-g`                | any non-shift motion       |

### 2.4 Editing

| Action              | **fedit**        | Helix                           | Neovim                              | Emacs           | Zed (Linux)               |
| ------------------- | ---------------- | ------------------------------- | ----------------------------------- | --------------- | ------------------------- |
| Undo                | `Ctrl+Z`         | `u`                             | `u`                                 | `C-/` / `C-x u` | `Ctrl+Z`                  |
| Redo                | `Ctrl+Y`         | `U`                             | `Ctrl-R`                            | `C-?` / `C-M-_` | `Ctrl+Y` / `Ctrl+Shift+Z` |
| Copy                | `Ctrl+C`         | `y` (`Space y` for system clip) | `y{motion}`                         | `M-w`           | `Ctrl+C`                  |
| Cut                 | `Ctrl+X`         | `d`                             | `d{motion}`                         | `C-w`           | `Ctrl+X`                  |
| Paste               | `Ctrl+V`         | `p` (`Space p` system)          | `p`                                 | `C-y`           | `Ctrl+V`                  |
| Backspace word      | `Ctrl+Backspace` | `Alt-Backspace` (insert)        | `Ctrl-W` (insert)                   | `M-DEL`         | `Ctrl+Backspace`          |
| Delete word forward | `Ctrl+Delete`    | —                               | `dw`                                | `M-d`           | `Ctrl+Delete`             |
| Indent              | `Tab`            | `>` (sel) / `Tab` (insert)      | `>>` / Tab (insert)                 | `TAB`           | `Tab` / `Ctrl+]`          |
| Unindent            | `Shift+Tab`      | `<` / `Shift+Tab`               | `<<` / `Ctrl-D` (insert)            | `C-x TAB`       | `Shift+Tab` / `Ctrl+[`    |
| Newline             | `Enter`          | `Enter` (insert)                | `Enter` (insert) / `o`,`O` (normal) | `RET`           | `Enter`                   |

### 2.5 Search / find

| Action              | **fedit**           | Helix     | Neovim    | Emacs       | Zed (Linux)       |
| ------------------- | ------------------- | --------- | --------- | ----------- | ----------------- |
| Find in buffer      | `Ctrl+F` (`/`-mode) | `/`       | `/`       | `C-s`       | `Ctrl+F`          |
| Find backward       | —                   | `?`       | `?`       | `C-r`       | (toggle in panel) |
| Next match          | `Enter` / `↓`       | `n`       | `n`       | `C-s` again | `F3`              |
| Prev match          | `↑`                 | `N`       | `N`       | `C-r`       | `Shift+F3`        |
| Replace             | —                   | `:s/...`  | `:%s/...` | `M-%`       | `Ctrl+H`          |
| Project-wide search | —                   | `Space /` | (plugin)  | `M-x rgrep` | `Ctrl+Shift+F`    |

### 2.6 Command palette / Ex mode

| Action                      | **fedit**                         | Helix                          | Neovim                    | Emacs            | Zed (Linux)    |
| --------------------------- | --------------------------------- | ------------------------------ | ------------------------- | ---------------- | -------------- |
| Open palette / command line | `Ctrl+P` (`:`-mode)               | `:` (Ex) / `Space ?` (palette) | `:`                       | `M-x`            | `Ctrl+Shift+P` |
| Open file picker            | `Ctrl+O` _(wired, unreachable)_   | `Space f`                      | `:e`                      | `C-x C-f`        | `Ctrl+P`       |
| Open buffer switcher        | type `@` inside the prompt        | `Space b`                      | `:b`                      | `C-x b`          | `Ctrl+B`       |
| Run command                 | `Enter`                           | `<CR>`                         | `<CR>`                    | `RET`            | `Enter`        |
| Cancel                      | `Escape`                          | `Escape`                       | `Escape`                  | `C-g`            | `Escape`       |
| Autofill highlighted match  | `Tab` (one-shot — does not cycle) | `Tab`                          | `Tab`                     | (mode-dependent) | `Tab`          |
| Cycle completions           | `Shift+Tab` or `↑`/`↓`            | `Tab` / `Shift+Tab`            | `Tab` / `Shift+Tab`       | (mode-dependent) | `↓` / `↑`      |
| History prev/next           | `Alt+↑` / `Alt+↓`                 | `↑` / `↓`                      | `↑` / `↓` (or `Ctrl-P/N`) | `M-p` / `M-n`    | `↑` / `↓`      |

### 2.7 Buffers / tabs

| Action        | **fedit**                                               | Helix                                 | Neovim    | Emacs   | Zed (Linux) |
| ------------- | ------------------------------------------------------- | ------------------------------------- | --------- | ------- | ----------- | -------- |
| Next buffer   | `Ctrl+PgDn` (also palette `next`)                       | `gn` / `:bn`                          | `:bn`     | `C-x →` | `Ctrl+PgDn` |
| Prev buffer   | `Ctrl+PgUp` (also palette `prev`)                       | `gp` / `:bp`                          | `:bp`     | `C-x ←` | `Ctrl+PgUp` |
| Jump to N     | `Ctrl+1..9` (sorted index, 1-based; out-of-range no-op) | —                                     | `:b{N}`   | —       | `Ctrl+<N>`  |
| Pick / switch | type `@<id                                              | name>`in prompt (or palette`buffers`) | `Space b` | `:b{N}` | `C-x b`     | `Ctrl+P` |
| Close buffer  | —                                                       | `:bc`                                 | `:bd`     | `C-x k` | `Ctrl+W`    |
| Goto line:col | palette `<line>[:<col>]`                                | `:goto`                               | `:N`      | `M-g g` | `Ctrl+G`    |

### 2.8 Panels / file tree

| Action                 | **fedit**                                   | Helix     | Neovim     | Emacs            | Zed (Linux)                |
| ---------------------- | ------------------------------------------- | --------- | ---------- | ---------------- | -------------------------- |
| Focus / toggle sidebar | `Ctrl+B` (three-state: show → focus → hide) | (no tree) | `:Ex`      | `C-x d`          | `Ctrl+B`                   |
| Focus editor           | `Ctrl+E`                                    | implicit  | `Ctrl+W w` | `C-x o`          | `Ctrl+1`                   |
| Theme switcher         | palette `theme <name>`                      | `:theme`  | (plugin)   | `M-x load-theme` | palette → "theme selector" |
| Plugin manager         | palette `plugin <verb> [arg]`               | n/a       | (plugin)   | `M-x package-*`  | extensions panel           |

### 2.9 Sidebar (in fedit, when focused)

| Action                             | **fedit**       | Closest analog                                           |
| ---------------------------------- | --------------- | -------------------------------------------------------- |
| Move up/down                       | `↑` / `↓`       | identical in Helix file picker, dired, Zed project panel |
| PgUp / PgDn (10 lines)             | `PgUp` / `PgDn` | dired, Zed project panel                                 |
| Jump to top/bottom                 | `Home` / `End`  | dired (`<` / `>`), Zed                                   |
| Collapse / parent                  | `←`             | dired's `^`, Zed `←`                                     |
| Expand                             | `→`             | identical in Zed                                         |
| Activate (open file or toggle dir) | `Enter`         | universal                                                |
| Incremental filter                 | type any char   | Helix file picker, Zed project panel                     |
| Return to editor                   | `Escape`        | nvim `:wincmd p`                                         |

## 3. Observations / drift from convention

1. **`Ctrl+B` is reused.** Vim/Helix/Emacs all bind it to "page up". fedit binds it to "toggle sidebar" (matching
   Zed and VS Code). Anyone with Vim muscle memory will accidentally pop the tree mid-edit.
2. **`Ctrl+F` overloads.** Vim/Helix = page down. Emacs = forward char. fedit = find-in-buffer (Zed/VS Code
   convention) — but the find UI is the unified prompt in `/`-mode, not a side panel.
3. **`Ctrl+P` is the palette**, like VS Code's quick-open and Zed's file finder — but Zed/VS Code use `Ctrl+Shift+P`
   for the command palette and `Ctrl+P` for the file finder. fedit collapses both into the same prompt and lets the
   leading character (`:` / `/` / `@` / none) choose the mode.
4. **`Ctrl+O` is currently dead.** The handler exists in `Editor.update`
   ([Editor.fs#L1128-L1133](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L1128-L1133)) but
   `Input.tryMap` does not include `ConsoleKey.O` in the Ctrl whitelist, so the chord is dropped
   before it reaches `update`. Either add it to `Input.fs` or remove the handler.
5. **No `Ctrl+Shift+*` chords exist at all.** `Input.tryMap` discards the Shift bit whenever Ctrl is set, so the natural
   Zed/VS Code shortcuts (`Ctrl+Shift+P`, `Ctrl+Shift+F`, `Ctrl+Shift+S`) are structurally unavailable until `KeyInput`
   is extended.
6. **`Ctrl+Y` = redo** (Windows convention). Emacs would yank-paste; Vim would scroll up one line.
7. **No mouse, no macro recording, no registers, no jump list, no marks.**
8. **Word motion is Mac-style `Alt+←/→`.** A Linux user accustomed to `Ctrl+←/→` will be surprised — `Ctrl+←/→` is not
   mapped at all in `Input.tryMap`.
9. **Save-as has no chord.** It is only reachable via the palette (`writeas <path>`), or implicitly when saving a
   scratch buffer (which auto-opens the palette pre-filled with `:writeas `,
   see [Editor.fs#L311-L323](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L311-L323)).
10. **Search lacks "previous direction" and replace.** Backward navigation is `↑`, forward is `↓`/`Enter`; there is no
    `/`-vs-`?` distinction and no replace command anywhere.
11. **Help is gone.** The previous `help` palette command no longer exists; there is no `F1` binding because `F1` isn't
    in `KeyInput`. New users get only the startup notification (`Ctrl+P prompt  Ctrl+B tree  Ctrl+S save  Ctrl+Q quit`).
12. **Tab in the prompt autofills rather than cycles.** Users who expect VS Code/Zed-style Tab cycling will be
    surprised — `Shift+Tab`, `↑`, or `↓` are the cycle keys; `Tab` commits the highlighted candidate.
13. **Plugins can override `Ctrl+_` chords.** Plugin keybindings are checked before built-in editor handling
    ([Editor.fs#L729-L746](file:///Users/helge/code/fedit/src/Fedit/Editor.fs#L729-L746)), so a plugin binding to a
    chord that is also a global handler (e.g. `Ctrl+S`) will never fire — global chords intercept first. Only
    chords that would otherwise fall through to `runEditor` are pluggable.
