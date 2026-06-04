# Plugin Action Expansion Plan

**Goal:** Grow the plugin capability surface past the MVP text-munging set,
in two tiers — cheap additive `PluginAction` cases first, then the structural
work (events, async, storage) that unlocks real tooling plugins.

**Status:** Forward-looking. `SelectRange` shipped as the first post-MVP
action (2026-06-04); everything below is proposed, not built.

**Reference:** Original contract in
[`docs/archived/plans/2026-05-19-plugin-api.md`](../archived/plans/2026-05-19-plugin-api.md)
and [`docs/archived/specs/2026-05-19-plugin-api-spec.md`](../archived/specs/2026-05-19-plugin-api-spec.md).
Author guide: [`docs/plugins.md`](../plugins.md).

---

## Current surface

Plugins register `PluginCommand`s and non-plain `KeyChord`s. A command's
`Run` receives a read-only `PluginContext` snapshot (active buffer, all
buffers, workspace root) and returns a `PluginAction list` the host applies
in declaration order (`Editor.applyPluginActions`).

Actions today: `Notify`, `InsertText`, `ReplaceSelection`, `MoveCursor`,
`SelectRange`, `OpenFile`, `SaveActiveBuffer`, `RunCommand`, `SetClipboard`.

Constraints that shape everything below:

- **`Run` is synchronous and budgeted (< 50 ms).** No background work, no
  network, no awaiting.
- **Context is a snapshot.** Plugins read state once, at invocation; they
  cannot observe later changes or push state back mid-run.
- **Fire-and-forget.** A command emits actions and returns. There is no
  request/response, no callback, no way to read a result the host produces.
- **`PluginAction` is a closed but additive DU.** Plugins only _construct_
  actions, so new cases never break existing plugins; the host's exhaustive
  match forces every case to be handled. A plugin built against a newer API
  that references a new case simply won't load on an older host.

---

## Tier 1 — additive action cases (cheap)

Each is a new `PluginAction` case plus one arm in `applyPluginActions`, all
mapping to a primitive that already exists in `Buffer`/`Editor`. No API
redesign, no versioning hazard. Ordered by value.

| Proposed action                                                             | Maps to                  | Why it matters                                                                                                                          |
| --------------------------------------------------------------------------- | ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------- |
| `ReplaceRange of from: CursorPosition * to_: CursorPosition * text: string` | `Buffer.replaceRange`    | The big one. Edit at an arbitrary span without first moving cursor/selection. Enables formatters, codemods, surround-without-selecting. |
| `ClearSelection`                                                            | `Buffer.clearSelection`  | Collapse to a caret after an operation; pairs with `SelectRange`.                                                                       |
| `DeleteSelection`                                                           | `Buffer.deleteSelection` | Cut without supplying replacement text.                                                                                                 |
| `NewBuffer of name: string * text: string`                                  | new scratch buffer       | Show generated output as a real, editable buffer instead of a dock blob (e.g. a TODO index, a diff).                                    |
| `SwitchBuffer of id: int`                                                   | `Editor.jumpToBuffer`    | Act across the buffers already exposed in `AllBuffers`.                                                                                 |
| `ShowDock of title: string * lines: string list`                            | `DockInfo`               | Structured panel output; today multi-line `Notify` is overloaded for this.                                                              |

Open questions for Tier 1:

- `ReplaceRange` undo granularity — one undo step per action, or coalesce a
  whole `PluginAction list` into one? (Today each action is its own edit;
  `ReplaceSelection` already composes two primitives into one step.)
- `NewBuffer`/`SwitchBuffer` mutate buffer lifecycle the snapshot can't see.
  Decide whether subsequent actions in the same list observe the new buffer
  (they currently re-read `activeBufferState current` each arm, so yes).
- `SetClipboard` and `SelectAll` already have `RunCommand` equivalents for
  some cases; avoid shipping redundant actions where a built-in command
  already covers it cleanly.

## Tier 2 — structural (needs API surface changes)

These are not "add a DU case." They change the shape of the plugin contract
(`IPluginHost`, `PluginContext`, or the call protocol) and warrant a v2 API
review. Listed by unlock value.

1. **Event hooks.** `IPluginHost.RegisterHook(event, commandName)` for
   `OnSave`, `OnChange`, `OnOpen`, `OnFocus`. Requires invocation points in
   the update loop and a clear contract for hook-returned actions (and
   re-entrancy: an `OnChange` hook that edits must not loop). Unlocks
   format-on-save, lint-on-change, autosave policies.
2. **Async / long-running work.** Today `Run` is sync and < 50 ms. Real
   tooling (LSP clients, external formatters, network) needs either an async
   action model or a host-managed job (`RunProcess`/`Fetch`) whose result is
   delivered back into a follow-up plugin command. Demands a result-delivery
   protocol — the largest single change here.
3. **Per-plugin persistent storage.** A scoped key-value API so plugins stop
   hand-rolling file I/O (the TODO examples read the filesystem directly).
4. **Interactive input.** Prompts, pickers, quick-fix menus — a
   request/response surface, not fire-and-forget. Probably built on (2).
5. **Read-back actions.** `GetClipboard`, "what does this stroke resolve
   to", etc. — anything that returns a value to the plugin needs (2)'s
   delivery protocol; a synchronous DU case can't express it.

## Non-goals

- Multi-cursor editing — out of scope until the core has it.
- Letting plugins bind plain `Char` chords (reserved for text input).
- A general scripting/eval surface — plugins stay compiled F#.

## Sequencing

1. Land Tier 1 actions opportunistically as example plugins demand them;
   `ReplaceRange` first (highest leverage, lowest risk).
2. Before Tier 2, write a v2 API spec covering the call protocol change —
   hooks and async share a result-delivery design and should be designed
   together, not bolted on case by case.

## Per-action checklist (Tier 1)

Adding one action is a four-touch change — mirror how `SelectRange` was done:

- [ ] Add the case to `PluginAction` in `src/Fedit.PluginApi/Types.fs` (end
      of the DU; additive). Document anchor/cursor/coord semantics in a `///`.
- [ ] Add the arm to `applyPluginActions` in `src/Fedit/Editor.fs`, reusing
      the existing `Buffer`/`Editor` primitive. Mind the 1-based↔0-based
      coordinate translation (`pos.Line - 1`, `pos.Column - 1`); rely on
      `Buffer.positionToIndex` clamping for out-of-range coords.
- [ ] Add a row to the action table in `docs/plugins.md`.
- [ ] Add or extend an example plugin under `examples/` exercising it, so the
      `PluginsTests.fs` e2e path covers the new action end to end.
