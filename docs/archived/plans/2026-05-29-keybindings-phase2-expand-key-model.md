# Keybindings Phase 2 — Expand the Key Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the closed `KeyInput` DU with a structured `Chord`/`KeyStroke` key model (`Keys.fs`), decode the full key universe in `Input.tryMap` (Ctrl+Shift, F-keys, Ctrl+←/→ — without dropping Shift-under-Ctrl), change `Msg.KeyPressed of KeyInput` to `Msg.KeyPressed of Chord`, convert the three dispatch sites to match on `Chord`, fix the dead `Ctrl+O` (wip audit #4) and the word-motion gaps (#8), and build the multi-key **sequence engine** (`Model.PendingPrefix` + status-bar pending indicator + timeout). The default chord set still fires byte-for-byte as today.

**Architecture:** `Keys.fs` (compiled right after `Primitives.fs`) introduces `Modifier`/`Key`/`NamedKey`/`Chord`/`KeyStroke`, a `Chord.toKeyChord : Chord -> KeyChord option` bridge for the plugin host, and `Chord.parse`/`render` helpers (used by the status indicator and Phase 3's parser — `render` lands now, `parse` is stubbed/minimal). `Input.tryMap` becomes `ConsoleKeyInfo -> Chord option`, collecting modifiers into a `Set<Modifier>` and normalizing per spec §6.1. `Msg.KeyPressed` carries a `Chord`; `Msg.MouseScrolled of int` is untouched. The three dispatch sites in `Editor.fs` (the global `Ctrl` handler in `update`, `runEditor`, `runSidebar`) pattern-match `Chord` records instead of `KeyInput` cases. A sequence engine threads a new `Model.PendingPrefix` field; the runtime tick clears it on timeout. Phase 2 wires and tests the engine but ships **no built-in multi-key sequence** — sequences only become bound once the default keymap lands in Phase 3.

**Tech Stack:** F# (.NET 9 SDK pinned in `.dotnet`), xUnit + FsUnit + FsCheck. Build/test via `just` only (never bare `dotnet`).

---

## Scope & deviations from the spec

This plan implements **only Phase 2** of [`docs/superpowers/specs/2026-05-29-keybindings-spec.md`](../specs/2026-05-29-keybindings-spec.md) §9.2. Phase 1 (the `Action`/`Cond` vocabulary, `runAction`/`evalCond`, the three sites routed through `runAction`, `Action.ofCommand`) has **already shipped on `main`** — `Actions.fs` exists, the dispatch sites already delegate motion/edit/nav to `runAction`. Phase 2 changes only the **key representation** those sites match on; the action bodies are untouched.

Deliberate, documented deviations from the spec (the spec predates the mouse-wheel feature and assumes a different compile order):

1. **Mouse-wheel scrolling coexists, unchanged.** `Msg.MouseScrolled of int` (added after the spec) **stays as-is**. `Input.parseSgrMouse` and the Runtime CSI-drain branch that dispatches `MouseScrolled` are **not touched** — only the keyboard branch changes, yielding a `Chord` and dispatching `KeyPressed chord`. Mouse scroll is an ambient input event handled in `update` beside `Resize`, deliberately outside the keybinding/`Action`/sequence layer.

2. **Sequence engine present, but no sequence is bound yet.** Phase 2 builds the engine (`Model.PendingPrefix`, prefix detection, pending status indicator, timeout) and tests it against a synthetic keymap in the unit suite — but the editor's hardcoded dispatch has no multi-key binding, so **no built-in sequence fires until the default keymap lands in Phase 3**. Until then the engine is a dormant pass-through: every real chord resolves on the first stroke. This mirrors Phase 1's "hard-coded defaults still" stance. (Concretely: the engine needs a `Keymap` to know which strokes are sequence prefixes; Phase 2 has no `Keymap` type yet, so the prefix set is empty and the engine is a no-op on the live path. The engine logic lives in a small pure helper that takes an explicit prefix-set so it is fully testable now.)

3. **No `Keymap.fs`, no `resolve`, no user file.** The user keybinds file, `Keymap.fs`, `resolve`, `Action.ofCommand` precedence flips, and macros are Phases 3/4 — explicitly out of scope. The three dispatch sites stay **hardcoded** `Chord` matches (e.g. `Ctrl+S` → `{ Mods = set [ Ctrl ]; Key = Char 's' }`); they are not yet keymap-driven.

4. **`Chord.parse` is minimal in Phase 2.** Only `Chord.render` (chord → display string, for the pending indicator) is fully needed now. A full grammar parser is Phase 3. Add `render` and a small internal `chord`/`namedKey` constructor surface; defer `Chord.parse` of the line grammar to Phase 3. (Stating this so the plan does not over-build the parser.)

5. **Compile-order reality.** The spec's §5 table places `Keys.fs` "after `Primitives.fs`" — correct, and it lands there. But the spec table also predates the current fsproj, where `Actions.fs` already sits after `Commands.fs` and **`Input.fs` is compiled after `Editor.fs`/`Status.fs`** (not before `Model.fs`). `Chord` must therefore live in `Keys.fs` near the top so `Model.fs`, `Editor.fs`, **and** `Input.fs` can all see it. `Chord.toKeyChord` references `Fedit.PluginApi.KeyChord`; `PluginApi` is a ProjectReference available to every source file, so `toKeyChord` can live in `Keys.fs` (it does not need `Plugins.fs`, which only defines the registry).

The deferred `RecordMacro`/`ReplayMacro`/`ReloadKeybinds` actions stay no-ops (unchanged from Phase 1).

---

## File structure

| File                               | Change     | Responsibility                                                                                                                                                                                |
| ---------------------------------- | ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Fedit/Keys.fs`                | **create** | `Modifier`, `Key`, `NamedKey`, `Chord`, `KeyStroke` DUs; `Chord.render`; internal `chord`/`named` constructors; `Chord.toKeyChord : Chord -> KeyChord option`; `Sequence` engine pure helper. |
| `src/Fedit/Fedit.fsproj`           | modify     | Add `<Compile Include="Keys.fs" />` immediately after `Primitives.fs`.                                                                                                                        |
| `src/Fedit/Input.fs`               | modify     | Rewrite `tryMap` to `ConsoleKeyInfo -> Chord option` (collect modifiers, Ctrl+Shift, F-keys, Ctrl+←/→, normalization). `parseSgrMouse` **unchanged**.                                         |
| `src/Fedit/Model.fs`               | modify     | `Msg.KeyPressed of Chord` (was `KeyInput`); add `Model.PendingPrefix: (Chord list * int) option` + its default. `MouseScrolled`, `Effect`, all async msgs unchanged.                          |
| `src/Fedit/Editor.fs`              | modify     | Rewrite the three dispatch sites to match `Chord`; replace `toKeyChord` with `Chord.toKeyChord`; wire the sequence-engine pass + pending-prefix clearing; route `Ctrl+O`, add `Ctrl+←/→`.     |
| `src/Fedit/Status.fs`              | modify     | Add a `[pending]` status token (or pending-prefix prefix on `[mode]`) rendering `Chord.render` of the in-flight prefix.                                                                       |
| `src/Fedit/Runtime.fs`             | modify     | Keyboard branch: `Input.tryMap keyInfo` now yields a `Chord` → `dispatch (KeyPressed chord)`. Add the per-tick pending-prefix timeout dispatch. Mouse-drain branch **untouched**.             |
| `tests/Fedit.Tests/InputTests.fs`  | **create** | `tryMap` table tests: each `ConsoleKeyInfo` shape → expected `Chord` (Ctrl+Shift, F-keys, Ctrl+arrows, case folding, bare-vs-Ctrl Shift handling).                                            |
| `tests/Fedit.Tests/UpdateTests.fs` | modify     | Migrate existing `KeyPressed(Ctrl 'x')`/`KeyPressed Right` literals to `Chord`; add sequence-engine tests; add `Chord.toKeyChord` round-trip + plugin-binding-still-fires tests.              |

**Compile-order rule (CLAUDE.md `FS0225` gotcha):** `Keys.fs` must be listed in `Fedit.fsproj` `<Compile>` **and** committed, or CI fails. It goes immediately after `Primitives.fs` and before `PieceTable.fs`, so every later file (`Model.fs`, `Editor.fs`, `Status.fs`, `Input.fs`, `Runtime.fs`) sees `Chord`.

`KeyInput` (in `Primitives.fs`) is **removed** at the end of this phase (spec §2.4: "`KeyInput` does not survive as a parallel type"). Confirm no source references it before deleting (Task 7).

---

## Task 1: Characterization safety net

Lock in current behavior **before** changing the key representation. These pass against today's `KeyInput`-based code; after the migration the same assertions (rewritten to `Chord` literals) must still hold — that is the "no behavior change for existing bindings" proof.

**Files:**

- Test: `tests/Fedit.Tests/UpdateTests.fs` (verify the Phase-1 net is intact)

- [ ] **Step 1: Establish the green baseline**

Run: `just test`
Expected: PASS — the Phase-1 characterization net (motions, Shift-selection, `Ctrl+A/C/X/V/Z/Y`, Tab indent, `Ctrl+R` rescan, sidebar nav, tri-state `Ctrl+B`, incremental filter) is already in `UpdateTests.fs`. Note the exact assertions; they get re-expressed in `Chord` form in Task 5.

- [ ] **Step 2: Inventory every `KeyPressed(...)` / `KeyInput` literal in the test suite**

Run: `grep -rn "KeyPressed\|Ctrl '\|Character\|ShiftRight\|AltLeft\|CtrlDigit\|CtrlPageDown" tests/Fedit.Tests/`
Expected: a list of every test literal that must be migrated to `Chord` in Task 5. Capture it; this is the migration worklist.

No commit — this task only records the baseline.

---

## Task 2: Add the `Keys.fs` key model

**Files:**

- Create: `src/Fedit/Keys.fs`
- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Create `src/Fedit/Keys.fs`**

```fsharp
namespace Fedit

type Modifier =
    | Ctrl
    | Alt
    | Shift
    | Super

type NamedKey =
    | Enter
    | Escape
    | Tab
    | Backspace
    | Delete
    | Left
    | Right
    | Up
    | Down
    | Home
    | End
    | PageUp
    | PageDown
    | Space

type Key =
    | Char of char // layout-dependent produced character (the default)
    | Named of NamedKey // structural keys, layout-independent
    | Fn of int // F1..F24

/// A single key event: a set of modifiers plus the key. Structural
/// equality makes it usable as a Map key and in List.tryFind.
type Chord = { Mods: Set<Modifier>; Key: Key }

/// A sequence of one or more chords (length > 1 == a multi-key sequence).
type KeyStroke = Chord list

[<RequireQualifiedAccess>]
module Chord =
    /// Build a chord from a modifier list and a key.
    let make (mods: Modifier list) (key: Key) : Chord = { Mods = Set.ofList mods; Key = key }

    let private modToken =
        function
        | Ctrl -> "ctrl"
        | Alt -> "alt"
        | Shift -> "shift"
        | Super -> "super"

    let private namedToken =
        function
        | Enter -> "enter"
        | Escape -> "esc"
        | Tab -> "tab"
        | Backspace -> "backspace"
        | Delete -> "delete"
        | Left -> "left"
        | Right -> "right"
        | Up -> "up"
        | Down -> "down"
        | Home -> "home"
        | End -> "end"
        | PageUp -> "pageup"
        | PageDown -> "pagedown"
        | Space -> "space"

    let private keyToken =
        function
        | Char c -> string c
        | Named n -> namedToken n
        | Fn n -> $"f{n}"

    /// Render a chord to a display string, e.g. `ctrl+shift+p`,
    /// `alt+left`, `f5`. Modifiers in canonical order (ctrl, alt, shift,
    /// super). Used by the status-bar pending indicator now, and matches
    /// the Phase-3 keybinds-file grammar.
    let render (chord: Chord) : string =
        let order = [ Ctrl; Alt; Shift; Super ]
        let mods = order |> List.filter chord.Mods.Contains |> List.map modToken
        String.concat "+" (mods @ [ keyToken chord.Key ])

    /// Render a whole stroke (sequence) space-separated, e.g. `ctrl+k ctrl+c`.
    let renderStroke (stroke: KeyStroke) : string =
        stroke |> List.map render |> String.concat " "

    /// Bridge an editor chord to the frozen plugin-API KeyChord (apiVersion
    /// "1"). Total: returns None for anything the v1 KeyChord cannot name
    /// (Super, Named keys, sequences are handled by the caller, Fn beyond
    /// the v1 range, etc.). Replaces the old private `toKeyChord` in
    /// Editor.fs, which handled only `Ctrl c`.
    let toKeyChord (chord: Chord) : Fedit.PluginApi.KeyChord option =
        let mods = chord.Mods

        match chord.Key with
        | Char c when mods = Set.ofList [ Ctrl ] -> Some(Fedit.PluginApi.KeyChord.Ctrl c)
        | Char c when mods = Set.ofList [ Ctrl; Shift ] -> Some(Fedit.PluginApi.KeyChord.CtrlShift c)
        | Char c when mods = Set.ofList [ Alt ] -> Some(Fedit.PluginApi.KeyChord.Alt c)
        | Char c when mods.IsEmpty -> Some(Fedit.PluginApi.KeyChord.Char c)
        | Fn n when mods.IsEmpty -> Some(Fedit.PluginApi.KeyChord.F n)
        | _ -> None
```

> **Judgment call — `Chord.toKeyChord` mapping.** The plugin `KeyChord` DU is `Char | Ctrl | Alt | CtrlShift | F of int` (confirm the exact case set against `src/Fedit.PluginApi/`). Spec §6.7.3 says Phase 2 makes the dormant `CtrlShift`/`Alt`/`F` variants reachable "for the first time." This is a **behavior change for plugins**: a plugin that registered `Alt 'x'`/`CtrlShift 'p'`/`F 5` "for later" will start firing. It is in scope per the spec and must be called out in the commit body. Mapping bare `Char c` here means a plugin could grab a plain letter — but `runEditor`'s plugin pre-check (Task 5) only consults `toKeyChord` for non-text chords, preserving today's "plain `Char` is text, not a plugin trigger" rule. Verify the exact `KeyChord` cases before finalizing the `match`.

- [ ] **Step 2: Add the sequence-engine pure helper to `Keys.fs`**

Append to `Keys.fs` (still in the `Chord` module, or a sibling `Sequence` module):

```fsharp
[<RequireQualifiedAccess>]
module Sequence =
    /// Outcome of feeding one chord to the sequence engine, given the
    /// chord prefixes currently bound as sequence-prefixes.
    /// `prefixes` is the set of strokes that are *proper prefixes* of some
    /// bound multi-key stroke (Phase 2: always empty on the live path —
    /// no Keymap yet — so `step` always returns `Fire`; populated only by
    /// the unit tests until Phase 3 supplies a real Keymap).
    type Step =
        /// The accumulated candidate is itself a (proper) prefix of a bound
        /// sequence — keep it pending and show it in the status bar.
        | Pending of KeyStroke
        /// No pending prefix extends — dispatch the candidate as a single
        /// stroke (the normal path).
        | Fire of KeyStroke
        /// A pending prefix existed but this chord did not extend it —
        /// the sequence failed; do NOT fall through to text insert.
        | Failed of KeyStroke

    /// Feed one chord. `pending` is the prefix accumulated so far (or []);
    /// `isPrefix stroke` is true when `stroke` is a proper prefix of some
    /// bound sequence. Pure and total.
    let step (isPrefix: KeyStroke -> bool) (pending: KeyStroke) (chord: Chord) : Step =
        let candidate = pending @ [ chord ]

        if isPrefix candidate then Pending candidate
        elif List.isEmpty pending then Fire candidate
        else Failed candidate
```

> **Judgment call — engine shape.** The spec §6.3 describes the engine matching against the live `Keymap`, which does not exist until Phase 3. To make the engine real, tested, and forward-compatible now, this plan factors it into a pure `Sequence.step` that takes an explicit `isPrefix` predicate. Phase 3 supplies `isPrefix = fun s -> Keymap.isSequencePrefix s model.Keymap`; Phase 2 supplies `fun _ -> false` on the live path (so `step` always `Fire`s — dormant, exactly as scoped) and arbitrary predicates in unit tests. The `(KeyStroke * deadlineTick)` storage and timeout live in `Model`/`Runtime` (Tasks 3, 6).

- [ ] **Step 3: Register the file in `Fedit.fsproj`**

In `src/Fedit/Fedit.fsproj`, add immediately after `<Compile Include="Primitives.fs" />`:

```xml
    <Compile Include="Primitives.fs" />
    <Compile Include="Keys.fs" />
```

- [ ] **Step 4: Build**

Run: `just build`
Expected: FAIL at this point is acceptable only if it is the **expected** clash — `Keys.fs` introduces case names (`Enter`, `Escape`, `Tab`, `Left`, …) that collide with `Primitives.KeyInput`'s cases and `Char` collides with `Commands`/etc. This is fine because `KeyInput` is deleted in Task 7 and `Keys` types are `[<RequireQualifiedAccess>]`-free DUs that callers will reference via the `Chord`/`Named`/`Char`-qualified forms. **Decision:** keep `KeyInput` alive through Tasks 3–6 to avoid a giant single commit, and resolve name clashes by qualifying at use sites (`NamedKey.Enter`, `Key.Char`). If the collision makes incremental migration impractical, fold Tasks 2–7 into one branch and land them together — document whichever path you take in the commit body.

> **Judgment call — `Char`/`Enter`/`Tab` name collisions.** `Primitives.KeyInput` already owns `Character`, `Enter`, `Tab`, `Left`, etc.; `Keys.Key`/`NamedKey` reuse `Char`/`Enter`/`Tab`/`Left`. F# resolves the most-recently-declared case for an unqualified name, so during the overlap window (`KeyInput` not yet deleted) the dispatch sites must qualify (`NamedKey.Enter`, `Key.Char`). Cleanest path: delete `KeyInput` in the **same** commit that rewrites `Input.tryMap` + the dispatch sites (merge Tasks 3–7 into one atomic change on a branch). The task split below is presented for review clarity; the implementer may collapse it. State the chosen granularity in the commit message.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Keys.fs src/Fedit/Fedit.fsproj
git commit -m "feat(keys): add Chord/Key model, render, and plugin KeyChord bridge"
```

---

## Task 3: Change `Msg.KeyPressed` to carry a `Chord`; add `Model.PendingPrefix`

**Files:**

- Modify: `src/Fedit/Model.fs`

- [ ] **Step 1: Change the `Msg` payload**

In `src/Fedit/Model.fs`, change:

```fsharp
type Msg =
    | KeyPressed of KeyInput
```

to:

```fsharp
type Msg =
    | KeyPressed of Chord
```

Leave `MouseScrolled of int` and every async `Msg` exactly as-is. The doc comment on `MouseScrolled` ("an ambient input event like `Resize` … stays outside the keybinding / `Action` layer") is still accurate — keep it.

- [ ] **Step 2: Add the pending-prefix field to `Model`**

Add to the `Model` record (near `QuitArmed`/`ShouldQuit`):

```fsharp
        /// In-flight multi-key sequence: the chords accumulated so far and
        /// the tick (UTC ms) after which the engine abandons the sequence.
        /// `None` when no sequence is pending. Rendered in the status bar.
        /// Phase 2: only ever set by the (currently dormant) sequence engine;
        /// no built-in sequence is bound until Phase 3, so in practice this
        /// stays `None` on the shipped key set.
        PendingPrefix: (Chord list * int64) option
```

- [ ] **Step 3: Default `PendingPrefix = None` wherever a `Model` is constructed**

Run: `grep -n "QuitArmed = " src/Fedit/Editor.fs`
Expected: find `Editor.init` (and any test model builder) and add `PendingPrefix = None` to the record literal.

> **Judgment call — deadline type.** Spec §6.3 says "now + timeout" and "`deadlineTick`". The runtime tick is not a counter; the simplest monotonic source already used in `Runtime.fs` is `DateTime.UtcNow`. This plan stores an absolute deadline as `int64` ms (`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000`). Default timeout 1000ms per spec; not config-surfaced in Phase 2 (the spec says "configurable later").

- [ ] **Step 4: Build (expect the same overlap clashes as Task 2)**

Run: `just build`
Expected: compile errors only at the `KeyInput`-consuming sites in `Editor.fs`/`Input.fs`/`Runtime.fs` (fixed in Tasks 4–6). No new errors in `Model.fs` itself.

- [ ] **Step 5: Commit (or fold into the atomic branch per Task 2 Step 4)**

```bash
git add src/Fedit/Model.fs
git commit -m "feat(model): KeyPressed carries a Chord; add PendingPrefix field"
```

---

## Task 4: Rewrite `Input.tryMap` to emit `Chord`

**Files:**

- Modify: `src/Fedit/Input.fs`

- [ ] **Step 1: Rewrite `tryMap`**

Replace the body of `tryMap` (`Input.fs:9-72`) so it collects modifiers into a `Set<Modifier>` and normalizes per spec §6.1. `parseSgrMouse` (`Input.fs:74-98`) stays **byte-for-byte unchanged**.

```fsharp
    let tryMap (keyInfo: ConsoleKeyInfo) : Chord option =
        let hasAlt = hasModifier ConsoleModifiers.Alt keyInfo
        let hasCtrl = hasModifier ConsoleModifiers.Control keyInfo
        let hasShift = hasModifier ConsoleModifiers.Shift keyInfo

        // Structural keys map to Named regardless of modifiers; the
        // modifier set is carried verbatim (Shift is NOT dropped under
        // Ctrl — fixes wip audit #5).
        let named =
            match keyInfo.Key with
            | ConsoleKey.Enter -> Some Enter
            | ConsoleKey.Escape -> Some Escape
            | ConsoleKey.Backspace -> Some Backspace
            | ConsoleKey.Delete -> Some Delete
            | ConsoleKey.Tab -> Some Tab
            | ConsoleKey.LeftArrow -> Some Left
            | ConsoleKey.RightArrow -> Some Right
            | ConsoleKey.UpArrow -> Some Up
            | ConsoleKey.DownArrow -> Some Down
            | ConsoleKey.Home -> Some Home
            | ConsoleKey.End -> Some End
            | ConsoleKey.PageUp -> Some PageUp
            | ConsoleKey.PageDown -> Some PageDown
            | ConsoleKey.Spacebar -> Some Space
            | _ -> None

        let mods =
            [ if hasCtrl then Ctrl
              if hasAlt then Alt
              if hasShift then Shift ]
            |> Set.ofList

        // Function keys F1..F24 are a contiguous ConsoleKey range.
        let fnKey =
            if keyInfo.Key >= ConsoleKey.F1 && keyInfo.Key <= ConsoleKey.F24 then
                Some(int keyInfo.Key - int ConsoleKey.F1 + 1)
            else
                None

        match named, fnKey with
        | Some n, _ ->
            // Structural key: keep Shift in the modifier set (so Shift+Tab,
            // Ctrl+Left, etc. are all expressible).
            Some { Mods = mods; Key = Named n }
        | None, Some n -> Some { Mods = mods; Key = Fn n }
        | None, None ->
            if hasCtrl || hasAlt then
                // Ctrl/Alt + letter: lowercase the letter, keep Shift in
                // Mods so Ctrl+Shift+P is distinct from Ctrl+P. Use the
                // ConsoleKey letter (KeyChar is often NUL/control under Ctrl).
                let baseChar =
                    if keyInfo.Key >= ConsoleKey.A && keyInfo.Key <= ConsoleKey.Z then
                        Some(char (int 'a' + (int keyInfo.Key - int ConsoleKey.A)))
                    elif keyInfo.Key >= ConsoleKey.D0 && keyInfo.Key <= ConsoleKey.D9 then
                        Some(char (int '0' + (int keyInfo.Key - int ConsoleKey.D0)))
                    elif not (Char.IsControl keyInfo.KeyChar) then
                        Some(Char.ToLowerInvariant keyInfo.KeyChar)
                    else
                        None

                baseChar |> Option.map (fun c -> { Mods = mods; Key = Char c })
            else
                // Bare printable key: Shift lives in the character itself
                // (A vs a), NOT in Mods. This is the text fast-path.
                if Char.IsControl keyInfo.KeyChar then
                    None
                else
                    Some { Mods = Set.empty; Key = Char keyInfo.KeyChar }
```

Parity notes (do not skip):

- **`Ctrl+O` (wip audit #4)** is now decoded for free — any `Ctrl`+letter produces a chord, so the previously-dead `Ctrl+O` handler in `update` becomes reachable. Wiring it is Task 5.
- **Ctrl+←/→ (wip audit #8)** now decode to `{ Mods = {Ctrl}; Key = Named Left/Right }`. The word-motion gap is closed in Task 5 by binding those chords to `MoveWordLeft`/`MoveWordRight` alongside the existing `Alt+←/→`.
- **Ctrl+Shift+letter** now decodes (Shift no longer dropped under Ctrl), making `CtrlShift` plugin chords reachable (spec §6.7.3).
- **F-keys** now decode to `Fn n`.
- `CtrlDigit n` (buffer jump) becomes `{ Mods = {Ctrl}; Key = Char '1'..'9' }` — handled in Task 5. The macOS Terminal.app digit caveat comment moves here.

- [ ] **Step 2: Build (Input.fs must compile against the new return type)**

Run: `just build`
Expected: `Input.fs` compiles; remaining errors are at the `Editor.fs`/`Runtime.fs` consumers (Tasks 5, 6).

- [ ] **Step 3: Commit (or fold)**

```bash
git add src/Fedit/Input.fs
git commit -m "feat(input): decode keys to Chord (Ctrl+Shift, F-keys, Ctrl+arrows, no dropped Shift)"
```

---

## Task 5: Convert the three dispatch sites in `Editor.fs` to match `Chord`

Rewrite the global `Ctrl` handler in `update`, `runEditor`, and `runSidebar` to pattern-match `Chord` records instead of `KeyInput` cases. The `runAction` arm bodies are **untouched** (Phase 1 already lifted them). Replace the private `toKeyChord` with `Chord.toKeyChord`. Wire the sequence engine pass and the now-reachable `Ctrl+O`/`Ctrl+←/→`.

**Files:**

- Modify: `src/Fedit/Editor.fs`

- [ ] **Step 1: Replace `toKeyChord` with `Chord.toKeyChord`**

Delete the private `toKeyChord` (`Editor.fs:1006-1012`). In `runEditor`'s plugin pre-check (`Editor.fs:1021`), call `Chord.toKeyChord key`. Everything else in the plugin-dispatch block is unchanged. (Spec §6.7.3: this makes `Alt`/`CtrlShift`/`F` plugin bindings fire — flag in the commit body.)

- [ ] **Step 2: Define chord pattern helpers near the top of the dispatch region**

To keep the matches readable, add active patterns or `let`-bound chord literals just above the global handler:

```fsharp
    // Chord literals for the hardcoded default bindings. (Phase 3 replaces
    // these with the keymap; until then the dispatch is hardcoded.)
    let private cc c = { Mods = Set.ofList [ Ctrl ]; Key = Char c }       // ctrl+<char>
    let private nk n = { Mods = Set.empty; Key = Named n }                // bare named key
    let private snk n = { Mods = Set.ofList [ Shift ]; Key = Named n }    // shift+<named>
    let private cnk n = { Mods = Set.ofList [ Ctrl ]; Key = Named n }     // ctrl+<named>
    let private ank n = { Mods = Set.ofList [ Alt ]; Key = Named n }      // alt+<named>
```

> **Judgment call — match style.** F# cannot pattern-match a record against a `let`-bound value directly; use `when` guards (`| c when c = cc 's' -> …`) or `[<return: Struct>]` active patterns. This plan uses `when c = …` guards for clarity. The implementer may prefer active patterns (`(|Ctrl|_|)`, `(|Named|_|)`) — either is fine; pick one and be consistent.

- [ ] **Step 3: Rewrite the global `Ctrl` handler in `update`**

Replace the `| KeyPressed key ->` branch (`Editor.fs:1400-1463`). The `Ctrl+Q` two-stage logic stays bespoke; the `{ model with QuitArmed = false }` preamble stays. Sequence-engine integration wraps the chord dispatch (Step 5). Convert each arm:

```fsharp
        | KeyPressed chord ->
            let model =
                if chord = cc 'q' then model
                else { model with QuitArmed = false }

            // Sequence engine (Phase 2: dormant — isPrefix is always false
            // because there is no Keymap yet, so `step` always Fires).
            let isPrefix (_: KeyStroke) = false
            let pending = model.PendingPrefix |> Option.map fst |> Option.defaultValue []

            match Sequence.step isPrefix pending chord with
            | Sequence.Pending candidate ->
                let deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000L
                { model with PendingPrefix = Some(candidate, deadline) }, []
            | Sequence.Failed _ ->
                { model with
                    PendingPrefix = None
                    Notification = Some(Notification.warning $"No binding for {Chord.renderStroke (pending @ [ chord ])}") },
                []
            | Sequence.Fire _ ->
                let model = { model with PendingPrefix = None }

                match chord with
                | c when c = cc 'q' -> (* unchanged two-stage Ctrl+Q body *) …
                | c when c = cc 'p' -> runAction OpenPalette { model with Notification = None }
                | c when c = cc 'o' -> runAction OpenFilePicker { model with Notification = None }  // wip #4: now reachable
                | c when c = cc 'f' -> runAction OpenSearch { model with Notification = None }
                | c when c = cc 'b' ->
                    runAction
                        (When(SidebarVisible,
                              When(SidebarFocused, Chain [ HideSidebar; Action.FocusEditor ], FocusSidebar),
                              Chain [ RevealSidebar; FocusSidebar ]))
                        { model with Notification = None }
                | c when c = cc 'e' -> runAction Action.FocusEditor { model with Notification = None }
                | c when c = cc 's' -> runAction Save { model with Notification = None }
                | c when c = cc 'r' -> runAction Action.ReloadWorkspace { model with Notification = None }
                | c when c = cc 'z' -> runAction Undo { model with Notification = None }
                | c when c = cc 'y' -> runAction Redo { model with Notification = None }
                | c when c = cc 'a' -> runAction SelectAll { model with Notification = None }
                | c when c = cc 'c' -> runAction Copy { model with Notification = None }
                | c when c = cc 'x' -> runAction Cut { model with Notification = None }
                | c when c = cc 'v' -> runAction Paste { model with Notification = None }
                | c when c = cnk PageDown -> runAction Action.NextBuffer { model with Notification = None }
                | c when c = cnk PageUp -> runAction PrevBuffer { model with Notification = None }
                | { Mods = m; Key = Char d } when m = Set.ofList [ Ctrl ] && d >= '1' && d <= '9' ->
                    runAction (JumpToBuffer(int d - int '0')) { model with Notification = None }
                | _ ->
                    match model.Focus with
                    | Sidebar -> runSidebar chord { model with Notification = None }
                    | Editor -> runEditor chord { model with Notification = None }
                    | Prompt -> runPrompt chord { model with Notification = None }
```

> **Judgment call — `Escape` clears pending.** Spec §6.3: "`Escape` always clears a pending prefix." With `isPrefix` always false in Phase 2, `PendingPrefix` is never set on the live path, so this is a no-op today. Still, implement it: in the `Fire` branch the `{ model with PendingPrefix = None }` reset already covers it. No special Escape arm needed in Phase 2 (the engine never strands a prefix); add the explicit Escape-clears behavior in Phase 3 when sequences are real. Note this in the commit body so the Phase-3 author knows.

- [ ] **Step 4: Rewrite `runEditor`'s key match**

Convert the `match key with` block (`Editor.fs:1048-1078`) to `Chord` patterns. The plugin pre-check (now `Chord.toKeyChord chord`), `hasSelection`, and `editTransform` are unchanged. Add **Ctrl+←/→ → word motion** (wip #8):

```fsharp
            match chord with
            // text fast-path — bare printable char with no Ctrl/Alt
            | { Mods = m; Key = Char value } when m.IsEmpty ->
                updateActiveBuffer (editTransform (Buffer.insertText (string value)) >> Buffer.clearSelection) model, []
            | { Mods = m; Key = Named NamedKey.Enter } when m.IsEmpty ->
                updateActiveBuffer (editTransform Buffer.insertNewline >> Buffer.clearSelection) model, []
            | { Key = Named NamedKey.Backspace } when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
            | { Mods = m; Key = Named NamedKey.Backspace } when m.IsEmpty -> updateActiveBuffer Buffer.backspace model, []
            | { Key = Named NamedKey.Delete } when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
            | { Mods = m; Key = Named NamedKey.Delete } when m.IsEmpty -> updateActiveBuffer Buffer.deleteForward model, []
            // motions / edits — delegated to the unified interpreter
            | c when c = nk Left -> runAction MoveLeft model
            | c when c = nk Right -> runAction MoveRight model
            | c when c = nk Up -> runAction MoveUp model
            | c when c = nk Down -> runAction MoveDown model
            | c when c = nk Home -> runAction MoveHome model
            | c when c = nk End -> runAction MoveEnd model
            | c when c = snk Left -> runAction ExtendLeft model
            | c when c = snk Right -> runAction ExtendRight model
            | c when c = snk Up -> runAction ExtendUp model
            | c when c = snk Down -> runAction ExtendDown model
            | c when c = snk Home -> runAction ExtendHome model
            | c when c = snk End -> runAction ExtendEnd model
            | c when c = nk PageUp -> runAction MovePageUp model
            | c when c = nk PageDown -> runAction MovePageDown model
            | c when c = nk Tab -> runAction Indent model
            | c when c = snk Tab -> runAction Unindent model
            | c when c = ank Left -> runAction MoveWordLeft model
            | c when c = ank Right -> runAction MoveWordRight model
            | c when c = cnk Left -> runAction MoveWordLeft model    // wip #8: Linux-style Ctrl+← word motion
            | c when c = cnk Right -> runAction MoveWordRight model  // wip #8: Ctrl+→
            | c when c = cnk Backspace -> runAction DeleteWordBack model
            | c when c = cnk Delete -> runAction DeleteWordForward model
            | _ -> model, []
```

> **Judgment call — word-motion gap fix (#8).** wip-keybinds §2.2 notes fedit uses `Alt+←/→` (Mac-style) and lacks the Linux `Ctrl+←/→` convention. Spec §9.2 lists "word-motion gaps (#8)" as in scope for Phase 2 but does not prescribe the exact binding. This plan adds `Ctrl+←/→` as **aliases** for `MoveWordLeft`/`MoveWordRight` (keeping the existing `Alt+←/→`), which is the minimal, additive interpretation. File-start/file-end motions (`Ctrl+Home`/`Ctrl+End`, also "—" in the matrix) are **not** added — they need new `Action` cases and `Buffer` support, which is feature work beyond "fix the gap." Flag this scoping decision for review.

- [ ] **Step 5: Rewrite `runSidebar`'s key match**

Convert `runSidebar` (`Editor.fs:982-1004`) to `Chord` patterns; the incremental-filter fast-path keys (`Character c`, `Backspace`) become bare-char / bare-`Backspace` chords:

```fsharp
    let private runSidebar chord model =
        match chord with
        | { Mods = m; Key = Char c } when m.IsEmpty ->
            { model with Workspace = Workspace.appendSearch c model.Workspace }, []
        | { Mods = m; Key = Named NamedKey.Backspace } when m.IsEmpty && model.Workspace.SearchBuffer.Length > 0 ->
            { model with Workspace = Workspace.backspaceSearch model.Workspace }, []
        | c when c = nk Up -> runAction SidebarUp model
        | c when c = nk Down -> runAction SidebarDown model
        | c when c = nk PageUp -> runAction SidebarPageUp model
        | c when c = nk PageDown -> runAction SidebarPageDown model
        | c when c = nk Home -> runAction SidebarTop model
        | c when c = nk End -> runAction SidebarBottom model
        | c when c = nk Left -> runAction SidebarCollapse model
        | c when c = nk Right -> runAction SidebarExpand model
        | c when c = nk Enter -> runAction SidebarActivate model
        | c when c = nk Escape -> runAction Action.FocusEditor model
        | _ -> model, []
```

(`runPrompt` also matches `KeyInput` today — it must be converted the same way. It is large; convert each arm to the equivalent `Chord` pattern, preserving behavior. This is mechanical but must be done or `runPrompt` won't compile against `Chord`.)

> **Judgment call — `runPrompt` conversion.** The plan's task list focuses on the three sites the prompt names (§9.2), but `runPrompt` also takes a `KeyInput` and must be migrated to `Chord` for the code to compile, even though prompt keys stay bespoke (spec §6.8 keeps prompt line-editing non-keymap-driven in v1). Convert it mechanically; do not change its behavior. Call this out — it is implied by the type change, not separately listed in §9.2.

- [ ] **Step 6: Build**

Run: `just build`
Expected: PASS once `KeyInput` is removed (Task 7) or fully qualified. If kept alive during overlap, expect ambiguity warnings on `Enter`/`Tab`/`Char`; resolve by qualifying (`NamedKey.Enter`, `Key.Char`).

- [ ] **Step 7: Commit (or fold)**

```bash
git add src/Fedit/Editor.fs
git commit -m "refactor(editor): match Chord at all dispatch sites; wire Ctrl+O, Ctrl+arrows, sequence engine"
```

---

## Task 6: Wire the runtime keyboard branch + pending-prefix timeout; add the status indicator

**Files:**

- Modify: `src/Fedit/Runtime.fs`
- Modify: `src/Fedit/Status.fs`

- [ ] **Step 1: Runtime keyboard branch**

In `Runtime.fs`, the mouse-drain branch (`Runtime.fs:463-498`, the `match mouseTicks with | Some ticks -> … MouseScrolled` arm) is **untouched**. Only the `| None ->` keyboard arm changes — `Input.tryMap keyInfo` now returns `Chord option`, so the existing code already works structurally:

```fsharp
                    | None ->
                        match Input.tryMap keyInfo with
                        | Some chord ->
                            model <- dispatch model (KeyPressed chord)
                            needsRender <- true
                        | None -> ()
```

Confirm no further change is needed here (the variable rename `key -> chord` is cosmetic).

- [ ] **Step 2: Pending-prefix timeout in the tick loop**

Add to the main loop, beside the `lastFsChange` timeout block (`Runtime.fs:443-448`):

```fsharp
                match model.PendingPrefix with
                | Some(_, deadline) when DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > deadline ->
                    model <- dispatch model SequenceTimedOut
                    needsRender <- true
                | _ -> ()
```

This requires a new `Msg.SequenceTimedOut` (add to `Model.fs`) whose `update` handler clears `PendingPrefix`:

```fsharp
        | SequenceTimedOut -> { model with PendingPrefix = None }, []
```

> **Judgment call — timeout as a `Msg`.** Spec §6.3 says the loop "clears it (and re-attempts the buffered first chord as a standalone if it resolves)." Phase 2 has no keymap, so the buffered first chord is never re-attempted (there are no sequences). This plan clears via a `SequenceTimedOut` `Msg` to keep the clearing in `update` (pure), consistent with MVU. The "re-attempt buffered chord" refinement is deferred to Phase 3 with the real keymap; note it for the Phase-3 author. Since `PendingPrefix` is always `None` on the shipped key set, this branch never fires in practice in Phase 2 — but it is wired and unit-testable.

- [ ] **Step 3: Status-bar pending indicator**

In `Status.fs`, add a `pending` token to `resolveToken` so the format string `[pending]` renders the in-flight prefix (empty when none):

```fsharp
        | "pending", _ ->
            match model.PendingPrefix with
            | Some(chords, _) -> Chord.renderStroke chords + " …"
            | None -> ""
```

> **Judgment call — surfacing the indicator.** Spec §6.3 wants the pending chords shown "in the status bar." Rather than hardcode it into the focus/mode label, this plan exposes it as a `[pending]` status token so it composes with the user's `StatusFormat`. The **default** `StatusFormat` (in `Config.defaults`) is not changed in Phase 2 (it would alter the shipped status line, and the indicator is dormant anyway). The Phase-3 author who ships real sequences should add `[pending]` to the default format. Alternatively, prepend the pending text to `[mode]` (`focusText`) so it shows without a format change — flagged as the alternative; pick one at review. This plan recommends the token (composable, no behavior change to the default bar).

- [ ] **Step 4: Build**

Run: `just build`
Expected: PASS.

- [ ] **Step 5: Commit (or fold)**

```bash
git add src/Fedit/Runtime.fs src/Fedit/Status.fs src/Fedit/Model.fs
git commit -m "feat(runtime): dispatch Chord keypresses; pending-prefix timeout + status indicator"
```

---

## Task 7: Remove `KeyInput`

Spec §2.4: `KeyInput` does not survive. Delete it once nothing references it.

**Files:**

- Modify: `src/Fedit/Primitives.fs`

- [ ] **Step 1: Confirm no remaining references**

Run: `grep -rn "KeyInput\|Character\b\|ShiftRight\|AltLeft\|CtrlDigit\|CtrlPageDown\|CtrlBackspace" src/Fedit/`
Expected: no hits in any `src/Fedit/*.fs` except `Primitives.fs` itself (and none in `Keys.fs`, which uses `Char`/`Named`). Any straggler is an unmigrated site — fix before deleting.

- [ ] **Step 2: Delete the `KeyInput` DU from `Primitives.fs`** (lines 23-55).

- [ ] **Step 3: Build + full test**

Run: `just check`
Expected: PASS (lint + build + test). This is the pre-commit gate.

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Primitives.fs
git commit -m "refactor(primitives): remove KeyInput, superseded by Chord"
```

---

## Task 8: Tests — tryMap table, sequence engine, parity, plugin bridge

Mirror spec §8: `tryMap` table tests, sequence-engine tests, defaults parity, `Chord.toKeyChord` round-trip.

**Files:**

- Create: `tests/Fedit.Tests/InputTests.fs` (register in the test fsproj `<Compile>`)
- Modify: `tests/Fedit.Tests/UpdateTests.fs`

- [ ] **Step 1: `tryMap` table tests (`InputTests.fs`)**

Construct `ConsoleKeyInfo(keyChar, key, shift, alt, control)` and assert the produced `Chord`. Cover:

- Bare `'a'` → `{ Mods = ∅; Key = Char 'a' }`; bare `'A'` (shift in char) → `{ Mods = ∅; Key = Char 'A' }`.
- `Ctrl+S` and `Ctrl+s` both → `{ Mods = {Ctrl}; Key = Char 's' }` (case fold).
- `Ctrl+Shift+P` → `{ Mods = {Ctrl; Shift}; Key = Char 'p' }` distinct from `Ctrl+P` (the audit-#5 fix).
- `F5` → `{ Mods = ∅; Key = Fn 5 }`; `Shift+F3` → `{ Mods = {Shift}; Key = Fn 3 }`.
- `Ctrl+Left` → `{ Mods = {Ctrl}; Key = Named Left }` (wip #8 decode).
- `Shift+Tab` → `{ Mods = {Shift}; Key = Named Tab }`.
- `Ctrl+O` decodes (wip #4) → `{ Mods = {Ctrl}; Key = Char 'o' }`.
- A lone modifier / control char → `None`.

- [ ] **Step 2: Migrate `UpdateTests.fs` literals to `Chord`**

Rewrite every `KeyPressed(Ctrl 'x')` → `KeyPressed { Mods = Set.ofList [ Ctrl ]; Key = Char 'x' }`, `KeyPressed Right` → `KeyPressed { Mods = Set.empty; Key = Named Right }`, `KeyPressed (Character c)` → `KeyPressed { Mods = Set.empty; Key = Char c }`, etc. (use the worklist from Task 1 Step 2). A small test-only `ck`/`nk` helper keeps these terse. **All Phase-1 assertions must still pass** — that is the parity proof.

- [ ] **Step 3: Sequence-engine tests (against `Sequence.step`)**

Pure-helper tests with a synthetic `isPrefix` (no live keymap needed):

- prefix → `Pending` (e.g. `isPrefix [ctrl+k] = true`, feed `ctrl+k` from empty → `Pending [ctrl+k]`).
- complete → `Fire` (feed `ctrl+c` while pending `[ctrl+k]` and `isPrefix [ctrl+k; ctrl+c] = false` → `Fire`).
- failed → `Failed` (pending `[ctrl+k]`, feed an unrelated chord that is no prefix → `Failed`, asserting the engine does NOT signal text insert).
- no pending + not-a-prefix → `Fire` (the normal single-stroke path).
- `update`-level: `SequenceTimedOut` clears `PendingPrefix`; a `Pending` keypress sets it with a future deadline.

- [ ] **Step 4: `Chord.toKeyChord` round-trip + plugin-binding-still-fires**

- `Chord.toKeyChord { Mods={Ctrl}; Key=Char 'c' }` = `Some (KeyChord.Ctrl 'c')`; `{Ctrl;Shift}+p` = `Some (CtrlShift 'p')`; `Alt+x` = `Some (Alt 'x')`; `F5` = `Some (F 5)`; `Super+x` / `Named` = `None`.
- Drive a model with a registered plugin `Ctrl`-chord binding through `Editor.update (KeyPressed (ctrl-chord))` and assert the plugin command dispatches (reuse the existing `PluginsTests.fs` wordcount fixture pattern).

- [ ] **Step 5: Run the full gate**

Run: `just check`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/Fedit.Tests/
git commit -m "test(keys): tryMap table, sequence engine, Chord parity, plugin bridge"
```

---

## Final verification

- [ ] **Step 1: Full gate**

Run: `just check`
Expected: PASS (fantomas lint clean, build clean, all tests green).

- [ ] **Step 2: Manual smoke**

Run: `just run .`
Confirm by hand: typing inserts (incl. capitals); arrows + `Shift`-arrows; `Alt+←/→` **and** `Ctrl+←/→` word-motion; `PgUp/PgDn`; `Tab`/`Shift+Tab` (un)indent; `Ctrl+S` save; `Ctrl+O` now opens the file picker (previously dead, wip #4); `Ctrl+P`/`Ctrl+F` open the prompt; `Ctrl+B` tri-state; `Ctrl+C/X/V`, `Ctrl+Z/Y`, `Ctrl+A`; sidebar arrows/Enter/Escape + incremental filter; **mouse-wheel scrolling still works** (the coexistence check). `Ctrl+Shift+P` does nothing yet (no binding) but is decoded — confirm it does not insert a stray character.

- [ ] **Step 3: Confirm the deltas vs spec held**

`Msg.MouseScrolled of int` unchanged; `Input.parseSgrMouse` + the Runtime mouse-drain branch unchanged; `Effect` DU unchanged; no `Keymap.fs`/`resolve`/user file; no built-in multi-key sequence bound. `git diff --stat` should touch only: `Keys.fs` (new), `Fedit.fsproj`, `Input.fs`, `Model.fs`, `Editor.fs`, `Status.fs`, `Runtime.fs`, `Primitives.fs`, and the two test files.

---

## Self-review checklist (done while authoring)

- **Spec coverage:** Implements spec §9.2 Phase 2 — `Keys.fs` (§4.1), `tryMap → Chord` with §6.1 normalization, `Msg.KeyPressed of Chord`, three dispatch sites converted, Ctrl+Shift/F-keys/Ctrl+← →, sequence engine + `PendingPrefix` + status indicator + timeout (§6.3), `Chord.toKeyChord` (§6.7.2) making dormant `KeyChord` variants reachable (§6.7.3), and `Ctrl+O` (#4) / word-motion (#8) fixes. `Keymap.fs`/`resolve`/user file (Phase 3) and macros (Phase 4) are out of scope.
- **Mouse-scroll coexistence:** `MouseScrolled`, `parseSgrMouse`, and the Runtime CSI-drain branch are explicitly untouched; only the keyboard branch yields a `Chord`. Verified in Task 6 Step 1 and Final Step 3.
- **Engine present, dormant:** the sequence engine is factored as a pure `Sequence.step` taking an explicit `isPrefix`; on the live path `isPrefix = false` so it always `Fire`s and `PendingPrefix` stays `None` until Phase 3 binds a sequence. Stated in Scope §2 and Task 5 Step 3.
- **No behavior change for existing bindings:** every Phase-1 `runAction` arm body is untouched; the migrated `UpdateTests.fs` assertions (Task 8 Step 2) are the parity net; `Ctrl+O`/`Ctrl+←/→` are additive (previously dead/absent).
- **Compile order:** `Keys.fs` after `Primitives.fs` (Task 2) — the `FS0225` gotcha; every consumer (`Model`, `Editor`, `Status`, `Input`, `Runtime`) sees `Chord`. `KeyInput` deleted last (Task 7).
- **Judgment calls surfaced:** `KeyChord` exact-case verification, name-collision/atomic-commit strategy, `int64` deadline, match-style (guards vs active patterns), word-motion scope (Ctrl+←/→ alias only, no file-start/end), `runPrompt` mechanical conversion, timeout-as-`Msg`, and `[pending]` status token vs `[mode]` prefix — all flagged inline for the review gate.
