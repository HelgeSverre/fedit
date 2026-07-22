# fedit language servers

fedit talks to language servers over stdio using the Language Server
Protocol (JSON-RPC under `Content-Length` framing). Servers start on
demand when a file they own opens, receive the full buffer text on every
edit, and push diagnostics back into the status bar. Navigation —
definition, references, hover — works from the keyboard; `:lsp` manages
the server set.

## Built-in servers

Three servers are configured out of the box:

| Name         | Command                              | File types               | Root markers                    |
| ------------ | ------------------------------------ | ------------------------ | ------------------------------- |
| `sema`       | `sema lsp`                           | `sema`                   | `sema.toml`                     |
| `typescript` | `typescript-language-server --stdio` | `ts`, `tsx`, `js`, `jsx` | `tsconfig.json`, `package.json` |
| `rust`       | `rust-analyzer`                      | `rs`                     | `Cargo.toml`                    |

A server only ever starts if its binary is on `PATH` and a matching file
opens. The workspace root passed to the server is found by walking up
from the file to the nearest root marker, falling back to the fedit
workspace root. One client runs per server + resolved root pair, so two
projects in one workspace each get their own server instance.

## Configuration

Servers live under a `languageServers` object in
`~/.config/fedit/config.json`. Each key is a server name; each value
takes `command`, `args`, `fileTypes` (extensions without the dot), and
`roots` (files or directories that mark a project root). A user entry
with a built-in's name replaces that built-in entirely — there is no
per-field merge.

```json
{
    "languageServers": {
        "gopls": {
            "command": "gopls",
            "args": [],
            "fileTypes": ["go"],
            "roots": ["go.mod"]
        },
        "sema": {
            "command": "/opt/sema/bin/sema",
            "args": ["lsp"],
            "fileTypes": ["sema"],
            "roots": ["sema.toml"]
        }
    },
    "disabledLanguageServers": ["typescript"]
}
```

`disabledLanguageServers` lists server names the editor must not start.
fedit writes this key when you toggle a server (`:lsp enable/disable`);
the `languageServers` block itself is yours — the editor never rewrites
it.

## Navigation

| Chord       | Action            | What it does                                                 |
| ----------- | ----------------- | ------------------------------------------------------------ |
| `F12`       | `goto-definition` | Jump to the definition; multiple candidates open a picker.   |
| `Shift+F12` | `find-references` | List references in a picker; `Enter` jumps, `Esc` closes.    |
| `F1`        | `hover`           | Show hover text in the dock; the next keypress dismisses it. |
| `Alt+-`     | `jump-back`       | Return to where the last jump left from (a 50-entry stack).  |

Rebind any of these in `~/.config/fedit/keybinds`, e.g.
`editor f9 = goto-definition`. Hover deliberately avoids VSCode's
`ctrl+k ctrl+i`: binding it by default would make bare `ctrl+k` a
sequence prefix and shadow plugin or user `ctrl+k` chords. On macOS
Terminal.app and iTerm2 default profiles, `Alt+-` needs "Use Option as
Meta key" (Terminal) / "Esc+" (iTerm2) enabled — otherwise Option+minus
types an en dash (`–`) instead of sending the chord; rebind `jump-back`
if you'd rather keep Option as-is.

Location pickers show one row per location — `relativePath:line:` plus a
preview. For definitions and references the preview is that line, read
from the open buffer when the file is open and from disk otherwise;
diagnostics rows preview `severity: message` instead. Type to filter,
`Enter` jumps (and pushes the jump stack), `Esc` closes.

## Diagnostics

Servers push diagnostics after every open and change. The status bar's
`[DIAGNOSTICS]` token shows compact severity counts for the active
buffer — `E2 W1` means two errors and one warning; a clean buffer shows
nothing. The segment stays uncolored on purpose (one accent per
surface); severity shows as text, not color, in the pickers too.

A custom `statusFormat` in config only renders the tokens it names —
keep `[DIAGNOSTICS]` in yours or the counts never appear (configs saved
before the token existed migrate automatically when they still carry
the old default format).

`:diagnostics` opens the active buffer's diagnostics in a location
picker with `severity: message` previews (`error: unknown symbol`).

## Managing servers

- `:lsp` — open the manager picker. Each configured server shows a
  status badge (`running`, `starting`, `failed`, `stopped`, `disabled`,
  `idle`); `r` restarts, `e` enables/disables, `l` shows the log.
- `:lsp status` — print one status word per server in the status bar.
- `:lsp restart [server]` — shut the server (or all servers) down and
  re-open every document it owns.
- `:lsp enable <server>` / `:lsp disable <server>` — toggle a server and
  persist to config. Disabling also shuts it down and drops its
  diagnostics; enabling re-opens matching documents immediately.
- `:lsp log [server]` — show the recent stderr ring (last ~200 lines per
  client, dock shows the tail) — the first place to look when a server
  misbehaves.

## How it works

The MVU seam mirrors syntax highlighting. A per-dispatch diff
(`Editor.lspSyncEffects`) compares open file-backed buffers before and
after each update: a path appearing emits `didOpen`, an edit-tick move
emits `didChange` (full text; the buffer's `EditTick` is the document
version), a path vanishing emits `didClose`. The Runtime interprets
those effects on a single task chain per process, so a `didChange` can
never outrun its `didOpen`, and materializes buffer text off the update
thread.

Requests (definition, hover, references) carry the buffer's `EditTick`;
responses echo it, and the update layer drops any result whose tick no
longer matches or whose buffer is no longer active — a stale position
must never move the cursor or yank the view from another buffer. Buffers are
LF-normalized in memory, and both fedit and LSP address positions as
0-based line + UTF-16 code unit, so positions cross the wire without
conversion.

Servers print startup banners to stderr; fedit drains and rings that
output (`:lsp log`) and never treats it as failure. A server that exits
during shutdown is normal (`sema` force-exits shortly after `shutdown`);
a server that exits mid-session shows as `failed` in `:lsp` with the
exit code in the log.

## Troubleshooting

- **Nothing happens on F12** — check `:lsp status`. `idle` means no
  matching file has opened yet; `failed` means the spawn or handshake
  broke, including a binary missing from `PATH` — `:lsp log <server>`
  has the stderr.
- **Definitions in other files come back empty right after startup** —
  servers index the workspace asynchronously (sema scans `.sema` files
  after `initialized`); retry after a moment.
- **Config changes don't apply** — `languageServers` is read at startup;
  restart fedit after editing the block. `:lsp enable/disable/restart`
  apply immediately.
- `fedit --log /tmp/fedit.log` traces every LSP effect and message
  dispatch alongside the rest of the editor's event flow.
