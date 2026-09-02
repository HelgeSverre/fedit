# Extension surface: plugin UI, LSP, and syntax

Status: proposal, 2026-09-02. Phase A shipped the same day (config-driven
grammars). Phases B–D are designs, not code.

This folds the deferred Tier 2 list in
[`docs/plans/2026-06-04-plugin-action-expansion.md`](../plans/2026-06-04-plugin-action-expansion.md)
into one shape, after re-reading every seam the plugin host, the LSP layer,
and the highlighter expose today.

## What exists

The plugin contract is three members and one DU. `IPluginHost` is
`RegisterCommand`, `RegisterKeybinding`, `Log`. A command is
`PluginContext -> PluginAction list`, synchronous, one request in flight at
a time over newline-framed JSON-RPC to the out-of-process host. The context
is the active buffer, all buffers, and the workspace root/selection/files.
Twenty `PluginAction` cases mutate the editor; all are applied in order
through `applyPluginActions` in `Editor.fs`.

What a plugin cannot do, by category:

| Category  | Missing                                                                                                                  |
| --------- | ------------------------------------------------------------------------------------------------------------------------ |
| UI        | Any panel, picker, prompt, status item, or decoration. The closest thing is a scratch buffer plus `SetBufferActivation`. |
| Events    | No hooks. Plugins run only when invoked by name, chord, or line activation.                                              |
| Async     | `Run` blocks the host; slow work stalls every other plugin call.                                                         |
| Read-back | No request/response; a plugin cannot ask the editor anything mid-run.                                                    |
| LSP       | Cannot register a server, read diagnostics, or provide hover/completion.                                                 |
| Syntax    | Cannot add a grammar or a query. Fixed by Phase A below, config-side.                                                    |
| Context   | No language id, dirty flag, edit tick, diagnostics, config values, or theme.                                             |

Every one of these seams in the editor is a closed DU: `DockPanel` (three
cases), `PickerKind` (six), `PickerActionId` (fifteen), `PromptSessionKind`,
`Status.resolveToken`, `Command`. The pattern for extending them is the same
each time: add a data-carrying case whose payload comes from a plugin, and
let the existing renderer paint it.

## Phase A: config-driven grammars (shipped)

`TreeSitter.Language(path, symbol)` accepts an absolute library path, so no
loader tricks are needed. A `languages` block in `config.json` adds grammars
and overrides queries:

```json
"languages": {
  "vue":  { "extensions": [".vue"], "library": "/…/libtree-sitter-vue.dylib", "queries": "/…/vue" },
  "json": { "queries": "/…/json" }
}
```

`HighlightRegistry.tryCreateWith` takes the specs; `readQuery` prefers the
user's directory over the embedded resource; `Highlight.detectLanguageWith`
consults user extensions first. Documented in `docs/syntax-highlighting.md`.

This is also the substrate for a plugin `RegisterLanguage` later: a plugin
ships the library and queries in its folder and the host forwards a
`LanguageSpec` in the scan registry. Nothing else changes.

## Phase B: plugin UI as data

No widget toolkit. A plugin never draws cells. It returns data that the
existing dock, picker, prompt, and status renderers already know how to
paint. Four actions, all append-only on `PluginAction`:

```fsharp
/// Styled text for panels: a theme slot, not a color, so it follows the theme.
type TextStyle = Plain | Accent | Muted | Error | Warning | Keyword | String
type Segment = { Text: string; Style: TextStyle }
type Line = Segment list

| ShowPanel of title: string * lines: Line list
| ShowPicker of title: string * items: PickerEntry list * onSelect: commandName
| PromptInput of label: string * initial: string * onSubmit: commandName
| SetStatusItem of text: string option
```

Model changes, each one field:

- `Model.Lsp.Panel : LspInfoPanel option` becomes `Model.InfoPanel :
InfoPanel option` with an `Owner = Lsp | Plugin of source` and styled
  lines. `Dock.panel` already falls through to it; hover and `:lsp log` keep
  working. The LSP owner keeps its dismiss-on-keypress rule.
- `PickerKind` gains `PluginItems of source * commandName`. `Pickers.itemsForKind`
  reads the items from `Model.PluginPicker`. Selecting an item invokes the
  named plugin command with the item id as the argument, through the same
  `RunPluginCommand` effect. Filter, inspector, and accessories come free.
- `PromptSessionKind` gains `PluginInput of source * commandName`. Enter
  invokes the command with the typed text.
- `Model.PluginStatus : Map<source, string>` and a `[PLUGINS]` token in
  `Status.resolveToken`.

Why this and not a canvas: the Model stays pure data, the renderer stays the
only painter, snapshot tests still cover plugin UI, and NativeAOT keeps
working because nothing new crosses the wire but strings and tags. The
`ShowDock` action deferred in the Tier 1 plan is `ShowPanel` with plain
segments.

What this does not give: arbitrary layouts, per-cell painting, or a
plugin-owned sidebar. If a real need for those appears, the next step is a
`Decoration` action (gutter marks and inline virtual text keyed by buffer
and line), still data, still painted by `View.renderEditor`.

## Phase C: v2 protocol, hooks and async together

The Tier 1 plan was right that hooks and async share one design. The
smallest version:

**Wire.** Every request carries an `id`; the host answers out of order. The
editor's `PluginHostClient` drops its single lock for a pending-request map.
The `PluginFence` used by macro replay keys on request id.

**Async commands.** `IPluginHost.RegisterAsyncCommand` with
`Run: PluginContext -> Task<PluginAction list>`. The host awaits it; the
editor already treats every plugin result as an async `Msg`. A cancellation
token flows when the buffer's edit tick moves on, the same stale guard LSP
responses use.

**Hooks.**

```fsharp
type PluginEvent = BufferSaved | BufferOpened | BufferChanged | FocusChanged
abstract RegisterHook: event: PluginEvent * commandName: string -> unit
```

Hook invocation is an ordinary `RunPluginCommand` with `PluginContext.Event
: PluginEvent option` set. The editor emits them from the `update` wrapper
that already diffs before/after models for highlight and LSP sync
(`lspSyncEffects`, `highlightEffects`), so the invocation points exist.
`BufferChanged` is debounced by edit tick, and actions returned by a hook run
do not fire hooks again. That single rule prevents loops.

**Context.** Add to `BufferView`: `Language: string option`, `Dirty: bool`,
`EditTick: int`, `Diagnostics: Diagnostic list`. Add `PluginContext.Config :
Map<string, string>` for the plugin's own `plugins.<name>` block in
`config.json`, which covers per-plugin settings without a storage API.

**Read-back.** With ids on the wire, the host can send requests to the
editor: `GetClipboard`, `GetConfig`. Defer until a plugin needs one.

## Phase D: LSP for plugins

Two things become trivial once B and C exist, and one stays hard.

- `RegisterLanguageServer of LanguageServerConfig`: the spec already crosses
  config.json; a plugin's registry carries it the same way and
  `LanguageServers.merge` takes it. Half a day.
- Diagnostics in the context (Phase C) plus `ShowPanel` and `ShowPicker`
  cover linting, quick-fix lists, and custom diagnostics views.
- Plugins as providers (hover, completion, code actions, formatting): a
  hook per feature, `ProvideHover` etc., with the async response feeding the
  same `LspHoverResolved` path. Worth doing only after the editor itself has
  completion and formatting, which it does not today. The LSP client
  implements sync, diagnostics, definition, references, hover, and nothing
  else.

## Order

1. Phase B `ShowPanel` and `SetStatusItem` first: two days, no protocol
   change, unblocks every "show me a list" plugin.
2. Phase C wire ids and async commands, then hooks.
3. Phase B picker and prompt, which want async to be useful.
4. Phase D.

## Refactors applied in this pass

- `Runtime.fs`: `post`, `attempt`, `continueOn` replace the hand-written
  Task/try/enqueue blocks; `lspPositionRequest` collapses the three LSP
  request interpreters; `cancelDispose` reused at shutdown.
- `Editor.fs`: `emptyPrompt` reused, `addBuffer`, `deleteOr`,
  `overWorkspace`, `lspNavigate`, `whenResponseFresh`, and one search engine
  (`repeatSearchWith`, `searchForWith`) for text and hex.
- `Pickers.fs`: `action` and `enabledWhen` replace 22 hand-written records.
- `View.fs`: one `paintSpan` for the selection and search overlays.
- `LspClient.fs`: `WhenRunning` gate for the three document notifications.
- `Primitives.fs`: one `writeAtomic`. `Renderer.Ansi` shared with `Terminal`.
- `MouseProtocol.fs`: one `parseTriple`.

Remaining, all small: the eight `Replay = Some { state with … }` updates,
the three escape-drain loops in `Terminal.fs` and `KittyImage.fs`, the
hex `MoveTo`/`ExtendTo` arms, and `setLanguageServerDisabled` versus
`setPluginDisabled`.
