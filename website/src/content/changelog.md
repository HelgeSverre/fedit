## v1.9.0 — 2026-07-29

- Added language-server configuration, diagnostics, hover, definition and reference navigation, jump-back, and the `lsp` manager.
- Added `pyright` to the built-in language-server registry, and python shebang detection for highlighting.
- Reworked macros around semantic actions, persistent registers, safe replay fencing, and an editable macros file.
- Added binary-file hex editing: byte-exact saves with a one-time `.bak` backup, nibble and ASCII overwrite typing, byte search, and `replace` over byte sequences.
- Added syntax-tree expand and shrink selection on `Shift+Alt+Up` and `Shift+Alt+Down`.
- Extended `replace` to text buffers with exact literal matching.
- Added close-buffer, consistent dirty-buffer quit protection, message history, which-key hints, multi-click selection, and clearer search accept/cancel behavior.
- Moved language-server navigation to terminal-safe JetBrains-style bindings.

## v1.8.0 — 2026-07-19

- Added `Alt+Up` and `Alt+Down` line movement.
- Moving a selection moves every covered line as one edit, preserves the selection, clamps at file boundaries, and creates one undo step.
- Added matching `MoveLinesUp` and `MoveLinesDown` plugin actions.

## v1.7.3 — 2026-07-04

- Updated the bundled Sema tree-sitter grammar to `sema-lisp/tree-sitter-sema` v0.2.0.
- Added highlighting for short lambdas, regex literals, dereference operators, and shebang lines.

## v1.7.2 — 2026-07-04

- Corrected mouse drag selection so the glyph under the release point is included.

## v1.7.1 — 2026-06-20

- Made `--log` dispatch tracing safe under NativeAOT.
- Added completion-command coverage to the NativeAOT release smoke tests.

## v1.7.0 — 2026-06-20

- Made NativeAOT the default release flavor.

## v1.6.0 — 2026-06-20

- Added NativeAOT archives alongside ReadyToRun builds for every supported platform.

## v1.5.1 — 2026-06-20

- Fixed plugin API resolution from the sidecar assembly in single-file builds.
- Expanded NativeAOT release smoke tests across all five target platforms.

## v1.5.0 — 2026-06-19

- Moved plugin loading and invocation into the separate `Fedit.PluginHost` process.
- Added the small JSON-RPC protocol between the NativeAOT editor and the managed plugin host.
- Kept the existing plugin authoring contract while preventing slow or crashing plugins from freezing the editor.
- Normalized paths across macOS, Linux, and Windows.

## v1.4.0 — 2026-06-19

- Reduced time to first paint from roughly 412 ms to 133 ms.
- Added shell installers for macOS, Linux, and Windows.
- Clarified Homebrew and direct-download installation paths.

## v1.3.0 — 2026-06-16

- Added terminal capability detection, mouse interaction, preview buffers, sidebar reveal, and the prompt-session picker system.
- Expanded syntax highlighting with AppleScript, ReScript, Zig, Sema, and TOML grammars.
- Added the Ayu Dark full-surface theme.
- Expanded the plugin API with buffer activation, open-at-position, scratch buffers, range replacement, reveal, preview-open, and workspace file context.
- Improved incremental editing and syntax parsing performance.
- Packaged grammar libraries and plugin API sidecars with releases.

## v1.2.0 — 2026-06-04

- Added the `SelectRange` plugin action for setting arbitrary selections.

## v1.1.1 — 2026-06-04

- Added `fedit themes --json`.
- Updated the theme catalog and editor previews to use resolved runtime colors.

## v1.1.0 — 2026-06-04

- Added user keybindings, multi-key sequences, binding inspection, and JSON keybinding export.
- Added macro recording and replay.
- Added Tree-sitter highlighting, bundled language queries, and the `syntax` command.
- Added configurable mouse-wheel scrolling, `scrollOff`, buffer switching, and status-line templates.
- Added the F# plugin API, plugin installation commands, and shell completion generation.
- Added full-surface themes and opt-in Nerd Font tree icons.
- Added the `config` command and editable configuration file.
