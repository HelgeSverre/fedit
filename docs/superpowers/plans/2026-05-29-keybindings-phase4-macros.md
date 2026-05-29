# Keybindings Phase 4 — Macros (Record/Replay) Implementation Plan

> **STATUS: DEFERRED — do not build until explicitly prioritized.** This plan
> exists so the design is captured while the architecture is fresh; macros are
> the explicitly out-of-scope item of the keybindings spec (§1, §10). Build
> only on an explicit go-ahead. Everything below assumes **Phases 1, 2, and 3
> are already shipped** (the `Action` vocabulary + `runAction`, the `Chord`
> key model + sequence engine, and the user keymap file + `resolve`).

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user record a sequence of keystrokes into a register and
replay it N times — bound to modifier chords (never bare `Char`, which is
reserved for text). Net-new surface is deliberately tiny: three `Model`
fields, one `Effect` (`ReplayKeys`), a record-append hook in `update`, and
making the two already-present `Action` cases (`RecordMacro`, `ReplayMacro`)
live in `runAction`.

**Architecture:** Recording is pure state in the `Model`: while
`Recording = Some r`, `update` appends each incoming `KeyPressed chord` to
register `r` (unless `Replaying`, so injected keys are never re-recorded).
Replay is an `Effect.ReplayKeys (chords, count)` interpreted by the runtime,
which re-enqueues the recorded chords as `KeyPressed` msgs into the existing
`ConcurrentQueue` (drained each tick in `Runtime.fs`), bracketed by markers
that set/clear `Replaying`. Replay stops on the first no-op (a chord that
changes neither model nor effects) so "replay 9999×" terminates.

**Tech Stack:** F# (.NET 9 SDK pinned in `.dotnet`), xUnit + FsUnit +
FsCheck. Build/test via `just` only (never bare `dotnet`).

---

## Why this is deferrable (and low-risk when it lands)

The keybindings spec was authored so macros drop in without rework
([`spec §10`](../specs/2026-05-29-keybindings-spec.md)):

- `Action.RecordMacro of register: char` and
  `Action.ReplayMacro of register: char * count: int` **already exist** in
  `src/Fedit/Actions.fs` (lines 85–86) and are wired as **no-ops** in
  `runAction` (`Editor.fs`, the `RecordMacro _ -> model, []` /
  `ReplayMacro _ -> model, []` arms). Phase 4 only fills those bodies in.
- The MVU loop already drains a `ConcurrentQueue<Msg>` every tick
  (`Runtime.fs` `while not model.ShouldQuit` → `while queue.TryDequeue`), so
  replay needs no new scheduler — it enqueues `KeyPressed` msgs.
- Phase 3's keymap file can already bind a chord to `ReplayMacro`
  (`ctrl+shift+r = replay-macro:a`), so persistence reuses the existing
  line parser/renderer.

Macros are deferred because they are additive power-user surface with no
dependency from the core editing loop — nothing else in the spec waits on
them, and they carry a re-entrancy risk (replay feeding the input loop) best
taken on deliberately rather than bundled into the foundational phases.

---

## Scope & deviations from the spec

Implements [`spec §9`](../specs/2026-05-29-keybindings-spec.md) item 4, as
sketched in **§10**. In scope:

1. `Model` fields: `Registers: Map<char, Chord list>`, `Recording: char option`,
   `Replaying: bool`.
2. `Effect.ReplayKeys of Chord list * int`; markers `Msg.MacroReplayStart` /
   `Msg.MacroReplayEnd` to bracket the `Replaying` guard across enqueued keys.
3. The record-append hook in `update` (append each incoming `KeyPressed` to
   the recording register, unless replaying).
4. `RecordMacro r` (toggle) and `ReplayMacro (r, n)` (emit `ReplayKeys`) live
   in `runAction`.
5. Default bindings using **modifier chords** + a "repeat last macro" binding.
6. Persisting a named macro into `~/.config/fedit/keybinds` by serializing its
   `Chord list` (ties to Phase 3's parser/render).
7. Tests.

**Explicitly deferred follow-ons** (noted, not built):

- **Counters / numeric prefix** (vim's `5@a`) beyond the single `count` arg a
  bound `replay-macro:a:5` carries. No live count-accumulation UI.
- **Region-apply** (run a macro over each line of a selection).
- **Macro ring / history** (cycling recent macros). One "last macro" slot only.
- A which-key/recording-picker popup. The status-bar `recording @r` indicator
  is the only discoverability surface in this phase.

**Reserved-key constraint (CLAUDE.md / spec §10):** plain `Char` chords are
text input. Macros are triggered by **modifier chords**, never bare `q`/`@`
like vim. See "Judgment calls" for the chosen defaults.

---

## File structure

| File                               | Change | Responsibility                                                                                                             |
| ---------------------------------- | ------ | -------------------------------------------------------------------------------------------------------------------------- |
| `src/Fedit/Model.fs`               | modify | Add `Registers`/`Recording`/`Replaying` fields (+ defaults); `Effect.ReplayKeys`; `Msg.MacroReplayStart`/`MacroReplayEnd`. |
| `src/Fedit/Editor.fs`              | modify | Record-append hook in `update`; fill the `RecordMacro`/`ReplayMacro` arms in `runAction`; handle the two new marker msgs.  |
| `src/Fedit/Keymap.fs`              | modify | Default bindings for record/replay/repeat-last; serialize a `Chord list` into a keybinds line (reuse Phase 3 render).      |
| `src/Fedit/Runtime.fs`             | modify | `startEffect` arm for `ReplayKeys`: enqueue `MacroReplayStart`, the recorded `KeyPressed` chords, then `MacroReplayEnd`.   |
| `src/Fedit/Status.fs` / `View.fs`  | modify | Render a `recording @r` indicator while `Recording = Some r`.                                                              |
| `tests/Fedit.Tests/UpdateTests.fs` | modify | Record-captures-chords, replay-re-runs, replay-ignores-injected, stop-on-no-op, bound-key-triggers tests.                  |

No new files, so no `<Compile Include>` change — but note the CLAUDE.md
gotcha applies to any future split.

---

## Task 1: Model fields + Effect + marker messages

**Files:** `src/Fedit/Model.fs`

- [ ] **Step 1: Add the three macro fields to the `Model` record**

In the `Model` record (after `ShouldQuit`), add:

```fsharp
        /// Named macro registers. Key is the register char (e.g. 'a').
        /// Value is the chords recorded into it, in order.
        Registers: Map<char, Chord list>
        /// `Some r` while recording into register `r`; `None` otherwise.
        Recording: char option
        /// True while replaying a macro — the record-append hook checks this
        /// so injected keys are never re-recorded. Bracketed by
        /// `MacroReplayStart`/`MacroReplayEnd`.
        Replaying: bool
        /// The last register replayed, for "repeat last macro".
        LastMacro: char option
```

- [ ] **Step 2: Initialize them in the model constructor**

In the `initModel`/model-construction site (wherever the record is first
built — grep for `ShouldQuit = false`), add:

```fsharp
          Registers = Map.empty
          Recording = None
          Replaying = false
          LastMacro = None
```

- [ ] **Step 3: Add the Effect and the two marker messages**

In the `Effect` DU:

```fsharp
    /// Replay a recorded macro: re-enqueue these chords as KeyPressed msgs
    /// `count` times. Interpreted in Runtime.startEffect (it owns the queue).
    | ReplayKeys of chords: Chord list * count: int
```

In the `Msg` DU:

```fsharp
    /// Brackets a replay batch so the record-append hook can suppress
    /// recording of injected keys (sets/clears Model.Replaying).
    | MacroReplayStart
    | MacroReplayEnd
```

- [ ] **Step 4: Build**

Run: `just build`
Expected: PASS. (`update` will warn/error on the new non-exhaustive `Msg`
cases until Task 2 handles them — fix in Task 2; if the compiler treats
missing match arms as an error, add temporary `| MacroReplayStart | MacroReplayEnd -> model, []` arms now and flesh them out next.)

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Model.fs
git commit -m "feat(macros): add register/recording/replaying model fields and ReplayKeys effect"
```

---

## Task 2: Record-append hook + RecordMacro toggle

**Files:** `src/Fedit/Editor.fs`

- [ ] **Step 1: Append incoming chords to the recording register**

In `update`, in the `| KeyPressed chord ->` branch (Phase 2 renamed
`KeyInput` → `Chord`; the branch is at `Editor.fs` ~line 1400), wrap the
existing dispatch so each real keystroke is recorded **before** it is
handled, but only when recording and not replaying. Critically, the chord
that _toggles_ recording must not itself be recorded (see Step 2 — the toggle
chord is consumed by `runAction RecordMacro` and the append happens around the
result, so guard against appending the toggle chord).

The clean shape: append happens in `update` for any `KeyPressed chord` while
`Recording = Some r && not Replaying`, EXCEPT when that chord resolves to a
`RecordMacro` action (the stop-recording toggle). Compute the resolved action
once, append if it is not `RecordMacro`, then dispatch:

```fsharp
        | KeyPressed chord ->
            // Resolve once so we can decide whether to record this chord.
            let resolved = Keymap.resolve (contextOf model) [ chord ] model.Keymap
            let isRecordToggle =
                match resolved with
                | Some (RecordMacro _) -> true
                | _ -> false

            let model =
                match model.Recording with
                | Some r when not model.Replaying && not isRecordToggle ->
                    let appended = (model.Registers |> Map.tryFind r |> Option.defaultValue []) @ [ chord ]
                    { model with Registers = model.Registers |> Map.add r appended }
                | _ -> model

            // … existing Phase-2/3 dispatch (sequence engine → resolve → runAction
            //    → focus fallthrough) unchanged …
```

> **Design note — record at the input boundary, not per-Action.** Recording
> captures _chords_, not `Action`s, so a replay re-runs the same keymap
> resolution the user would get live (honouring any keymap reload between
> record and replay). This also means a multi-chord sequence is recorded as
> its individual chords; the sequence engine reassembles them on replay. The
> `isRecordToggle` guard keeps the stop key out of the register.

- [ ] **Step 2: Make `RecordMacro` toggle in `runAction`**

Replace the no-op arm `| RecordMacro _ -> model, []` with:

```fsharp
        | RecordMacro register ->
            match model.Recording with
            | Some active when active = register ->
                // Stop recording this register.
                { model with
                    Recording = None
                    LastMacro = Some register
                    Notification = Some(Notification.info $"Recorded macro @{register}") }, []
            | _ ->
                // Start (or switch) recording: clear the register, begin capture.
                { model with
                    Recording = Some register
                    Registers = model.Registers |> Map.add register []
                    Notification = Some(Notification.info $"Recording @{register}…") }, []
```

- [ ] **Step 3: Handle the marker messages in `update`**

Add arms (replace the temporary ones from Task 1):

```fsharp
        | MacroReplayStart -> { model with Replaying = true }, []
        | MacroReplayEnd -> { model with Replaying = false }, []
```

- [ ] **Step 4: Build + commit**

Run: `just build`
Expected: PASS.

```bash
git add src/Fedit/Editor.fs
git commit -m "feat(macros): record incoming chords and toggle RecordMacro"
```

---

## Task 3: ReplayMacro → ReplayKeys effect + Runtime re-enqueue + stop-on-no-op

**Files:** `src/Fedit/Editor.fs`, `src/Fedit/Runtime.fs`

- [ ] **Step 1: Make `ReplayMacro` emit `ReplayKeys` in `runAction`**

Replace `| ReplayMacro _ -> model, []` with:

```fsharp
        | ReplayMacro(register, count) ->
            match model.Registers |> Map.tryFind register with
            | Some chords when not (List.isEmpty chords) && count > 0 ->
                { model with LastMacro = Some register }, [ ReplayKeys(chords, count) ]
            | _ ->
                { model with Notification = Some(Notification.warning $"No macro in @{register}") }, []
```

> **Re-entrancy guard.** `ReplayMacro` must refuse to act while
> `model.Recording.IsSome` would otherwise capture the very replay it starts.
> Recording is already suppressed during replay by `Replaying` (Task 2
> hook), and the `isRecordToggle` path means the replay-trigger chord can be
> recorded into an _outer_ macro deliberately (nested replay). Decide
> explicitly: **forbid replaying register `r` while recording into `r`**
> (self-reference) — add a guard returning a warning. Nesting distinct
> registers is allowed.

- [ ] **Step 2: Interpret `ReplayKeys` in `Runtime.startEffect`**

`ReplayKeys` is pure in-memory queue manipulation, so it runs **synchronously**
on the dispatch thread (no `Task.Run`), unlike the I/O effects. In
`startEffect` (`Runtime.fs` ~line 178), add:

```fsharp
            | ReplayKeys(chords, count) ->
                // Bracket each pass with markers so the record-append hook
                // suppresses recording of injected keys. The main loop drains
                // these on subsequent ticks; stop-on-no-op (Step 3) trims runs.
                queue.Enqueue MacroReplayStart
                for _ in 1..count do
                    for chord in chords do
                        queue.Enqueue(KeyPressed chord)
                queue.Enqueue MacroReplayEnd
```

> **Why markers, not a per-msg flag:** the queue is FIFO and the loop drains
> it fully each tick, so a single `MacroReplayStart … MacroReplayEnd` bracket
> correctly spans all injected keys regardless of how many ticks the drain
> takes. Live user keystrokes arriving mid-replay are read by `Console.ReadKey`
> and appended to the _tail_ of the queue, so they land after `MacroReplayEnd`
> and are recorded/handled normally — they cannot interleave into the replay
> batch.

- [ ] **Step 3: Stop-on-no-op so large counts terminate**

A replayed chord that changes neither the model nor produces effects is a
no-op; once one occurs, the rest of the batch is pointless and can be a sign
of "macro ran off the end of the buffer." Implement by short-circuiting in the
**dispatch path** during replay: when `model.Replaying` and a dispatched
`KeyPressed` returns an unchanged model with no effects, drain and discard the
remaining `KeyPressed`/marker msgs up to and including the next
`MacroReplayEnd`.

Cleanest implementation point is the `dispatch` helper / drain loop in
`Runtime.fs` (lines 351–354, 439–441). Add a small helper that, on a detected
no-op during `Replaying`, calls `queue.Clear()`-of-the-replay-tail by
re-enqueuing only msgs after the next `MacroReplayEnd` (or, simpler: tag each
replayed `KeyPressed` so the drain can skip). Pick the simpler tag approach if
the queue-splice proves fiddly:

- Simplest robust option: have `ReplayKeys` enqueue a single
  `Msg.MacroReplayBatch of Chord list * int` instead of individual keys, and
  let `update` run the whole batch internally (folding `runAction`/resolve per
  chord, breaking on the first no-op). This keeps stop-on-no-op a **pure**
  decision in `update` and avoids queue surgery entirely.

> **Judgment call to confirm at build time:** prefer the
> `MacroReplayBatch`-in-`update` variant (pure, testable, terminates by
> construction) over re-enqueuing N×len individual msgs. The individual-msg
> form is what spec §10 sketches; the batch form is a strict improvement that
> keeps stop-on-no-op out of the impure runtime. If the batch form is taken,
> `ReplayKeys`/markers collapse into one `MacroReplayBatch` msg and Task 1's
> `MacroReplayStart`/`End` are unnecessary — note this in the commit. Decide
> and document; do not ship both.

- [ ] **Step 4: Build + commit**

Run: `just check`
Expected: PASS.

```bash
git add src/Fedit/Editor.fs src/Fedit/Runtime.fs
git commit -m "feat(macros): replay registers via ReplayKeys with stop-on-no-op"
```

---

## Task 4: Default bindings + repeat-last

**Files:** `src/Fedit/Keymap.fs`, `src/Fedit/Status.fs`/`View.fs`

- [ ] **Step 1: Add default record/replay bindings (modifier chords only)**

In `Keymap.defaults`, add (using the Phase 3 DSL helpers):

```fsharp
      // Macros — modifier chords (bare Char is reserved for text, CLAUDE.md).
      chord [Ctrl; Shift] (Char 'm') ==> RecordMacro 'a'      // toggle record → register a
      chord [Ctrl; Shift] (Char 'r') ==> ReplayMacro('a', 1)  // replay register a once
      chord [Ctrl; Shift] (Char '.') ==> ReplayMacro('a', 1)  // (see repeat-last, Step 2)
```

See "Judgment calls" for chord choice and conflict-check against existing
defaults. These are `Editor`-context (the default); macros do not fire in the
sidebar/prompt in this phase.

- [ ] **Step 2: Repeat-last-macro**

Add an `Action.RepeatLastMacro` (new case in `Actions.fs`) OR express
repeat-last as `ReplayMacro` against `model.LastMacro` inside `runAction`. The
latter needs no new chord-arg plumbing in the keymap parser; prefer adding a
zero-arg `RepeatLastMacro` action that resolves `model.LastMacro` at run time:

```fsharp
        | RepeatLastMacro ->
            match model.LastMacro with
            | Some r -> runAction (ReplayMacro(r, 1)) model
            | None -> { model with Notification = Some(Notification.info "No macro to repeat") }, []
```

Bind it: `chord [Ctrl; Shift] (Char '.') ==> RepeatLastMacro`. (Adding
`RepeatLastMacro` to the `Action` DU and its kebab name `repeat-last-macro` to
the Phase 3 parser is the only DU/parser change in this phase.)

- [ ] **Step 3: Recording indicator in the status bar**

While `model.Recording = Some r`, render `recording @r` (use the accent for
the indicator, consistent with the active-mode-indicator exception in
CLAUDE.md "one accent per surface"). Add a status token or splice it into the
existing status assembly in `Status.fs`/`View.fs`.

- [ ] **Step 4: Build + commit**

Run: `just check`
Expected: PASS.

```bash
git add src/Fedit/Keymap.fs src/Fedit/Actions.fs src/Fedit/Status.fs src/Fedit/View.fs
git commit -m "feat(macros): default record/replay/repeat bindings and recording indicator"
```

---

## Task 5: Persist a named macro into the keybinds file

A recorded register is in-memory only. Persistence = serialize its
`Chord list` into a `replay-macro:<r>` binding line in
`~/.config/fedit/keybinds`, reusing Phase 3's chord renderer and file writer.

**Files:** `src/Fedit/Keymap.fs`, `src/Fedit/Editor.fs`

- [ ] **Step 1: Decide the persistence shape**

A macro's _content_ (the chord list) and its _trigger_ (a bound chord) are
separate. The keybinds line format binds a stroke to an action, not a register
to a chord list. So persistence needs one of:

- **(a) A comment-encoded macro definition** the parser recognizes, e.g.
  `# macro a = ctrl+x left left enter` on its own line, loaded into
  `Model.Registers` at startup alongside the keymap. Lean and human-readable.
- **(b)** Bind `replay-macro:a` to a chord (trigger) AND store the chord list
  in (a). The trigger is an ordinary Phase 3 binding; only the _content_ needs
  the new `# macro` form.

Take **(a)+(b)**: add a `macro <r> = <chord> <chord> …` directive to the
Phase 3 parser (a sibling of binding lines, not a `Context stroke = action`
line), parsed into `Registers`. `KeymapIO.load` returns the registers too.

- [ ] **Step 2: Serialize via the Phase 3 chord renderer**

Add `Keymap.renderMacroLine : char -> Chord list -> string` reusing
`Chord.render` (Phase 2). A `:keybind save-macro <r>` verb (extend the Phase 3
`:keybind` verb) appends/replaces the `macro <r> = …` line in the keybinds
file via the existing config-write effect, then reloads.

- [ ] **Step 3: Load macros at startup**

`KeymapIO.load` already parses the file; extend its result to
`Keymap * Map<char, Chord list> * string list` (or carry registers in a record)
and seed `Model.Registers` in `KeybindsLoaded` handling.

- [ ] **Step 4: Build + commit**

Run: `just check`
Expected: PASS.

```bash
git add src/Fedit/Keymap.fs src/Fedit/Editor.fs
git commit -m "feat(macros): persist named macros to the keybinds file"
```

---

## Task 6: Tests

**Files:** `tests/Fedit.Tests/UpdateTests.fs`

- [ ] **Step 1: Record captures chords**

```fsharp
[<Fact>]
let ``recording captures each subsequent chord into the register`` () =
    let model = initModel ()
    let recording, _ = Editor.runAction (RecordMacro 'a') model
    recording.Recording |> should equal (Some 'a')
    let after, _ = Editor.update (KeyPressed(chord [] (Char 'x'))) recording
    (after.Registers |> Map.find 'a') |> should equal [ chord [] (Char 'x') ]
```

- [ ] **Step 2: Replay re-runs recorded chords**

Record a small edit, stop, replay once, assert the buffer reflects the macro
applied. (Drive through `Editor.update`/`runAction`; for the runtime
re-enqueue, prefer the `MacroReplayBatch`-in-`update` variant from Task 3 so
this is a pure assertion with no `ConcurrentQueue`.)

- [ ] **Step 3: Replay ignores injected keys (no self-recording)**

```fsharp
[<Fact>]
let ``keys injected during replay are not appended to a recording register`` () =
    // Recording @b while a replay of @a is in flight must not capture @a's keys.
    // With Replaying=true the append hook is suppressed.
    let model = { initModel () with Recording = Some 'b'; Replaying = true }
    let after, _ = Editor.update (KeyPressed(chord [] (Char 'z'))) model
    (after.Registers |> Map.tryFind 'b' |> Option.defaultValue []) |> should equal []
```

- [ ] **Step 4: Stop-on-no-op terminates a huge count**

```fsharp
[<Fact>]
let ``replay with a large count stops at the first no-op`` () =
    // A macro that moves left at column 0 is a no-op; replay 9999x must
    // terminate and not loop. With the batch variant this is a pure fold.
    // Assert it returns and the model is stable (idempotent).
    ()  // fill against the chosen Task-3 variant
```

- [ ] **Step 5: Bound key triggers replay**

```fsharp
[<Fact>]
let ``a keymap-bound chord triggers replay-macro`` () =
    // Bind ctrl+shift+r → ReplayMacro('a',1); record @a; press the chord;
    // assert a ReplayKeys/MacroReplayBatch effect (or the applied result).
    ()
```

- [ ] **Step 6: Self-reference guard**

```fsharp
[<Fact>]
let ``replaying the register currently being recorded is refused`` () =
    let model = { initModel () with Recording = Some 'a'; Registers = Map.ofList [ 'a', [ chord [] (Char 'x') ] ] }
    let after, effects = Editor.runAction (ReplayMacro('a', 1)) model
    effects |> should equal ([]: Effect list)
```

- [ ] **Step 7: Run + commit**

Run: `just check`
Expected: PASS.

```bash
git add tests/Fedit.Tests/UpdateTests.fs
git commit -m "test(macros): record capture, replay, injected-key suppression, stop-on-no-op"
```

---

## Final verification

- [ ] **Step 1: Full gate** — Run: `just check`. Expected: lint + build + test green.
- [ ] **Step 2: Manual smoke** — Run: `just run .`. Record (`Ctrl+Shift+M`) a
      few edits + motions, stop (`Ctrl+Shift+M` again), replay (`Ctrl+Shift+R`),
      confirm the edit re-applies at the new cursor; `Ctrl+Shift+.` repeats it;
      confirm the `recording @a` indicator appears/clears; confirm a replay that
      runs off the buffer end stops instead of hanging.
- [ ] **Step 3: Persistence** — `:keybind save-macro a`, confirm a
      `macro a = …` line appears in `~/.config/fedit/keybinds`, restart, replay,
      confirm it still runs.
- [ ] **Step 4: Regression** — existing keybindings, sequence engine, and
      mouse-wheel scroll (`Msg.MouseScrolled`, the SGR-mouse branch in `Runtime.fs`)
      are undisturbed.

---

## Self-review checklist (done while authoring)

- **Deferred & gated:** clearly marked DEFERRED; assumes Phases 1–3 shipped;
  rationale for deferral stated (additive, no core dependency, re-entrancy risk).
- **Spec coverage:** implements spec §9 item 4 / §10 — three Model fields, one
  Effect, the record-append hook, the two existing `Action` cases made live,
  default modifier-chord bindings, repeat-last, keybinds-file persistence.
- **Reserved-key rule honoured:** triggers are modifier chords
  (`Ctrl+Shift+M/R/.`), never bare `Char`; called out per CLAUDE.md / spec §10.
- **Replay terminates:** stop-on-no-op specified; preferred pure
  `MacroReplayBatch`-in-`update` variant flagged over the impure re-enqueue,
  with a "pick one, document it" instruction.
- **No self-recording:** `Replaying` guard on the append hook; self-reference
  replay refused.
- **Mouse-wheel untouched:** explicitly verified in final step.
- **Follow-ons fenced:** counters, region-apply, macro-ring noted as
  out-of-scope, not silently dropped.

```

```
