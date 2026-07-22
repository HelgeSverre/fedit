# fedit macros

Record what you do, replay it, and keep it. A macro is a named register
holding a list of **steps** — editor actions and palette command lines —
captured while recording and re-executed on replay. Registers persist to
a plain-text file at `~/.config/fedit/macros` that you can edit by hand.

## Recording and replaying

| Chord          | Action                                     |
| -------------- | ------------------------------------------ |
| `Ctrl+Shift+M` | Start / stop recording into register `a`   |
| `Ctrl+Shift+R` | Replay register `a`                        |
| `Ctrl+Shift+.` | Repeat the last recorded or replayed macro |

Triggers are modifier chords so bare keys stay text input. Other registers
are reachable two ways:

- the `:macros` picker lists every register with its steps — `Enter`
  replays the selection, `r` records into it, `m` marks it as the last
  macro, `c` clears it (press twice), `e` opens the macros file;
- keybindings: `record-macro:<r>`, `replay-macro:<r>[:count]`, and
  `repeat-last-macro` in the [keybinds file](../README.md#keybindings),
  e.g. `editor ctrl+shift+b = replay-macro:b:10`.

The status bar shows `REC @a` while recording. Stopping with at least one
captured step commits the steps to the register (and writes the macros
file); stopping with nothing captured cancels and leaves the register
untouched, so a double-toggle can never wipe a macro.

## What gets recorded

Recording is **semantic**: registers hold what you did, never which keys
you pressed.

- Actions dispatched through keybindings or typing (consecutive typed
  characters coalesce into one `insert-text` step — one undo entry on
  replay).
- Composite bindings (`when` / `chain`, like the default `Ctrl+T` sidebar
  toggle) record the leaf actions that actually ran — the taken branch,
  each chain member — never the composite form, so every recorded step
  round-trips through the macros file.
- Pasting: `Ctrl+V` as a `paste` step, and a terminal-native (bracketed)
  paste as an `insert-text` step carrying the pasted text.
- Palette accepts, as `command:` steps carrying the command line. The
  prompt itself is never part of a macro: opening it, typing in it, and
  cycling completions are not steps, so replay re-executes the outcome
  with the prompt closed.
- An accepted search, as a `search-for:` step that replays synchronously.
- Picker-invoked replays and plugin chords (as the plugin's command).
- The repeat chord (`Ctrl+Shift+.`) as the `replay-macro:` step it
  resolved to at record time — the register it repeated, not a
  repeat-last marker that would re-resolve on replay.

Replay runs one step at a time through the editor's queue, so live input
stays interleaved. Steps that schedule async work (opening a file,
searching, saving, copying, pasting, plugin commands) **fence** the
queue: the next step waits for the result to land, with a 5 s timeout
that cancels the replay naming the step — so a replayed save → close
sees the buffer marked clean, and a replayed cut → paste never races the
OS clipboard. `Escape` cancels an in-flight replay. Replays can nest (a
macro may `replay-macro:` another); a register that would splice itself
while its own expansion is still open is refused, and nesting deeper
than 8 cancels.

## The macros file

`~/.config/fedit/macros` — line-oriented and human-editable, in the same
spirit as the keybinds file. Loaded at startup; rewritten in canonical
form whenever a recording commits or a register is cleared. Saving the
file through fedit reloads it immediately (`Macros reloaded (N
register(s))`), like the keybinds file; from outside fedit, restart or
re-save the file in a fedit buffer.

```
# fedit macros
a = insert-text:"TODO: " move-home
b = search-for:"let x" delete-forward command:"open README.md"
c = select-all copy goto:1 replay-macro:b:3
```

Grammar, one macro per line:

```
REGISTER = step step step ...
```

- `REGISTER` is a single character. Later lines for the same register win.
- `#` comments and blank lines are ignored. Because the write-through
  save rewrites the file canonically, custom comments are not preserved.
- A malformed line is skipped and reported (`macros:<line>: <reason>`)
  as a notification; every well-formed line still loads.

A **step** is one whitespace-separated token:

- **An action** in the same syntax as the right-hand side of a keybind
  line: `undo`, `move-word-left`, `goto:12:4`, `insert-text:"TODO: "`,
  `search-for:"let x"`, `replay-macro:b:3`, … Free-text payloads
  (`insert-text`, `search-for`) are double-quoted with backslash escapes
  `\"` `\\` `\n` `\t` `\r`; a bare whitespace-free payload also parses
  (`insert-text:fn`).
- **A palette command line** as `command:"<line>"` (the text you would
  type after `:`), with the same payload quoting — `command:messages`,
  `command:"open README.md"`. Replay re-parses the line — plugin
  commands and the numeric `:LINE[:COL]` goto included — and executes it
  without opening the prompt. A line that no longer parses cancels the
  replay.
- **A wholly-quoted action** — `"run-plugin:wordcount/wc selection"`,
  `"set-theme:gruvbox light"` — the escape hatch for the few action
  syntaxes that carry whitespace outside a quoted payload. The renderer
  emits this form automatically.

## Limits

- The last-macro marker (`Ctrl+Shift+.`) is per-session — it is not
  persisted to the file.
- Composed actions (`chain` / `when`) and `save-as` have no file syntax;
  recorded macros never contain them (composites record their leaf
  actions, and `:writeas <path>` records as a `command:` step).
- A replayed `search-for:` step always lands on the **first** match in
  the buffer. Match cycling (Up/Down inside the search prompt) is not
  recorded — follow the accept with `search-next` steps to reach a later
  match.
