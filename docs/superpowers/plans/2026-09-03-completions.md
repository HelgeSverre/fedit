# Completions and the completion provider interface

Status: in progress, 2026-09-03. Phase 1 shipped (buffer-word popup, accept-as-edit); phases 2-4 pending. Follows the extension
surface shipped in `docs/superpowers/2026-09-02-extension-surface.md`; the
provider interface is the last item that document deferred.

## Goal

Code completion in the editor: a popup of candidates while typing, from
the language server, from the words already in the buffer, and from
plugins that register a provider. Accepting a candidate replaces the word
being typed. Everything stays pure data in the Model; the dock paints it.

Acceptance gate: with a language server configured (the `sema` default
or pyright), typing an identifier prefix shows server candidates; with no
server, typing shows words from the buffer; a plugin provider registered
by `examples/showcase` contributes candidates for `.showcase` files; Tab
accepts and replaces the prefix; Escape dismisses; a macro that accepted
a completion replays the same text without a server. Proven by the suite
and by a pty run of the real editor.

## What exists

- `CompletionItem = { Label; ApplyText; Detail; Kind }` and
  `DockPanel.DockCompletions of title * items * selectedIndex` in
  `Primitives.fs`, painted by `View.renderDock`, sized by `Dock.metrics`.
  Today only the prompt uses them (files, commands, buffers).
- The prompt's cycling and apply logic: `cycleCompletion`,
  `applyCompletion` in `Editor.fs`. Prompt-specific (rewrites prompt text).
- LSP: `textDocumentPositionRequest` in `LspWire.fs` builds any
  `textDocument/*` position request; `LspClient.sendRequest` correlates
  ids; `LspServerCapabilities` has no completion field; the initialize
  request advertises no completion capability. `LspPositionRequest`,
  `lspPositionRequest` in `Runtime.fs`, and `whenResponseFresh` in
  `Editor.fs` give the request/response/stale-guard shape to copy.
- Buffer word motion: `wordIndexLeft`, `wordIndexRight` in `Buffer.fs`
  define word boundaries; there is no `wordAt` yet.
- Editor-focus key dispatch: `runEditor` handles text keys after the
  keymap resolves chords; the which-key panel already shows how a
  transient state intercepts keys and owns the dock.
- Plugin hooks and async commands: `RegisterHook`, `RegisterAsyncCommand`,
  request ids, cancellation on edit tick (`CancelPluginRuns`).

## Design

The completion popup is Model state, painted by the existing dock. It is
not a new focus: keys route to it from inside `runEditor` while it is
open, the way the escape-precedence chain already special-cases panels.
Candidates arrive asynchronously from three source kinds and merge into
one ranked list keyed by the request's edit tick, reusing the LSP
stale-guard.

### Model

```fsharp
type CompletionSource =
    | FromServer of serverName: string
    | FromBuffer
    | FromPlugin of source: string

type CompletionCandidate =
    { Label: string
      /// Text that replaces the range. Snippet syntax is flattened:
      /// `$1`, `${1:x}` become their placeholder text; the caret lands
      /// at the first placeholder if any, else after the insert.
      Insert: string
      /// Replace range in the buffer, 0-based char indices. Defaults to
      /// the word prefix before the cursor; a server `textEdit` overrides.
      Replace: int * int
      Detail: string
      Kind: string          // "function", "variable", "keyword", "text", …
      Source: CompletionSource
      /// Server-provided ordering hint (`sortText`); "" otherwise.
      SortKey: string }

/// An open completion popup for the active buffer.
type CompletionState =
    { BufferId: int
      /// The edit tick the request was fired at; a response tagged with an
      /// older tick is dropped (the buffer moved on).
      EditTick: int
      /// The word prefix under the cursor when opened; used to filter as
      /// the user keeps typing without re-querying every source.
      Prefix: string
      /// Char range the prefix occupies, so filtering and accept share it.
      PrefixRange: int * int
      /// Every source's candidates, merged; re-ranked on each keystroke.
      Candidates: CompletionCandidate list
      Selected: int
      /// Sources still expected to answer this request, for the "…" hint.
      Pending: Set<CompletionSource>
      /// Set false once the user moves the selection or edits, so an
      /// auto-opened popup that the user ignores never steals Enter.
      Interacted: bool }
```

`Model` gains `Completion: CompletionState option`. It is cleared on
buffer switch, buffer close, focus change away from the editor, Escape,
and any edit that leaves the prefix range (cursor moved out, or a
non-word character typed).

### Effects and messages

```fsharp
// Effect
| RequestCompletions of CompletionRequest   // { Path; Position; EditTick; BufferId; Servers; Prefix; PrefixRange; Providers }
// Msgs
| CompletionsArrived of source: CompletionSource * editTick: int * bufferId: int * CompletionCandidate list
| CompletionsFailed of source: CompletionSource * message: string
```

One `RequestCompletions` effect fans out in `Runtime`: an LSP
`textDocument/completion` per configured server for the file, a synchronous
buffer-word scan posted back as a `CompletionsArrived`, and a
`RunPluginCommand`-style invoke per registered provider. Each source posts
its own `CompletionsArrived`; the editor merges by `(bufferId, editTick)`
through `whenResponseFresh`, drops stale ticks, and removes the source
from `Pending`. Buffer-word candidates are computed in the effect (not the
update) so the update stays pure and cheap.

### LSP completion

- Advertise `completion` in the initialize request's client capabilities
  (`LspWire.fs`), with `completionItem.snippetSupport = false` for now
  (snippets are flattened, not driven).
- Add `CompletionProvider: bool` to `LspServerCapabilities` and read it
  from the initialize result; skip the server as a source when false.
- `completionRequest id uri position` via the existing
  `textDocumentPositionRequest`; `readCompletionResult` parses both
  `CompletionItem[]` and `{ isIncomplete, items }`, mapping `label`,
  `insertText`/`textEdit`, `kind` (the LSP kind enum → our string),
  `detail`, `sortText`, and a `textEdit` range into `Replace`. Cap the
  item count with a new `ResourceLimits.LspCompletionCount`.
- `LspClient.SendCompletion` mirrors `SendHover`. `resolve` (extra detail
  on selection) is out of scope for this slice.

### Buffer-word source

`Buffer.wordAt (index)` returns the word range and text under a char
index, built from the existing `classify`/`wordIndexLeft` logic. The
source scans the active buffer (and optionally all open buffers, capped)
for distinct words sharing the prefix, excluding the word being typed,
ranked by proximity to the cursor then frequency. Always available, so
completion works with no server and inside strings and comments where a
server declines.

### Plugin provider interface

A fourth registration on `IPluginHost`, async by nature:

```fsharp
type CompletionItemSpec =
    { Label: string
      Insert: string
      Detail: string
      Kind: string
      SortKey: string }

abstract member RegisterCompletionProvider:
    fileTypes: string list *
    provide: (PluginContext -> CancellationToken -> Task<CompletionItemSpec list>) -> unit
```

- Providers are keyed by file extension, like grammars and servers; a
  provider only runs for a buffer whose language matches. The registry
  carries `CompletionProviders: (string * fileTypes) list` (source +
  types) across the wire; the runner lives host-side like
  `AsyncCommands`.
- `PluginContext` already carries the buffer text, cursor, and language.
  The provider returns candidates with the prefix `Replace` range filled
  in editor-side (a plugin does not compute char offsets). A provider that
  wants a different range is a later addition.
- Reuses the async plumbing verbatim: request id, `RunPluginCommand`
  effect shape, cancellation when the buffer's edit tick moves on. A
  provider run outstanding when the popup closes is cancelled.
- Wire: one new registry array (`completionProviders`) and one new
  invoke method the host serves against its provider runners, returning
  `CompletionItemSpec list`.

### Ranking and filtering

One merge function, pure, unit-tested in isolation:

1. Filter to candidates whose `Label` matches the current prefix
   (case-insensitive prefix match first, then subsequence as a fallback
   tier).
2. Order by tier, then `SortKey` when present, then source priority
   (server > provider > buffer), then label length, then alphabetical.
3. De-duplicate by `(Label, Insert)`, keeping the highest-priority source.
4. Cap at `Config.CompletionLimit` (the existing key).

Typing more narrows the list in place without re-querying, until the
prefix leaves the range or a trigger character (`.`, `::`) forces a fresh
server request.

### Interaction

- **Trigger.** Manual: a keybinding (`Action.TriggerCompletion`, default
  `Ctrl+Space`) always opens. Automatic: opening after an identifier
  character or a server trigger character, debounced, gated by a
  `completions` config bool (default on). Auto-open sets `Interacted =
false`.
- **Keys while open** (handled in `runEditor` before the text-insert
  arm): Up/Down and Ctrl+P/Ctrl+N move the selection; Tab and Enter
  accept the selection (Enter inserts a newline instead when
  `Interacted = false` and nothing is filtered — never hijack a newline
  from a popup the user ignored); Escape closes; any other text key types
  through and re-filters.
- **Accept.** Replace `PrefixRange` (or the candidate's `Replace`) with
  `Insert` as one undo entry, place the caret, close the popup. Record the
  accept as a macro step so replay reinserts the same text with no server:
  the recorded step is a plain `InsertText`/`ReplaceRange`, not "accept
  completion" (mirrors how search accept records `SearchFor`, not the
  keystrokes).
- **Paint.** `DockCompletions` already renders label + detail + selection.
  Add a source badge (`lsp`/`buf`/plugin name) and a `…` while `Pending`
  is non-empty. The popup shares the dock; it loses to an open prompt
  (you cannot complete in the buffer while the command prompt is up) and
  to the which-key panel, matching `Dock.metrics` precedence.

## Phases

Each phase is a commit with tests green; the buffer-word source lands
first so the UI has data with no LSP dependency.

1. **Popup state + buffer words + accept.** SHIPPED. `CompletionState`,
   `Buffer.completionPrefix`/`Buffer.words`, `buildCompletion`,
   key handling in the `KeyPressed` intercept, accept-as-edit with macro
   recording, the `DockCompletions` source badge. Deviation from the plan:
   the buffer-word source is synchronous and in-model (no
   `RequestCompletions` effect, no `CompletionsArrived` msg yet) — a buffer
   scan is pure and cheap, and staying synchronous keeps macro replay
   deterministic with no fences. The async effect/msg path lands in phase 2
   for the LSP source, merging into the already-open synchronous popup.
   Gate met: typing shows buffer words, Tab replaces the prefix, a macro
   replays the inserted text; proven by the suite and a pty run.
2. **LSP completion.** Capability advertise + read, `completionRequest`,
   `readCompletionResult`, `SendCompletion`, the server source in the
   fan-out, `LspCompletionCount`. Gate: server candidates appear for a
   configured language, merged above buffer words.
3. **Plugin providers.** `RegisterCompletionProvider`, registry array +
   wire, host runner, the provider source in the fan-out with edit-tick
   cancellation, a `showcase` provider for `.showcase`. Gate: the provider
   contributes candidates end to end through the real host.
4. **Polish.** Trigger characters forcing a re-query, debounce tuning,
   the `completions` config bool, docs (`docs/completions.md`, the plugins
   guide row, the README config table).

## Open questions

- **Auto-open aggressiveness.** Debounce interval and whether auto-open
  fires inside comments/strings. Default conservative (manual-friendly),
  tune in phase 4.
- **All-buffers word scan.** Phase 1 scans the active buffer only; adding
  every open buffer is a cap-bounded follow-up if it proves useful.
- **Snippet placeholders.** Flattened to text this slice. Full tab-through
  placeholders are a separate feature and not blocked by this shape.
- **`completionItem/resolve`.** Deferred; detail comes from the initial
  item. Add when a server's initial items are too thin.

## Non-goals

- Signature help, code actions, inlay hints — separate LSP features.
- A second focus mode; the popup is buffer-focus state, not a mode.
- Fuzzy-match scoring beyond prefix-then-subsequence tiers this slice.
