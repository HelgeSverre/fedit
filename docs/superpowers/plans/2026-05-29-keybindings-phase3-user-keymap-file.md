# Keybindings Phase 3 — User Keymap File Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move fedit's keybindings out of hardcoded dispatch arms and into a data-driven keymap: compiled-in defaults authored as an internal F# DSL, overlaid by a user-editable `~/.config/fedit/keybinds` line file, resolved per-context at dispatch time. Adds `unbind`, multi-key-sequence resolution, explicit reload, plugin-command binding from the file, a precedence flip (user keymap wins over plugins), and a `:keybind` introspection verb — with the default keymap reproducing today's behavior exactly.

**Architecture:** A new pure `Keymap.fs` (`Context`/`Binding`/`Keymap` types, the `defaults` DSL, the line parser, `resolve`, and `Keymap.index`) compiles between `Actions.fs` and `Model.fs`. `KeymapIO.load` mirrors `ConfigIO.load` — always returns a working keymap plus an error list. Loading is an `Effect.LoadKeybinds` → `Msg.KeybindsLoaded of Keymap * string list`, run at startup, on `:keybind reload`, and after the file is saved through fedit; `runAction ReloadKeybinds` emits it. The dispatch sites in `Editor.fs` stop matching `Chord` literals and instead call `Keymap.resolve` (built on Phase 2's `Chord`/sequence engine), with the focus-specific fallthrough (§6.2 rule 5) preserved for unbound keys. Plugin lookup moves _after_ the keymap and routes through `Chord.toKeyChord`.

**Tech Stack:** F# (.NET 9 SDK pinned in `.dotnet`), xUnit + FsUnit + FsCheck. Build/test via `just` only (never bare `dotnet`). No new runtime NuGet dependency — the parser is `Split` + active patterns.

This plan implements **only Phase 3** of [`docs/superpowers/specs/2026-05-29-keybindings-spec.md`](../specs/2026-05-29-keybindings-spec.md) §9.3 and assumes **Phases 1 and 2 are complete**.

---

## Assumed-complete prerequisites (Phases 1 & 2)

This plan builds on the following, treated as already shipped:

- **Phase 1** (`Actions.fs`): `Action`/`Cond` DUs incl. `RunPlugin of source * name * arg`, `ReloadKeybinds`, `Chain`, `When`, `NoOp`; `Action.ofCommand : Command -> Action option`; `runAction`/`evalCond` in `Editor.fs`; the global `Ctrl` handler, `runEditor`, `runSidebar`, and unifiable `executeCommand` verbs all route through `runAction`. `runAction ReloadKeybinds` is currently a `model, []` no-op (Phase 1 placeholder) — **this plan wires it**.
- **Phase 2** (`Keys.fs`): `Modifier`/`Key`/`NamedKey`/`Chord`/`KeyStroke`; `Chord.parse : string -> Chord option`, `Chord.render : Chord -> string`, `Chord.toKeyChord : Chord -> Fedit.PluginApi.KeyChord option` (total; `None` for what v1 `KeyChord` can't name). `Input.tryMap : ConsoleKeyInfo -> Chord option`. `Msg.KeyPressed of Chord`. `Model.PendingPrefix : (Chord list * int) option` plus the sequence engine driving it. All dispatch sites already match `Chord`. The Runtime input loop yields `Chord` and has its SGR-mouse / `MouseScrolled` branch.

If any of these are missing when execution starts, **stop** and resolve the prerequisite plan first — Phase 3 cannot be built against `KeyInput`.

> **Do not disturb mouse-wheel scrolling.** `Msg.MouseScrolled of int` and the Runtime SGR-mouse branch are ambient input, _outside_ the keybinding layer (Model.fs comment on `MouseScrolled`). No task here touches them; the final-verification step asserts the mouse path is byte-unchanged.

---

## Scope & deviations / resolved gaps

### Resolved gap: the `RunPlugin` grammar (spec §6.7.5, flagged unresolved)

Spec §6.6's grammar is `action := kebab-name [":" arg]` — one colon-delimited arg — but `RunPlugin of source * name * arg` needs three fields. **This plan resolves it as follows** (the spec's leaning, made concrete):

```
action       := plain-action | plugin-action
plain-action := kebab-name [ ":" arg ]            # arg = everything after the first ':'
plugin-action := "run-plugin" ":" plugin-ref [ WS arg ]
plugin-ref   := source "/" name                   # source and name are '/'-split once
```

Concretely: the parser special-cases the reserved action name `run-plugin`. After the leading `run-plugin:`, the remainder is split **once on the first `/`** into `source` and a tail; the tail is split **once on the first run of whitespace** into `name` and `arg` (arg defaults to `""`). So:

```
editor  ctrl+k ctrl+w  = run-plugin:wordcount/wc
editor  ctrl+k ctrl+W  = run-plugin:wordcount/wc selection
```

parse to `RunPlugin("wordcount", "wc", "")` and `RunPlugin("wordcount", "wc", "selection")` respectively. This keeps the single-`:` grammar intact for every other action (the _first_ `:` still separates name from arg; `run-plugin` is the one name whose arg is itself structured `source/name [arg]`). Rationale and the exact split rules are pinned in parser tests (Task 3). No `KeyChord` / `apiVersion` change — this is purely host-side parsing.

Error cases: a `run-plugin:` with no `/` in the ref → `Result.Error "run-plugin needs <source>/<name>"`; empty source or name → same error. These surface as load errors (§7) and the line is dropped, like any other malformed line.

### Other deviations

1. **Prompt control keys stay bespoke** (spec §6.8). Only `Global` bindings reach the prompt; the prompt's line-edit keys are _not_ keymap-driven in v1. `runPrompt` is unchanged except that it is reached via the §6.2-rule-5 fallthrough.
2. **`Keymap.index` keys on `Action`** (spec §6.9). `Action` already derives structural equality (no functions inside it), so it is usable as a `Map` key — verified in Task 8. If a future `Action` case carries a closure this breaks; none do today.
3. **Phase-2 sequence engine is reused, not rebuilt.** This plan only changes _what_ the engine resolves against (the keymap, via `resolve`) — the pending-prefix state machine, timeout, and Escape-clears behavior land in Phase 2 and are left intact. The one addition is the load-time **prefix-conflict** detector (§6.2), which lives in `Keymap.fs`, not the engine.
4. **`Config` is untouched.** Keybinds are a sibling file (`~/.config/fedit/keybinds`), not a `config.json` block (spec §11.4).

---

## File structure

In `Fedit.fsproj` `<Compile Include>` order (load-bearing — CLAUDE.md `FS0225` gotcha; update the fsproj **and** commit each file):

| Position in compile order | File                             | Change        | Responsibility                                                                                                                                                                                                                                                                                     |
| ------------------------- | -------------------------------- | ------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| after `Actions.fs`        | `src/Fedit/Keymap.fs`            | **create**    | `Context`/`Binding`/`Keymap` types; the `defaults` DSL (today's bindings, verbatim parity); `parseLine`; `parse` (whole file); `resolve`; prefix-conflict check; `Keymap.index`; `KeymapIO.load`. Pure data + I/O module; no `Model` reference.                                                    |
| —                         | `src/Fedit/Fedit.fsproj`         | modify        | Add `<Compile Include="Keymap.fs" />` immediately after `Actions.fs` and before `Model.fs`.                                                                                                                                                                                                        |
| —                         | `src/Fedit/Model.fs`             | modify        | Add `Keymap: Keymap` field to `Model` (+ default = `Keymap.defaults`); add `Msg.KeybindsLoaded of Keymap * string list`; add `Effect.LoadKeybinds`.                                                                                                                                                |
| —                         | `src/Fedit/Editor.fs`            | modify        | Wire `Msg.KeybindsLoaded` in `update`; make `runAction ReloadKeybinds` emit `[ LoadKeybinds ]`; switch the global/`runEditor`/`runSidebar` dispatch from hardcoded `Chord` matching to `Keymap.resolve` + focus fallthrough; flip plugin lookup after the keymap and route via `Chord.toKeyChord`. |
| —                         | `src/Fedit/Commands.fs`          | modify        | Add the `keybind` verb spec (`Hidden = false`).                                                                                                                                                                                                                                                    |
| —                         | `src/Fedit/Runtime.fs`           | modify        | Handle `Effect.LoadKeybinds` (call `KeymapIO.load`, enqueue `KeybindsLoaded`); enqueue an initial `LoadKeybinds` at startup so the user file overlays defaults. Save-then-reload when the keybinds file is written through fedit.                                                                  |
| —                         | `README.md`                      | modify        | Add a "Keybindings" section (file location, grammar, reload, precedence).                                                                                                                                                                                                                          |
| —                         | `examples/keybinds` (or `docs/`) | **create**    | A commented default `keybinds` example file users can copy.                                                                                                                                                                                                                                        |
| —                         | `docs/plugins.md`                | modify        | Document the precedence flip (user keymap wins over plugin chords) + keymapping plugin commands via `run-plugin:`.                                                                                                                                                                                 |
| —                         | `CHANGELOG.md`                   | modify        | Note the deliberate plugin-precedence behavior change.                                                                                                                                                                                                                                             |
| —                         | `tests/Fedit.Tests/*`            | modify/create | Parser table tests, resolution tests, defaults-parity, plugin-mapping, KeymapIO.load.                                                                                                                                                                                                              |

---

## Task 1: Keymap types + the `defaults` DSL (parity)

Create the type layer and the compiled-in defaults that reproduce today's bindings **exactly**. No parser, no resolve yet — just the data and a build.

**Files:**

- Create: `src/Fedit/Keymap.fs`
- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Establish the green baseline**

Run: `just check`
Expected: PASS (Phases 1+2 land clean). If not, stop — fix the prerequisite first.

- [ ] **Step 2: Create `src/Fedit/Keymap.fs` with the types + DSL helpers + `defaults`**

```fsharp
namespace Fedit

/// Which focus a binding applies in. Closed enum (spec §4.3) — there is no
/// deeper tree, so resolution specificity is just "context-match beats Global".
type Context =
    | Global
    | Editor
    | Sidebar
    | Prompt

type Binding =
    { Stroke: KeyStroke // Chord list; length > 1 == a sequence
      Context: Context
      Action: Action option } // None == unbind

/// defaults @ user-delta. Order is load order: later wins (spec §6.2 rule 3).
type Keymap = Binding list

[<RequireQualifiedAccess>]
module Keymap =

    // ── DSL helpers (devs only; mirror the file 1:1, spec §6.6) ───────────
    let chord mods key : Chord = { Mods = Set.ofList mods; Key = key }
    let private (==>) (c: Chord) action : Binding =
        { Stroke = [ c ]; Context = Editor; Action = Some action }
    let private bindSeq (cs: Chord list) action : Binding =
        { Stroke = cs; Context = Editor; Action = Some action }
    let private inCtx ctx (b: Binding) = { b with Context = ctx }

    /// Compiled-in defaults. MUST reproduce today's dispatch exactly — every
    /// chord currently handled in the global Ctrl handler / runEditor /
    /// runSidebar gets one entry here. Guarded by the parity test (Task 7).
    let defaults: Keymap =
        [
          // ── global Ctrl chords (fire in every focus → Context = Global) ──
          (chord [ Ctrl ] (Char 's') ==> Save) |> inCtx Global
          (chord [ Ctrl ] (Char 'p') ==> OpenPalette) |> inCtx Global
          (chord [ Ctrl ] (Char 'o') ==> OpenFilePicker) |> inCtx Global
          (chord [ Ctrl ] (Char 'f') ==> OpenSearch) |> inCtx Global
          (chord [ Ctrl ] (Char 'e') ==> FocusEditor) |> inCtx Global
          (chord [ Ctrl ] (Char 'r') ==> ReloadWorkspace) |> inCtx Global
          (chord [ Ctrl ] (Char 'z') ==> Undo) |> inCtx Global
          (chord [ Ctrl ] (Char 'y') ==> Redo) |> inCtx Global
          (chord [ Ctrl ] (Char 'a') ==> SelectAll) |> inCtx Global
          (chord [ Ctrl ] (Char 'c') ==> Copy) |> inCtx Global
          (chord [ Ctrl ] (Char 'x') ==> Cut) |> inCtx Global
          (chord [ Ctrl ] (Char 'v') ==> Paste) |> inCtx Global
          // buffer switching (Phase-2 chord spellings for CtrlPageDown/Up/Digit)
          (chord [ Ctrl ] (Named PageDown) ==> NextBuffer) |> inCtx Global
          (chord [ Ctrl ] (Named PageUp) ==> PrevBuffer) |> inCtx Global
          // (one JumpToBuffer entry per digit 1..9 — see Step 3)

          // ── tri-state sidebar Ctrl+B (spec §6.5 per-context split, §11.1) ──
          //   editor/global view, mirrored under Prompt so it works there too:
          (chord [ Ctrl ] (Char 'b')
           ==> When(SidebarVisible, FocusSidebar, Chain [ RevealSidebar; FocusSidebar ]))
          |> inCtx Global
          //   when already in the sidebar: hide + return to editor:
          (chord [ Ctrl ] (Char 'b') ==> Chain [ HideSidebar; FocusEditor ]) |> inCtx Sidebar

          // ── editor motions / edits (Context = Editor) ──
          chord [] (Named Left) ==> MoveLeft
          chord [] (Named Right) ==> MoveRight
          chord [] (Named Up) ==> MoveUp
          chord [] (Named Down) ==> MoveDown
          chord [] (Named Home) ==> MoveHome
          chord [] (Named End) ==> MoveEnd
          chord [ Shift ] (Named Left) ==> ExtendLeft
          chord [ Shift ] (Named Right) ==> ExtendRight
          chord [ Shift ] (Named Up) ==> ExtendUp
          chord [ Shift ] (Named Down) ==> ExtendDown
          chord [ Shift ] (Named Home) ==> ExtendHome
          chord [ Shift ] (Named End) ==> ExtendEnd
          chord [] (Named PageUp) ==> MovePageUp
          chord [] (Named PageDown) ==> MovePageDown
          chord [] (Named Tab) ==> Indent
          chord [ Shift ] (Named Tab) ==> Unindent
          chord [ Alt ] (Named Left) ==> MoveWordLeft
          chord [ Alt ] (Named Right) ==> MoveWordRight
          chord [ Ctrl ] (Named Backspace) ==> DeleteWordBack
          chord [ Ctrl ] (Named Delete) ==> DeleteWordForward

          // ── sidebar navigation (Context = Sidebar) ──
          (chord [] (Named Up) ==> SidebarUp) |> inCtx Sidebar
          (chord [] (Named Down) ==> SidebarDown) |> inCtx Sidebar
          (chord [] (Named PageUp) ==> SidebarPageUp) |> inCtx Sidebar
          (chord [] (Named PageDown) ==> SidebarPageDown) |> inCtx Sidebar
          (chord [] (Named Home) ==> SidebarTop) |> inCtx Sidebar
          (chord [] (Named End) ==> SidebarBottom) |> inCtx Sidebar
          (chord [] (Named Left) ==> SidebarCollapse) |> inCtx Sidebar
          (chord [] (Named Right) ==> SidebarExpand) |> inCtx Sidebar
          (chord [] (Named Enter) ==> SidebarActivate) |> inCtx Sidebar
          (chord [] (Named Escape) ==> FocusEditor) |> inCtx Sidebar
        ]
```

- [ ] **Step 3: Generate the nine `JumpToBuffer` digit entries**

Append inside `defaults` (or as a `let`-bound list spliced in via `yield!`):

```fsharp
          yield! [ for n in 1..9 -> (chord [ Ctrl ] (Char (char (int '0' + n))) ==> JumpToBuffer n) |> inCtx Global ]
```

(Phase 2's decoder maps `Ctrl`+digit to `Chord {Ctrl; Char '1'..'9'}`; the
old `CtrlDigit` quirk comment now lives in `Input.tryMap`. Confirm the digit
chord spelling matches Phase 2's decoder before relying on this — adjust if
Phase 2 chose `Named` for the digit row.)

- [ ] **Step 4: Cross-check the default set against today's three dispatch sites**

Read `src/Fedit/Editor.fs` global `Ctrl` handler, `runEditor`, and
`runSidebar` (post-Phase-1, they map chords → `Action`). Every chord they
match must appear exactly once in `defaults` with the same `Action` and the
right `Context`. Make a checklist comment in the PR description. The text
fast-path (bare `Char`, `Enter`, `Backspace`, `Delete` insertion) is **not**
in the keymap — it is the §6.2-rule-5 fallthrough (Task 6).

- [ ] **Step 5: Register in `Fedit.fsproj`**

```xml
    <Compile Include="Actions.fs" />
    <Compile Include="Keymap.fs" />
    <Compile Include="Model.fs" />
```

- [ ] **Step 6: Build**

Run: `just build`
Expected: PASS (no `FS0225`, no unresolved `Action`/`Chord` cases).

- [ ] **Step 7: Commit**

```bash
git add src/Fedit/Keymap.fs src/Fedit/Fedit.fsproj
git commit -m "feat(keymap): add Context/Binding/Keymap types and compiled-in defaults"
```

---

## Task 2: `resolve` + conflict rules

Add the pure resolver and the prefix-conflict detector. No callers yet.

**Files:**

- Modify: `src/Fedit/Keymap.fs`
- Test: `tests/Fedit.Tests/KeymapTests.fs` (create)

- [ ] **Step 1: Implement `resolve` (spec §6.2)**

Add to the `Keymap` module:

```fsharp
    /// Resolve a full keystroke in a context (spec §6.2):
    ///   1. keep bindings whose Stroke equals the input
    ///   2. context-match beats Global (specificity)
    ///   3. within a tier, LAST match wins (load order; user delta is appended)
    ///   4. a matched Action = None (unbind) actively frees the stroke:
    ///      returns Unbound — caller must NOT fall back to Global
    ///   5. no match → NotBound — caller applies focus-specific fallthrough
    let resolve (ctx: Context) (stroke: KeyStroke) (keymap: Keymap) : Resolution =
        let matching = keymap |> List.filter (fun b -> b.Stroke = stroke)
        let inCtx = matching |> List.filter (fun b -> b.Context = ctx)
        let globals = matching |> List.filter (fun b -> b.Context = Global)
        // Specificity: prefer the active context tier; else fall to Global.
        let tier = if List.isEmpty inCtx then globals else inCtx
        match List.tryLast tier with
        | Some b ->
            match b.Action with
            | Some a -> Bound a
            | None -> Unbound // unbind suppresses Global fallback (rule 4)
        | None -> NotBound
```

with the result DU at the top of the module file:

```fsharp
/// Outcome of resolving a keystroke (spec §6.2). `Unbound` is distinct from
/// `NotBound`: the former means "explicitly freed, do nothing, do not fall
/// through"; the latter means "no binding, apply focus fallthrough".
type Resolution =
    | Bound of Action
    | Unbound
    | NotBound
```

> Subtlety pinned by tests: when the active context is `Editor`/`Sidebar`/`Prompt`,
> rule 2 says a same-context binding beats a `Global` one. But an **unbind in
> the active context** must suppress the `Global` fallback too — that is why
> `tier` picks the active-context list _if non-empty_ and we read its last
> entry even when that entry is `Unbound`. If the active context has no
> matching binding at all, we fall to the `Global` tier. Verify both branches
> in Task 2 Step 3.

- [ ] **Step 2: Implement the prefix-conflict detector (spec §6.2, §7)**

```fsharp
    /// A stroke that is a PROPER prefix of any bound sequence in the same
    /// context cannot also be a standalone binding there (avoids Ghostty-style
    /// silent shadowing). Returns the offending standalone bindings as errors;
    /// the longer (sequence) binding is kept by the caller.
    let prefixConflicts (keymap: Keymap) : (Binding * string) list =
        let isProperPrefix (short: KeyStroke) (long: KeyStroke) =
            short.Length < long.Length
            && long |> List.take short.Length = short
        [ for b in keymap do
              if b.Stroke.Length = 1 && b.Action.IsSome then
                  let shadowed =
                      keymap
                      |> List.exists (fun o ->
                          o.Context = b.Context && isProperPrefix b.Stroke o.Stroke)
                  if shadowed then
                      yield b, $"'{(b.Stroke |> List.map Chord.render |> String.concat \" \")}' is a prefix of a bound sequence in {b.Context}" ]
```

(Only standalone-vs-sequence conflicts matter; sequence-vs-longer-sequence
prefixing is the normal pending-prefix case the Phase-2 engine handles.
Generalize the `Stroke.Length = 1` guard to "any stroke that is a proper
prefix of a longer one" if the engine supports 3+ chord sequences — keep it
to length-1 vs length-2 if not, and note the limitation in a comment.)

- [ ] **Step 3: Create `tests/Fedit.Tests/KeymapTests.fs` — resolution tests**

Cover: context beats Global; load-order (last wins) within a tier; unbind in
context suppresses Global fallback (`Unbound`, not the Global action); unbind
of a non-existent stroke → `NotBound`; no match → `NotBound`; prefix-conflict
detection flags the standalone and keeps the sequence. Register the new test
file in `tests/Fedit.Tests/Fedit.Tests.fsproj` (`<Compile Include>`).

```fsharp
[<Fact>]
let ``context binding beats a global binding for the same stroke`` () =
    let km =
        [ { Stroke = [ Keymap.chord [ Ctrl ] (Char 'g'); ]; Context = Global; Action = Some Save }
          { Stroke = [ Keymap.chord [ Ctrl ] (Char 'g') ]; Context = Editor; Action = Some Undo } ]
    Keymap.resolve Editor [ Keymap.chord [ Ctrl ] (Char 'g') ] km |> should equal (Bound Undo)

[<Fact>]
let ``later binding wins within the same tier`` () =
    let s = [ Keymap.chord [ Ctrl ] (Char 'g') ]
    let km =
        [ { Stroke = s; Context = Editor; Action = Some Save }
          { Stroke = s; Context = Editor; Action = Some Undo } ]
    Keymap.resolve Editor s km |> should equal (Bound Undo)

[<Fact>]
let ``unbind in context suppresses the global fallback`` () =
    let s = [ Keymap.chord [ Ctrl ] (Char 'g') ]
    let km =
        [ { Stroke = s; Context = Global; Action = Some Save }
          { Stroke = s; Context = Editor; Action = None } ]
    Keymap.resolve Editor s km |> should equal Unbound
```

- [ ] **Step 4: Run + commit**

Run: `just test`
Expected: PASS.

```bash
git add src/Fedit/Keymap.fs tests/Fedit.Tests/KeymapTests.fs tests/Fedit.Tests/Fedit.Tests.fsproj
git commit -m "feat(keymap): resolve with specificity, load-order, unbind and prefix-conflict rules"
```

---

## Task 3: The line-format parser

Parse `~/.config/fedit/keybinds` lines into `Binding option` (`None` for
blank/comment). No new deps — `Split` + active patterns. Includes the resolved
`run-plugin:` grammar.

**Files:**

- Modify: `src/Fedit/Keymap.fs`
- Test: `tests/Fedit.Tests/KeymapTests.fs`

- [ ] **Step 1: Implement `parseLine` (spec §6.6 + the §6.7.5 resolution above)**

```fsharp
    // Active pattern: a context word, else default Editor.
    let private (|ContextWord|_|) (s: string) =
        match s.ToLowerInvariant() with
        | "global" -> Some Global
        | "editor" -> Some Editor
        | "sidebar" -> Some Sidebar
        | "prompt" -> Some Prompt
        | _ -> None

    /// Map a kebab-case action name (+ optional arg) to an Action.
    /// `run-plugin` is special-cased per the resolved grammar (see plan).
    let private parseAction (name: string) (arg: string) : Result<Action, string> =
        match name with
        | "save" -> Ok Save
        | "quit" -> Ok Quit
        | "command-palette" | "open-palette" -> Ok OpenPalette
        | "open-file" -> Ok OpenFilePicker
        | "search" -> Ok OpenSearch
        | "undo" -> Ok Undo
        | "redo" -> Ok Redo
        | "copy" -> Ok Copy
        | "cut" -> Ok Cut
        | "paste" -> Ok Paste
        | "select-all" -> Ok SelectAll
        | "move-left" -> Ok MoveLeft
        // … one arm per Action that should be nameable (see Step 2 table) …
        | "set-theme" when arg <> "" -> Ok (SetTheme arg)
        | "goto" -> // arg = "LINE" or "LINE:COL"
            match arg.Split(':') with
            | [| l |] -> match Int32.TryParse l with true, n -> Ok (Goto(n, None)) | _ -> Error "goto needs a line number"
            | [| l; c |] -> /* parse both */ failwith "see impl"
            | _ -> Error "goto: bad argument"
        | "reload-workspace" -> Ok ReloadWorkspace
        | "reload-keybinds" -> Ok ReloadKeybinds
        | "open-config" -> Ok OpenConfig
        | "toggle-sidebar" -> Ok ToggleSidebar
        | "focus-editor" -> Ok FocusEditor
        | "focus-sidebar" -> Ok FocusSidebar
        | "sidebar-activate" -> Ok SidebarActivate
        // … sidebar-* nav names …
        | "run-plugin" ->
            // arg, here, is the WHOLE remainder after "run-plugin:" because the
            // caller passes everything past the first ':' as `arg`. Split it:
            //   <source>/<name> [ws <plugin-arg>]
            let refAndArg = arg.TrimStart()
            match refAndArg.IndexOf('/') with
            | -1 -> Error "run-plugin needs <source>/<name>"
            | slash ->
                let source = refAndArg.Substring(0, slash)
                let rest = refAndArg.Substring(slash + 1)
                let name, pluginArg =
                    match rest.IndexOfAny([| ' '; '\t' |]) with
                    | -1 -> rest, ""
                    | ws -> rest.Substring(0, ws), rest.Substring(ws + 1).Trim()
                if source = "" || name = "" then Error "run-plugin needs <source>/<name>"
                else Ok (RunPlugin(source, name, pluginArg))
        | "record-macro" when arg.Length = 1 -> Ok (RecordMacro arg.[0])    // deferred; parses now
        | "replay-macro" -> /* "<reg>[:count]" */ failwith "see impl"        // deferred; parses now
        | "no-op" -> Ok NoOp
        | other -> Error $"unknown action '{other}'"

    /// Parse one line. Ok None = blank/comment. Ok (Some b) = a binding
    /// (b.Action = None means unbind). Error = malformed (skipped + reported).
    let parseLine (line: string) : Result<Binding option, string> =
        let trimmed = line.Trim()
        if trimmed = "" || trimmed.StartsWith "#" then Ok None
        else
            // Split once on '=' into the stroke part and the action part.
            match trimmed.IndexOf('=') with
            | -1 -> Error "missing '='"
            | eq ->
                let lhs = trimmed.Substring(0, eq).Trim()
                let rhs = trimmed.Substring(eq + 1).Trim()
                // lhs := [context] WS stroke ; stroke := chord ( WS chord )*
                let tokens = lhs.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                let ctx, chordTokens =
                    match tokens with
                    | (ContextWord c) :: rest when not (List.isEmpty rest) -> c, rest
                    | _ -> Editor, tokens   // default context (spec §6.6)
                if List.isEmpty chordTokens then Error "no stroke"
                else
                    let chords = chordTokens |> List.map Chord.parse
                    if chords |> List.exists Option.isNone then
                        Error "unparseable chord in stroke"
                    else
                        let stroke = chords |> List.map Option.get
                        if rhs = "" then
                            Ok (Some { Stroke = stroke; Context = ctx; Action = None }) // unbind
                        else
                            // action := name [":" arg]; first ':' splits name/arg.
                            let name, arg =
                                match rhs.IndexOf(':') with
                                | -1 -> rhs, ""
                                | colon -> rhs.Substring(0, colon), rhs.Substring(colon + 1)
                            parseAction (name.Trim()) arg
                            |> Result.map (fun a -> Some { Stroke = stroke; Context = ctx; Action = Some a })
```

(Fill in the elided arms; `failwith "see impl"` markers are for the
implementer, not to ship. `Chord.parse` is the Phase-2 token parser — it
handles `ctrl+shift+p`, `f6`, `enter`, aliases `cmd/super`, `opt/alt`.)

- [ ] **Step 2: Build the action-name table deliberately**

Author one `parseAction` arm per `Action` case that should be user-bindable.
Cross-reference `Commands.fs` verb names so the keymap names and the prompt
verbs agree where they overlap (e.g. file uses `command-palette`; reconcile
with `Action.ofCommand` so `:`-verbs and keymap names map to the same
`Action`). Deferred macro actions (`record-macro`, `replay-macro`) parse now
but `runAction` no-ops them (Phase-1 contract).

- [ ] **Step 3: Parser table tests (spec §8)**

Append to `KeymapTests.fs`. Cover every grammar form and each error class:

```fsharp
[<Theory>]
[<InlineData("editor  ctrl+s = save")>]
[<InlineData("ctrl+s = save")>]                         // default context = editor
[<InlineData("# a comment")>]
[<InlineData("")>]                                       // blank
[<InlineData("editor  ctrl+k ctrl+c = no-op")>]          // sequence
[<InlineData("editor  ctrl+x =")>]                       // unbind
[<InlineData("editor  f6 = set-theme:gruvbox")>]         // arg-taking
[<InlineData("editor  ctrl+k ctrl+w = run-plugin:wordcount/wc")>]
[<InlineData("editor  ctrl+k ctrl+W = run-plugin:wordcount/wc selection")>]
let ``parseLine accepts valid forms`` (line: string) =
    Keymap.parseLine line |> Result.isOk |> should equal true

[<Fact>]
let ``run-plugin parses source name and arg`` () =
    match Keymap.parseLine "editor  ctrl+j = run-plugin:wordcount/wc selection" with
    | Ok (Some b) -> b.Action |> should equal (Some (RunPlugin("wordcount", "wc", "selection")))
    | other -> failwithf "unexpected %A" other

[<Theory>]
[<InlineData("ctrl+s save")>]                            // no '='
[<InlineData("editor  boguskey = save")>]                // unparseable chord
[<InlineData("editor  ctrl+s = no-such-action")>]        // unknown action
[<InlineData("xyz  ctrl+s = save")>]                     // unknown context (parsed as chord, then fails)
[<InlineData("editor  ctrl+j = run-plugin:wordcount")>]  // run-plugin missing '/'
let ``parseLine rejects malformed forms`` (line: string) =
    Keymap.parseLine line |> Result.isError |> should equal true
```

(Pin the `run-plugin` source/name/arg split precisely — it is the resolved gap;
add a test that an embedded `/` in the plugin arg is preserved, e.g.
`run-plugin:fs/find a/b` → `RunPlugin("fs","find","a/b")`.)

- [ ] **Step 4: Run + commit**

Run: `just test`

```bash
git add src/Fedit/Keymap.fs tests/Fedit.Tests/KeymapTests.fs
git commit -m "feat(keymap): line-format parser with run-plugin source/name/arg grammar"
```

---

## Task 4: `KeymapIO.load` + `Keymap.index`

Mirror `ConfigIO.load`: always return a working keymap plus an error list.

**Files:**

- Modify: `src/Fedit/Keymap.fs`
- Test: `tests/Fedit.Tests/KeymapTests.fs`

- [ ] **Step 1: Implement `KeymapIO.load` (spec §6.6, §7)**

```fsharp
/// File location + load. Sibling of ConfigIO; named KeymapIO so it doesn't
/// collide with the Keymap type/module.
[<RequireQualifiedAccess>]
module KeymapIO =
    open System.IO

    let path () = Path.Combine(ConfigIO.directory (), "keybinds")

    /// Mirrors ConfigIO.load: read the file if present, parse each line,
    /// collect "keybinds:<n>: <reason>" errors, and return
    /// `defaults @ validUserBindings` plus the error list. ALWAYS returns a
    /// working keymap (defaults floor) so the editor boots on a broken file.
    let load () : Keymap * string list =
        try
            let p = path ()
            if not (File.Exists p) then
                Keymap.defaults, []   // missing file = defaults only, no error (§7)
            else
                let mutable bindings = []
                let mutable errors = []
                File.ReadAllLines p
                |> Array.iteri (fun i line ->
                    match Keymap.parseLine line with
                    | Ok None -> ()
                    | Ok (Some b) -> bindings <- b :: bindings
                    | Error reason -> errors <- $"keybinds:{i + 1}: {reason}" :: errors)
                let merged = Keymap.defaults @ List.rev bindings
                // prefix-conflict pass: drop the offending standalones, report
                let conflicts = Keymap.prefixConflicts merged
                let conflictErrs = conflicts |> List.map snd
                let cleaned = merged |> List.filter (fun b -> not (conflicts |> List.exists (fun (c, _) -> System.Object.ReferenceEquals(c, b))))
                cleaned, List.rev errors @ conflictErrs
        with ex ->
            Keymap.defaults, [ $"keybinds: {ex.Message}" ]
```

(Use a value-equality filter for the conflict drop rather than
`ReferenceEquals` if duplicate bindings can legitimately exist; pin the chosen
semantics in a test. Defaults are conflict-free by construction, so a conflict
can only come from the user delta — keep the longer/sequence binding.)

- [ ] **Step 2: Implement `Keymap.index` (spec §6.9)**

```fsharp
    /// keystroke ↔ action index, built at load. Used by the prompt to show
    /// bound keys and by `:keybind`. Keyed on Action (structural equality).
    let index (keymap: Keymap) : Map<Action, KeyStroke list> =
        keymap
        |> List.choose (fun b -> b.Action |> Option.map (fun a -> a, b.Stroke))
        |> List.fold (fun acc (a, s) ->
            let existing = acc |> Map.tryFind a |> Option.defaultValue []
            Map.add a (existing @ [ s ]) acc) Map.empty
```

- [ ] **Step 3: Tests**

`load` against a temp dir (set via the same mechanism `ConfigIO` tests use, or
inject the path) — or, simpler, test `Keymap.parse : string seq -> Keymap *
string list` extracted from `load` so the file read is the only untested line.
Prefer extracting a pure `parse` helper and testing it directly (defaults
overlay, error collection, prefix-conflict drop). Test `index` round-trips a
known default (`Save` → contains `[ctrl+s]`).

- [ ] **Step 4: Run + commit**

Run: `just test`

```bash
git add src/Fedit/Keymap.fs tests/Fedit.Tests/KeymapTests.fs
git commit -m "feat(keymap): KeymapIO.load mirroring ConfigIO and keystroke index"
```

---

## Task 5: Effect/Msg wiring (Model + Runtime)

Add the load effect/message and run it at startup, on reload, and after save.

**Files:**

- Modify: `src/Fedit/Model.fs`, `src/Fedit/Runtime.fs`, `src/Fedit/Editor.fs`

- [ ] **Step 1: Extend `Model`, `Msg`, `Effect` (Model.fs)**

```fsharp
// in Model:
        Keymap: Keymap
// PendingPrefix is already present from Phase 2.

// in Msg:
    | KeybindsLoaded of Keymap * string list

// in Effect:
    | LoadKeybinds
```

Set the `Model` default for `Keymap` to `Keymap.defaults` wherever the initial
model is built (so the editor is fully functional before the async load lands).

- [ ] **Step 2: Handle `KeybindsLoaded` in `update` (Editor.fs)**

In `Editor.update`, add an arm mirroring how theme-load errors surface as a
startup notification:

```fsharp
        | KeybindsLoaded(keymap, errors) ->
            let model = { model with Keymap = keymap }
            match errors with
            | [] -> model, []
            | _ -> { model with Notification = Some(Notification.warn (String.concat "; " errors)) }, []
```

(Match the existing theme-error notification severity/format — check what
`ConfigIO`/theme errors use today and reuse it.)

- [ ] **Step 3: Make `runAction ReloadKeybinds` emit the effect (Editor.fs)**

Phase 1 left it `model, []`. Change to:

```fsharp
        | ReloadKeybinds -> model, [ LoadKeybinds ]
```

- [ ] **Step 4: Handle `Effect.LoadKeybinds` in Runtime.fs**

In `startEffect`, mirror the `SaveConfig`/`ScanWorkspace` shape — load is fast
and synchronous, but keep it on the queue path for consistency:

```fsharp
            | LoadKeybinds ->
                Task.Run(fun () ->
                    let keymap, errors = KeymapIO.load ()
                    queue.Enqueue(KeybindsLoaded(keymap, errors)))
                |> ignore
```

- [ ] **Step 5: Enqueue an initial `LoadKeybinds` at startup**

In `Runtime.run`, alongside the initial `ScanWorkspace`/plugin scan enqueues,
enqueue `LoadKeybinds` once so the user file overlays `Keymap.defaults` on
boot. (The `Model` already carries `defaults`, so there is no flash of
unbound keys.)

- [ ] **Step 6: Reload after saving the keybinds file through fedit**

The spec requires a reload after the file is saved _through fedit_. Decide the
trigger: the cleanest is to detect, in the `BufferSaved` handler in
`Editor.update`, that the saved path equals `KeymapIO.path ()` and emit
`[ LoadKeybinds ]` (alongside whatever it already returns). Add that path
check. (`:keybind reload` is the explicit trigger; this is the implicit one.)

- [ ] **Step 7: Build + run + commit**

Run: `just check`

```bash
git add src/Fedit/Model.fs src/Fedit/Editor.fs src/Fedit/Runtime.fs
git commit -m "feat(keymap): load keybinds at startup, on reload, and after save"
```

---

## Task 6: Switch dispatch to `Keymap.resolve` (parity + focus fallthrough)

Replace the hardcoded `Chord`-matching dispatch with `resolve`, preserving the
text/insert fallthrough per focus (§6.2 rule 5). This is the behavior-critical
task — the parity test (Task 7) is the proof.

**Files:**

- Modify: `src/Fedit/Editor.fs`

- [ ] **Step 1: Add a single keymap-dispatch helper**

Add a helper (in `Editor`, in the `runAction` recursive group or just above the
`KeyPressed` handling) that, given the active `Context`, the candidate stroke,
and the model, consults the keymap and either runs the action or signals
fallthrough:

```fsharp
    // Returns Some (model', fx) if the keymap handled the stroke (incl. an
    // explicit unbind, which consumes the key and does nothing); None means
    // "no binding — caller applies the focus-specific fallthrough".
    let private dispatchViaKeymap (ctx: Context) (stroke: KeyStroke) (model: Model) =
        match Keymap.resolve ctx stroke model.Keymap with
        | Bound action -> Some(runAction action model)
        | Unbound -> Some(model, [])      // explicitly freed: consume, do nothing
        | NotBound -> None
```

The Phase-2 sequence engine builds `stroke` (the pending prefix + this chord)
and decides pending vs fire vs cancel. Where the engine currently "fires" a
completed stroke against the hardcoded table, point it at `dispatchViaKeymap`
instead, and have it resolve against **`Global` first in every focus**, then
the focus's own context — matching spec §6.2's "Global resolves in every
focus" rule. Concretely the fire path tries `dispatchViaKeymap Global stroke`,
then `dispatchViaKeymap <focusCtx> stroke`; the _first_ non-`None` wins. (Or
fold both into one `resolve` call if `resolve` already prefers the active
context over `Global` — it does, per Task 2. Simpler: map focus → `Context`
and call `resolve` once with that context; `resolve`'s specificity already
returns the context binding when present and the Global binding otherwise.)

> Recommended: **one `resolve` call** with the focus's `Context`. `resolve`
> already implements "context beats Global, else Global". This is simpler than
> two calls and is what Task 2's tests pin. Use the two-call form only if the
> engine needs to distinguish "Global handled it" from "focus handled it".

- [ ] **Step 2: Rewrite the global `Ctrl` handler / `KeyPressed` branch**

Remove the hardcoded `| Ctrl 'p' -> …` etc. arms added in Phase 1. The
`KeyPressed chord` branch now:

1. preserves the `QuitArmed`/`Notification = None` preamble (the two-stage
   `Ctrl+Q` arm — confirm whether it is keymap-driven or stays bespoke;
   recommend keeping the `QuitArmed` two-press logic bespoke since it owns
   that flag, and letting the keymap map the _first_ `Ctrl+Q` to a `Quit`
   action only if `QuitArmed` semantics allow — otherwise leave `Ctrl+Q`
   entirely out of `defaults` and keep it inline). **Document the choice.**
2. runs the Phase-2 sequence engine, which on a completed stroke calls
   `dispatchViaKeymap (contextOf model.Focus) stroke`.
3. on `None` (NotBound), applies the **focus-specific fallthrough**:
    - `Editor` → text fast-path (`runEditor`'s `Character`/`Enter`/`Backspace`/
      `Delete` insertion arms — now the _only_ thing left in `runEditor`).
    - `Sidebar` → incremental filter (`runSidebar`'s `Character`/`Backspace`).
    - `Prompt` → `runPrompt` (unchanged; only `Global` bindings reach it).

Define `let private contextOf = function | FocusTarget.Editor -> Editor | Sidebar -> Sidebar | Prompt -> Prompt` (mind the `Editor` name clash between the `Context` case and the module — qualify as needed).

- [ ] **Step 3: Reduce `runEditor` and `runSidebar` to their fallthrough cores**

`runEditor` keeps **only** the text fast-path (literal insertion) + the plugin
pre-check (which moves to Task 8). All motion/edit arms are gone — they are
now keymap entries dispatched via `resolve` → `runAction`. Same for
`runSidebar`: keep only the incremental-filter arms; navigation is keymap-driven.

- [ ] **Step 4: Run the full suite**

Run: `just test`
Expected: PASS. The Phase-1 characterization net (motions, edits, clipboard,
sidebar nav, tri-state `Ctrl+B`, buffer switch) must stay green — that is the
parity proof. If any fail, a `defaults` entry diverges from the old arm: fix
`defaults`, not the test.

- [ ] **Step 5: Commit**

```bash
git add src/Fedit/Editor.fs
git commit -m "refactor(editor): dispatch keystrokes through Keymap.resolve with focus fallthrough"
```

---

## Task 7: Defaults-parity test

Lock the "no behavior change" claim with a dedicated parity suite.

**Files:**

- Test: `tests/Fedit.Tests/KeymapTests.fs` (or `UpdateTests.fs`)

- [ ] **Step 1: Snapshot/assert the default chord set**

Add a test that, for each chord the editor handled before Phase 3, drives
`Editor.update (KeyPressed chord)` through the keymap path and asserts the same
`(Model, Effect list)` outcome as the documented old behavior. Reuse the
Phase-1 characterization helpers (`withText`, `activeBufferState`). Add an
explicit table test asserting `Keymap.resolve <ctx> [chord] Keymap.defaults`
returns the expected `Action` for every default (this guards the DSL itself,
independent of `update`).

```fsharp
[<Theory>]
[<InlineData("ctrl+s", "Save")>]   // … table covering every default …
let ``defaults resolve to the expected action`` (strokeText: string) (actionName: string) =
    // parse strokeText via Chord.parse, resolve in the right context, compare
    ()
```

- [ ] **Step 2: Tri-state sidebar regression**

Drive `Ctrl+B` through all three states (hidden→reveal+focus, visible→focus,
focused→hide+editor) and assert the `(SidebarVisible, Focus)` transitions match
today's (the Phase-1 test already exists — confirm it still passes against the
keymap-driven path; if it was deleted with the inline arms, re-add it).

- [ ] **Step 3: Run + commit**

Run: `just test`

```bash
git add tests/Fedit.Tests/KeymapTests.fs
git commit -m "test(keymap): defaults parity and tri-state sidebar regression"
```

---

## Task 8: Plugin precedence flip + `Chord.toKeyChord` lookup

Move plugin lookup _after_ the keymap and route it through `Chord.toKeyChord`.
Deliberate behavior change (spec §6.7.4, §11.2).

**Files:**

- Modify: `src/Fedit/Editor.fs`
- Test: `tests/Fedit.Tests/PluginsTests.fs` (or `KeymapTests.fs`)

- [ ] **Step 1: Remove the plugin pre-check from `runEditor`**

Today `runEditor` checks plugin keybindings _before_ the default editor
behavior (`Editor.fs` ~1014-1038, post-Phase-1 it maps the chord first). Delete
that pre-check from `runEditor`.

- [ ] **Step 2: Add a plugin fallthrough layer after the keymap**

In the `KeyPressed` fire path, the order becomes (spec §6.7.4):

```
Keymap.resolve (context beats Global)  →  plugin bindings  →  text fast-path
```

So when `dispatchViaKeymap` returns `None` (NotBound) **and** `Unbound` was not
returned, and the focus is `Editor`, try the plugin layer before the text
fast-path:

```fsharp
    let private dispatchViaPlugins (chord: Chord) (model: Model) =
        // plugins stay editor-focus only in v1 (spec §6.7.4)
        match Chord.toKeyChord chord with
        | None -> None
        | Some kc ->
            model.Plugins.Keybindings
            |> List.tryFind (fun (c, _) -> c = kc)
            |> Option.map snd
            |> Option.map (fun commandName ->
                match model.Plugins.Commands.TryFind commandName with
                | Some binding -> runAction (RunPlugin(binding.Source, commandName, "")) model
                | None ->
                    match Commands.parse commandName with
                    | Ready cmd -> executeCommand cmd model
                    | _ -> notify (Some(Notification.error $"Plugin binding refers to unknown command '{commandName}'.")) model, [])
```

(Plugin bindings are single chords only — `Chord.toKeyChord` returns `None` for
sequences, `Super`, and `Named` keys, so a pending multi-chord prefix never
reaches plugins.) Wire the editor-focus fallthrough as:
`dispatchViaKeymap` → (if NotBound) `dispatchViaPlugins` → (if None) text insert.
An `Unbound` from the keymap consumes the key and **must not** reach plugins
(the user explicitly freed it).

- [ ] **Step 3: Tests — precedence + mapping**

- A chord bound in the keymap (default or a fake user binding) and _also_ in a
  fake plugin registry resolves to the **keymap** action (precedence flip).
- A chord bound only in the plugin registry still fires the plugin command
  (fallthrough preserved).
- `Chord.toKeyChord` round-trips the expressible subset (`Ctrl c`, `CtrlShift`,
  `Alt`, `F n`) and returns `None` for `Super`/`Named`/sequences. (This may
  already be a Phase-2 test — extend, don't duplicate.)
- The end-to-end `PluginsTests.fs` `wordcount` test still passes.

- [ ] **Step 4: Run + commit**

Run: `just test`

```bash
git add src/Fedit/Editor.fs tests/Fedit.Tests/PluginsTests.fs
git commit -m "refactor(plugins): user keymap takes precedence over plugin chords"
```

---

## Task 9: The `:keybind` verb + prompt integration

Add introspection and surface bound keys in the prompt (spec §6.9).

**Files:**

- Modify: `src/Fedit/Commands.fs`, `src/Fedit/Editor.fs` (`executeCommand`), prompt/completion rendering

- [ ] **Step 1: Add the `keybind` command spec (Commands.fs)**

Add a `Command.Keybind of verb: string` case (or reuse a generic verb arg) and
a `Spec` mirroring the `plugin`/`syntax` verb-style entries:

```fsharp
          { Name = "keybind"
            Usage = "keybind [reload | <stroke>]"
            Summary = "List effective keybindings, reload the file, or show what a stroke is bound to."
            Hidden = false
            Constructor =
              fun argument -> Ready(Keybind(argument.Trim())) }
```

- [ ] **Step 2: Handle it in `executeCommand` (Editor.fs)**

```fsharp
        | Keybind arg ->
            match arg with
            | "" -> // list effective bindings: render Keymap.index model.Keymap into a buffer or notification
                …
            | "reload" -> runAction ReloadKeybinds model
            | strokeText -> // Chord.parse the stroke(s), resolve in each context, report
                …
```

- `:keybind` with no arg: list effective bindings. Decide presentation —
  cleanest is to open them in a scratch buffer (like `:config` opens a file)
  or a multi-line notification; reuse whatever `:plugin list` does for
  consistency. Build the listing from `Keymap.index model.Keymap` + `Chord.render`.
- `:keybind reload`: delegate to `runAction ReloadKeybinds` (Task 5 already
  wires the effect).
- `:keybind <stroke>`: parse the stroke, then for each `Context` show
  `Keymap.resolve ctx stroke model.Keymap`.

- [ ] **Step 3: Add `Action.ofCommand` mapping if needed**

If `:keybind reload` should share the executor, add `Command.Keybind "reload"
-> Some ReloadKeybinds` is awkward (the verb is overloaded); keep `Keybind` in
`executeCommand` and let only `reload` delegate to `runAction`. Document.

- [ ] **Step 4: Show bound keys in the command prompt**

In the prompt's completion rendering, look up each command's `Action` (via
`Action.ofCommand`) in `Keymap.index model.Keymap` and append the rendered
keystroke (e.g. `write            ctrl+s`). Keep it cheap — the index is built
once per keymap load; cache it on the model or compute lazily. Match the
existing completion-row layout; do not break `NO_COLOR`.

- [ ] **Step 5: Run + commit**

Run: `just check`

```bash
git add src/Fedit/Commands.fs src/Fedit/Editor.fs src/Fedit/Prompt.fs src/Fedit/View.fs
git commit -m "feat(keymap): add :keybind verb and show bound keys in the prompt"
```

---

## Task 10: Docs + default keybinds example + changelog

**Files:**

- Modify: `README.md`, `docs/plugins.md`, `CHANGELOG.md`
- Create: `examples/keybinds`

- [ ] **Step 1: README "Keybindings" section**

Document: file location (`~/.config/fedit/keybinds`), the grammar (context,
stroke, sequences, action names, `:` arg, unbind via empty RHS), the
`run-plugin:<source>/<name> [arg]` form, reload (`:keybind reload` + autosave
reload), and precedence (user keymap > plugins > text). Voice rules
(`brand/voice.md`): no emoji, no marketing adjectives, lead with the verb.

- [ ] **Step 2: Create `examples/keybinds`**

A commented, copy-pastable file showing each grammar form (a remap, a sequence,
an unbind, an arg-taking action, a `run-plugin` binding). Keep it minimal and
correct — it doubles as documentation.

- [ ] **Step 3: `docs/plugins.md` precedence note**

Document the deliberate flip: user keymap (defaults ⊕ file) now wins over a
plugin's `RegisterKeybinding`; a plugin can no longer shadow a chord the keymap
binds. Show binding a plugin command from the keymap file via `run-plugin:`.

- [ ] **Step 4: `CHANGELOG.md`**

Note the behavior change under the current phase: "Plugin keybindings no longer
shadow built-in or user keybindings — the user keymap takes precedence."
Lead with the verb; no emoji.

- [ ] **Step 5: Format + commit**

Run: `just format` then `just lint`

```bash
git add README.md docs/plugins.md CHANGELOG.md examples/keybinds
git commit -m "docs(keymap): document the keybinds file, reload, and plugin precedence flip"
```

---

## Final verification

- [ ] **Step 1: Full gate**

Run: `just check`
Expected: PASS (fantomas + prettier lint clean, build clean, all tests green).

- [ ] **Step 2: Manual smoke**

Run: `just run .`
Confirm by hand, against a real `~/.config/fedit/keybinds`:

- No file present → today's bindings work unchanged.
- Add `editor ctrl+s = no-op`, then `:keybind reload` → `Ctrl+S` stops saving.
- Add a sequence (`editor ctrl+k ctrl+s = save`) → status bar shows `ctrl+k …`
  pending; completing it saves; `Escape` cancels the prefix.
- Add an unbind (`global ctrl+r =`) → `Ctrl+R` no longer rescans (and does not
  fall through to text).
- Bind a plugin: `editor ctrl+k ctrl+w = run-plugin:wordcount/wc` fires it.
- A malformed line surfaces a startup/reload warning; the editor still boots.
- `:keybind`, `:keybind reload`, `:keybind ctrl+s` all behave.
- Mouse-wheel scrolling still works (unchanged).

- [ ] **Step 3: Confirm scope**

`git diff --stat` shows only: `Keymap.fs` (new), `Fedit.fsproj`, `Model.fs`,
`Editor.fs`, `Commands.fs`, `Runtime.fs`, prompt/view files, README, plugins
doc, changelog, `examples/keybinds`, and test files. `Config.fs`, `Keys.fs`,
`Input.fs`, the `MouseScrolled`/SGR-mouse path, and `PluginApi` are unchanged.

---

## Self-review checklist (done while authoring)

- **Spec coverage:** Implements spec §9.3 — `Keymap.fs` (types + defaults DSL +
  parser + `resolve` + `KeymapIO.load` + `index`), the `LoadKeybinds`/
  `KeybindsLoaded` wiring (startup + `:keybind reload` + post-save), dispatch
  switched to `resolve` with focus fallthrough (§6.2), plugin precedence flip +
  `Chord.toKeyChord` lookup (§6.7.4), the `run-plugin` grammar resolution
  (§6.7.5), the `:keybind` verb + prompt integration (§6.9), error handling
  (§7, always boots), and the testing matrix (§8). Macros (Phase 4) stay
  parse-able no-ops.
- **Resolved gap:** the `RunPlugin` grammar is defined concretely as
  `run-plugin:<source>/<name> [arg]` (split once on `/`, then once on
  whitespace; embedded `/` preserved in arg), pinned by parser tests. Called
  out in "Scope & deviations / resolved gaps."
- **Parity:** `defaults` reproduces every current chord with the right context;
  the Phase-1 characterization net + a dedicated defaults-parity test + the
  tri-state regression are the proof. Fixes go in `defaults`, never the tests.
- **No behavior change except the intended one:** the plugin-precedence flip is
  the one deliberate change, documented in `CHANGELOG.md` and `docs/plugins.md`.
- **Always boots:** `KeymapIO.load` floors on `Keymap.defaults`; the `Model`
  carries `defaults` before the async load lands; errors surface as a
  notification, exactly like theme/config errors.
- **Compile order:** `Keymap.fs` added to `Fedit.fsproj` after `Actions.fs`,
  before `Model.fs` (CLAUDE.md `FS0225` gotcha); every new test file registered
  in the test fsproj.
- **No new deps:** parser is `Split` + active patterns; no FSI/`.fsx`.
- **`Action` as a `Map` key:** `Keymap.index` relies on `Action` deriving
  structural equality (no closures in any case today) — verified.
- **Mouse path untouched:** `MouseScrolled`/SGR-mouse branch is outside the
  keybinding layer and is asserted unchanged in final verification.
- **Open judgment calls flagged inline:** `Ctrl+Q` two-stage handling
  (keymap-driven vs bespoke — recommend bespoke), the single-vs-two `resolve`
  call in the fire path (recommend single), and the prefix-conflict drop
  semantics (value vs reference equality) are each called out for the
  implementer/reviewer to confirm.
