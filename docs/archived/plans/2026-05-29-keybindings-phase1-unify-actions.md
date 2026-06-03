# Keybindings Phase 1 — Unify Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse fedit's three scattered key-dispatch sites (the global `Ctrl` handler, `runEditor`, `runSidebar`) into one named `Action` vocabulary executed by a single `runAction`, with **zero behavior change**.

**Architecture:** Add an `Action`/`Cond` discriminated-union vocabulary (`Actions.fs`) and a single `runAction : Action -> Model -> Model * Effect list` (plus `evalCond`) in `Editor.fs`, whose arm bodies are lifted verbatim from today's inline handlers. Each dispatch site is then rewritten to map its `KeyInput` to an `Action` and call `runAction`. The MVU loop, `KeyInput` type, and `Msg`/`Effect` types are untouched in this phase (the richer `Chord` key model is Phase 2; the user keymap file is Phase 3).

**Tech Stack:** F# (.NET 9 SDK pinned in `.dotnet`), xUnit + FsUnit + FsCheck. Build/test via `just` only (never bare `dotnet`).

---

## Scope & deviations from the spec

This plan implements **only Phase 1** of [`docs/superpowers/specs/2026-05-29-keybindings-spec.md`](../specs/2026-05-29-keybindings-spec.md) §9.

Two deliberate, documented refinements of spec §2.1 / §6.8, made to keep the
"no behavior change" guarantee provable:

1. **`Chord`/`KeyStroke` are NOT introduced yet.** `KeyInput` stays. The
   dispatch sites map `KeyInput -> Action` directly. The `Chord` key model is
   Phase 2.
2. **`executeCommand` is only _partially_ collapsed.** Command verbs whose
   behavior is byte-identical to a chord action delegate to `runAction`
   (Task 7). Verbs that historically diverge from their chord cousin
   (`:editor`/`FocusEditor`) or are prompt-only (`open`, `theme`, `recent`,
   `buffers`, `syntax`, `plugin`, `goto`, `config`) **stay in
   `executeCommand`**. `runAction` delegates _to_ `executeCommand` for the
   prompt-only verbs it names (`SetTheme`, `Goto`, `OpenConfig`, `RunPlugin`).
   The full collapse lands in Phase 3 alongside the keymap, where divergences
   are reconciled on purpose. (Direction is cycle-free: see Task 7.)

The deferred-macro actions (`RecordMacro`, `ReplayMacro`) and `ReloadKeybinds`
are added to the `Action` DU now (so the type is stable) but are **no-ops** in
`runAction` until later phases.

---

## File structure

| File                               | Change     | Responsibility                                                                                                                                                                                                                          |
| ---------------------------------- | ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Fedit/Actions.fs`             | **create** | The `Cond` and `Action` DUs. Pure data, no `Model` reference.                                                                                                                                                                           |
| `src/Fedit/Fedit.fsproj`           | modify     | Add `<Compile Include="Actions.fs" />` immediately after `Commands.fs`.                                                                                                                                                                 |
| `src/Fedit/Editor.fs`              | modify     | Add `moveCursor`/`extendCursor` helpers; add `runAction`/`evalCond` to the `executeCommand` recursive group; rewrite the global `Ctrl` handler, `runEditor`, `runSidebar`, and the unifiable `executeCommand` arms to call `runAction`. |
| `tests/Fedit.Tests/UpdateTests.fs` | modify     | Add characterization tests (motions, edits, sidebar nav, clipboard effects) + direct `runAction` tests (`Chain`/`When`/primitives).                                                                                                     |

No other files change. `Primitives.fs`, `Model.fs`, `Input.fs`, `Runtime.fs`,
`Commands.fs` are untouched in Phase 1.

**Compile-order rule (CLAUDE.md gotcha):** `Actions.fs` must be listed in
`Fedit.fsproj` `<Compile>` **and** the file committed, or CI fails with
`FS0225`. It goes after `Commands.fs` (it has no dependency on `Commands`, but
this leaves room for `Keymap.fs` after it in Phase 3) and before `Prompt.fs`.

---

## Task 1: Characterization safety net

Lock in current behavior with tests **before** refactoring. These pass against
today's code; they are the regression net that proves "no behavior change."

**Files:**

- Test: `tests/Fedit.Tests/UpdateTests.fs` (append)

- [ ] **Step 1: Establish the green baseline**

Run: `just test`
Expected: PASS (all existing tests green, including the `Ctrl+B` tri-state and
buffer-switch tests already in `UpdateTests.fs`).

- [ ] **Step 2: Add characterization tests for motions, edits, clipboard, sidebar nav**

Append to `tests/Fedit.Tests/UpdateTests.fs`:

```fsharp
// ─────────────────────────────────────────────────────────────────────
// Phase-1 characterization net: these pin current behavior so the
// runAction refactor is provably behavior-preserving.
// ─────────────────────────────────────────────────────────────────────

let private withText (s: string) =
    // Type each char of s into a fresh model, return the resulting model.
    s |> Seq.fold (fun m c -> fst (Editor.update (KeyPressed(Character c)) m)) (initModel ())

[<Fact>]
let ``Right then Left leaves the cursor where it started`` () =
    let model = withText "abc"
    let home, _ = Editor.update (KeyPressed Home) model
    let right, _ = Editor.update (KeyPressed Right) home
    (Editor.activeBufferState right).Cursor.Column |> should equal 1
    let back, _ = Editor.update (KeyPressed Left) right
    (Editor.activeBufferState back).Cursor.Column |> should equal 0

[<Fact>]
let ``Shift+Right selects one character`` () =
    let model = withText "abc"
    let home, _ = Editor.update (KeyPressed Home) model
    let sel, _ = Editor.update (KeyPressed ShiftRight) home
    (Editor.activeBufferState sel).Selection.IsSome |> should equal true

[<Fact>]
let ``Ctrl+A selects the whole buffer`` () =
    let model = withText "abc"
    let sel, _ = Editor.update (KeyPressed(Ctrl 'a')) model
    (Editor.activeBufferState sel).Selection.IsSome |> should equal true

[<Fact>]
let ``Ctrl+C with a selection emits a ClipboardCopy effect`` () =
    let model = withText "abc"
    let selected, _ = Editor.update (KeyPressed(Ctrl 'a')) model
    let _, effects = Editor.update (KeyPressed(Ctrl 'c')) selected
    effects
    |> List.exists (function ClipboardCopy _ -> true | _ -> false)
    |> should equal true

[<Fact>]
let ``Ctrl+C with no selection emits no effect`` () =
    let model = initModel ()
    let _, effects = Editor.update (KeyPressed(Ctrl 'c')) model
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``Ctrl+V emits a ClipboardPaste effect`` () =
    let model = initModel ()
    let _, effects = Editor.update (KeyPressed(Ctrl 'v')) model
    effects |> should equal [ ClipboardPaste ]

[<Fact>]
let ``Ctrl+Z then Ctrl+Y round-trips an edit`` () =
    // Undo granularity is an internal detail; a round-trip is robust to it.
    let model = withText "ab"
    (Buffer.text (Editor.activeBufferState model)) |> should equal "ab"
    let undone, _ = Editor.update (KeyPressed(Ctrl 'z')) model
    (Buffer.text (Editor.activeBufferState undone)) |> should not' (equal "ab")
    let redone, _ = Editor.update (KeyPressed(Ctrl 'y')) undone
    (Buffer.text (Editor.activeBufferState redone)) |> should equal "ab"

[<Fact>]
let ``Tab indents by the configured tab width`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed Tab) model
    (Buffer.text (Editor.activeBufferState next)).Length
    |> should equal model.Config.TabWidth

[<Fact>]
let ``Ctrl+R requests a workspace rescan`` () =
    let model = initModel ()
    let _, effects = Editor.update (KeyPressed(Ctrl 'r')) model
    effects
    |> List.exists (function ScanWorkspace _ -> true | _ -> false)
    |> should equal true

[<Fact>]
let ``Down in the focused sidebar moves the tree selection`` () =
    // Focus the sidebar first (Ctrl+B from the default visible state).
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    inSidebar.Focus |> should equal Sidebar
    // Down should not throw and should keep focus in the sidebar.
    let moved, _ = Editor.update (KeyPressed Down) inSidebar
    moved.Focus |> should equal Sidebar

[<Fact>]
let ``typing in the focused sidebar appends to the incremental filter`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    let filtered, _ = Editor.update (KeyPressed(Character 's')) inSidebar
    filtered.Workspace.SearchBuffer |> should equal "s"
```

- [ ] **Step 3: Run the new tests to confirm they pass against current code**

Run: `just test`
Expected: PASS. (If `Editor.activeBufferState` is not accessible, note it is
already a public `let` in `Editor.fs:45` — no change needed.)

- [ ] **Step 4: Commit**

```bash
git add tests/Fedit.Tests/UpdateTests.fs
git commit -m "test(editor): characterization net for key dispatch before action refactor"
```

---

## Task 2: Add the Action vocabulary (`Actions.fs`)

**Files:**

- Create: `src/Fedit/Actions.fs`
- Modify: `src/Fedit/Fedit.fsproj`

- [ ] **Step 1: Create `src/Fedit/Actions.fs`**

```fsharp
namespace Fedit

/// Boolean predicate over the model, evaluated by `Editor.evalCond`.
/// Closed DU on purpose — this is NOT a VS Code-style open `when` language.
type Cond =
    | SidebarVisible
    | SidebarFocused
    | EditorFocused
    | PromptActive
    | HasSelection
    | BufferDirty
    | Not of Cond
    | AllOf of Cond list
    | AnyOf of Cond list

/// The single named vocabulary of everything a keybinding can trigger.
/// Pure data — no `Model` reference — so it compiles below `Editor`.
/// `Editor.runAction` is the one interpreter.
type Action =
    // motion / selection
    | MoveLeft | MoveRight | MoveUp | MoveDown
    | MoveWordLeft | MoveWordRight | MoveHome | MoveEnd
    | MovePageUp | MovePageDown
    | ExtendLeft | ExtendRight | ExtendUp | ExtendDown
    | ExtendHome | ExtendEnd | SelectAll
    // editing
    | Indent | Unindent | DeleteWordBack | DeleteWordForward
    | Undo | Redo | Copy | Cut | Paste
    // commands
    | Save | SaveAs of string | Quit
    | OpenPalette | OpenFilePicker | OpenSearch
    | NextBuffer | PrevBuffer | JumpToBuffer of int
    | SetTheme of string | Goto of line: int * col: int option
    | ReloadWorkspace | OpenConfig | ReloadKeybinds
    | RunPlugin of source: string * name: string * arg: string
    // panel / focus primitives — each a COMPLETE, valid transition
    | RevealSidebar | HideSidebar | ToggleSidebar | FocusSidebar | FocusEditor
    // sidebar navigation
    | SidebarUp | SidebarDown | SidebarPageUp | SidebarPageDown
    | SidebarTop | SidebarBottom | SidebarCollapse | SidebarExpand | SidebarActivate
    // composition & control flow
    | Chain of Action list
    | When of cond: Cond * thenDo: Action * elseDo: Action
    | NoOp
    // deferred — bind/parse-able later, but no-ops until macros/keymap land
    | RecordMacro of register: char
    | ReplayMacro of register: char * count: int
```

- [ ] **Step 2: Register the file in `Fedit.fsproj`**

In `src/Fedit/Fedit.fsproj`, find the `<Compile Include="Commands.fs" />` line
and add the new file immediately after it:

```xml
    <Compile Include="Commands.fs" />
    <Compile Include="Actions.fs" />
```

- [ ] **Step 3: Build to confirm the new type compiles and is ordered correctly**

Run: `just build`
Expected: PASS (no `FS0225 Source file could not be found`, no other errors).

- [ ] **Step 4: Commit**

```bash
git add src/Fedit/Actions.fs src/Fedit/Fedit.fsproj
git commit -m "feat(editor): add Action/Cond vocabulary types"
```

---

## Task 3: Implement `runAction` + `evalCond`

Add the interpreter to `Editor.fs`. Bodies are lifted verbatim from the
existing inline handlers. **No callers are rewritten yet** — this task only
adds new code, so the suite stays green throughout.

**Files:**

- Modify: `src/Fedit/Editor.fs`
- Test: `tests/Fedit.Tests/UpdateTests.fs`

- [ ] **Step 1: Add two motion helpers near `updateActiveBuffer`**

In `src/Fedit/Editor.fs`, immediately **after** `updateActiveBuffer` (which
ends at line 94), add:

```fsharp
    /// Cursor motion that drops any existing selection. Mirrors the
    /// `move` closure that used to live inside runEditor.
    let private moveCursor transform model =
        updateActiveBuffer (Buffer.clearSelection >> transform) model, []

    /// Shifted motion that extends the selection through the new cursor.
    let private extendCursor transform model =
        updateActiveBuffer (Buffer.extendSelectionToCursor >> transform) model, []
```

- [ ] **Step 2: Add `evalCond` and `runAction` to the recursive group**

The functions `applyPluginActions` and `executeCommand` form a
`let rec private … and private …` group (`Editor.fs:447-750`). `runAction`
must join this group because it calls `executeCommand` (for prompt-only verbs)
and, after Task 7, `executeCommand` calls it back.

Immediately **after** the final `executeCommand` arm — the `PluginInvoke` arm
ending at `Editor.fs:750` — and **before** `let private moveSearchMatch`
(line 752), insert the following two `and` bindings:

```fsharp
    and evalCond (cond: Cond) (model: Model) : bool =
        match cond with
        | SidebarVisible -> model.Panels.SidebarVisible
        | SidebarFocused -> model.Focus = Sidebar
        | EditorFocused -> model.Focus = Editor
        | PromptActive -> model.Prompt.Active
        | HasSelection -> (activeBufferState model).Selection.IsSome
        | BufferDirty -> (activeBufferState model).Dirty
        | Not c -> not (evalCond c model)
        | AllOf cs -> cs |> List.forall (fun c -> evalCond c model)
        | AnyOf cs -> cs |> List.exists (fun c -> evalCond c model)

    /// The single dispatcher. Each arm is the transition lifted verbatim
    /// from the handler that used to inline it. Public so tests and (later
    /// phases) the keymap resolver can call it.
    and runAction (action: Action) (model: Model) : Model * Effect list =
        match action with
        // composition & control flow
        | NoOp -> model, []
        | Chain actions ->
            actions
            |> List.fold (fun (m, fx) a ->
                let m', fx' = runAction a m
                m', fx @ fx') (model, [])
        | When(cond, thenDo, elseDo) -> runAction (if evalCond cond model then thenDo else elseDo) model

        // motion / selection (verbatim from runEditor)
        | MoveLeft -> moveCursor Buffer.moveLeft model
        | MoveRight -> moveCursor Buffer.moveRight model
        | MoveUp -> moveCursor Buffer.moveUp model
        | MoveDown -> moveCursor Buffer.moveDown model
        | MoveHome -> moveCursor Buffer.moveHome model
        | MoveEnd -> moveCursor Buffer.moveEnd model
        | MoveWordLeft -> moveCursor Buffer.moveLeftWord model
        | MoveWordRight -> moveCursor (Buffer.moveRightWord model.Config.WordMotion) model
        | MovePageUp ->
            let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
            let jump = max 1 (viewportHeight - model.Config.PageOverlap)
            moveCursor (Buffer.movePageUp jump) model
        | MovePageDown ->
            let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
            let jump = max 1 (viewportHeight - model.Config.PageOverlap)
            moveCursor (Buffer.movePageDown jump) model
        | ExtendLeft -> extendCursor Buffer.moveLeft model
        | ExtendRight -> extendCursor Buffer.moveRight model
        | ExtendUp -> extendCursor Buffer.moveUp model
        | ExtendDown -> extendCursor Buffer.moveDown model
        | ExtendHome -> extendCursor Buffer.moveHome model
        | ExtendEnd -> extendCursor Buffer.moveEnd model
        | SelectAll -> updateActiveBuffer Buffer.selectAll model, []

        // editing (verbatim from runEditor + global handler)
        | Indent -> moveCursor (Buffer.indent model.Config.TabWidth) model
        | Unindent -> moveCursor (Buffer.unindent model.Config.TabWidth) model
        | DeleteWordBack ->
            let buffer = activeBufferState model
            if buffer.Selection.IsSome then updateActiveBuffer Buffer.deleteSelection model, []
            else updateActiveBuffer Buffer.backspaceWord model, []
        | DeleteWordForward ->
            let buffer = activeBufferState model
            if buffer.Selection.IsSome then updateActiveBuffer Buffer.deleteSelection model, []
            else updateActiveBuffer (Buffer.deleteForwardWord model.Config.WordMotion) model, []
        | Undo -> updateActiveBuffer Buffer.undo model, []
        | Redo -> updateActiveBuffer Buffer.redo model, []
        | Copy ->
            let buffer = activeBufferState model
            let text = Buffer.selectionText buffer
            if String.IsNullOrEmpty text then model, []
            else
                { model with Notification = Some(Notification.info $"Copied {text.Length} char(s)") },
                [ ClipboardCopy text ]
        | Cut ->
            let buffer = activeBufferState model
            let text = Buffer.selectionText buffer
            if String.IsNullOrEmpty text then model, []
            else
                updateActiveBuffer
                    Buffer.deleteSelection
                    { model with Notification = Some(Notification.info $"Cut {text.Length} char(s)") },
                [ ClipboardCopy text ]
        | Paste -> model, [ ClipboardPaste ]

        // command-group bodies (verbatim from global handler / executeCommand)
        | Save -> saveActiveBuffer None model
        | SaveAs path -> saveActiveBuffer (Some path) model
        | Quit -> { model with ShouldQuit = true }, [ SaveConfig model.Config ]
        | OpenPalette -> openPrompt ":" { model with Workspace = Workspace.clearSearch model.Workspace }
        | OpenFilePicker -> openPrompt "" { model with Workspace = Workspace.clearSearch model.Workspace }
        | OpenSearch -> openPrompt "/" { model with Workspace = Workspace.clearSearch model.Workspace }
        | NextBuffer -> switchBuffer 1 model, []
        | PrevBuffer -> switchBuffer -1 model, []
        | JumpToBuffer n -> jumpToBuffer n model, []
        | ReloadWorkspace -> model, [ ScanWorkspace model.Workspace.RootPath ]

        // panel / focus primitives
        | RevealSidebar -> { model with Panels = { model.Panels with SidebarVisible = true } }, []
        | HideSidebar ->
            { model with
                Panels = { model.Panels with SidebarVisible = false }
                Workspace = Workspace.clearSearch model.Workspace }, []
        | ToggleSidebar ->
            { model with
                Panels = { model.Panels with SidebarVisible = not model.Panels.SidebarVisible } }, []
        | FocusSidebar -> { model with Focus = Sidebar }, []
        | FocusEditor ->
            { model with
                Focus = Editor
                Workspace = Workspace.clearSearch model.Workspace }, []

        // sidebar navigation (verbatim from runSidebar)
        | SidebarUp ->
            { model with Workspace = Workspace.moveSelection -1 (Workspace.clearSearch model.Workspace) }, []
        | SidebarDown ->
            { model with Workspace = Workspace.moveSelection 1 (Workspace.clearSearch model.Workspace) }, []
        | SidebarPageUp ->
            { model with
                Workspace = Workspace.moveSelection -model.Config.TreePageJump (Workspace.clearSearch model.Workspace) }, []
        | SidebarPageDown ->
            { model with
                Workspace = Workspace.moveSelection model.Config.TreePageJump (Workspace.clearSearch model.Workspace) }, []
        | SidebarTop -> { model with Workspace = Workspace.moveHome (Workspace.clearSearch model.Workspace) }, []
        | SidebarBottom -> { model with Workspace = Workspace.moveEnd (Workspace.clearSearch model.Workspace) }, []
        | SidebarCollapse ->
            let cleared = Workspace.clearSearch model.Workspace
            let nextWorkspace =
                match Workspace.tryCollapseSelected cleared with
                | Some collapsed -> collapsed
                | None -> Workspace.selectParent cleared
            { model with Workspace = nextWorkspace }, []
        | SidebarExpand ->
            { model with Workspace = Workspace.expandSelected (Workspace.clearSearch model.Workspace) }, []
        | SidebarActivate ->
            let workspace, sidebarAction = Workspace.activateSelected (Workspace.clearSearch model.Workspace)
            match sidebarAction with
            | SidebarOpenFile path -> { model with Workspace = workspace }, [ LoadFile path ]
            | SidebarNoOp -> { model with Workspace = workspace }, []

        // verbs whose canonical body stays in executeCommand (prompt-only)
        // RHS cases are qualified `Command.*` because Action and Command
        // share these names; executeCommand expects a Command.
        | SetTheme name -> executeCommand (Command.Theme name) model
        | Goto(line, col) -> executeCommand (Command.Goto(line, col)) model
        | OpenConfig -> executeCommand Command.OpenConfig model
        | RunPlugin(source, name, arg) -> executeCommand (Command.PluginInvoke(source, name, arg)) model

        // deferred — later phases wire these; no-ops for now
        | ReloadKeybinds -> model, []
        | RecordMacro _ -> model, []
        | ReplayMacro _ -> model, []
```

- [ ] **Step 3: Build**

Run: `just build`
Expected: PASS. (If the compiler reports `runAction`/`evalCond` are not
accessible from tests, confirm they are written without the `private` keyword —
they default to public inside the public `Editor` module.)

- [ ] **Step 4: Add direct `runAction` unit tests**

Append to `tests/Fedit.Tests/UpdateTests.fs`:

```fsharp
// ─────────────────────────────────────────────────────────────────────
// runAction interpreter — combinators and a few primitives.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``runAction NoOp is identity`` () =
    let model = initModel ()
    let next, effects = Editor.runAction NoOp model
    next |> should equal model
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``runAction Chain applies actions left to right`` () =
    let model = initModel ()
    // RevealSidebar then FocusSidebar: visible + focused.
    let next, _ = Editor.runAction (Chain [ RevealSidebar; FocusSidebar ]) model
    next.Panels.SidebarVisible |> should equal true
    next.Focus |> should equal Sidebar

[<Fact>]
let ``runAction When picks the then-branch when the cond holds`` () =
    let model = initModel () // sidebar visible by default
    let next, _ = Editor.runAction (When(SidebarVisible, FocusSidebar, FocusEditor)) model
    next.Focus |> should equal Sidebar

[<Fact>]
let ``runAction When picks the else-branch when the cond fails`` () =
    let model =
        { initModel () with Panels = { (initModel ()).Panels with SidebarVisible = false } }
    let next, _ = Editor.runAction (When(SidebarVisible, FocusSidebar, FocusEditor)) model
    next.Focus |> should equal Editor

[<Fact>]
let ``runAction HideSidebar clears the incremental search`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    let searching, _ = Editor.update (KeyPressed(Character 'x')) inSidebar
    searching.Workspace.SearchBuffer |> should equal "x"
    let hidden, _ = Editor.runAction HideSidebar searching
    hidden.Workspace.SearchBuffer |> should equal ""
    hidden.Panels.SidebarVisible |> should equal false
```

- [ ] **Step 5: Run tests**

Run: `just test`
Expected: PASS (new `runAction` tests + the entire existing suite).

- [ ] **Step 6: Commit**

```bash
git add src/Fedit/Editor.fs tests/Fedit.Tests/UpdateTests.fs
git commit -m "feat(editor): add runAction/evalCond interpreter (no callers yet)"
```

---

## Task 4: Route the global `Ctrl` handler through `runAction`

Rewrite the `KeyPressed` arms in `update` to map each chord to an `Action` and
call `runAction`, preserving the existing `Notification`/`QuitArmed`
pre-processing. The two-stage `Ctrl+Q` stays bespoke (it owns `QuitArmed`).

**Files:**

- Modify: `src/Fedit/Editor.fs` (the `| KeyPressed key ->` branch, ~lines 1219-1330)

- [ ] **Step 1: Rewrite the chord arms**

In the `| KeyPressed key ->` branch, leave the `Ctrl 'q'` arm and the
`let model = if key = Ctrl 'q' then model else { model with QuitArmed = false }`
preamble exactly as they are. Replace the arms from `Ctrl 'p'` through
`CtrlDigit` with these `runAction` delegations (the `Ctrl+B` tri-state becomes
the `When` tree; every other arm threads `{ model with Notification = None }`
just as today):

```fsharp
            | Ctrl 'p' -> runAction OpenPalette { model with Notification = None }
            | Ctrl 'o' -> runAction OpenFilePicker { model with Notification = None }
            | Ctrl 'f' -> runAction OpenSearch { model with Notification = None }
            | Ctrl 'b' ->
                // Tri-state sidebar, expressed via the combinators (spec §6.5):
                //   hidden            → reveal + focus
                //   visible, !focused → focus
                //   visible, focused  → hide + focus editor
                runAction
                    (When(SidebarVisible,
                          When(SidebarFocused, Chain [ HideSidebar; FocusEditor ], FocusSidebar),
                          Chain [ RevealSidebar; FocusSidebar ]))
                    { model with Notification = None }
            | Ctrl 'e' -> runAction FocusEditor { model with Notification = None }
            | Ctrl 's' -> runAction Save { model with Notification = None }
            | Ctrl 'r' -> runAction ReloadWorkspace { model with Notification = None }
            | Ctrl 'z' -> runAction Undo { model with Notification = None }
            | Ctrl 'y' -> runAction Redo { model with Notification = None }
            | Ctrl 'a' -> runAction SelectAll { model with Notification = None }
            | Ctrl 'c' -> runAction Copy { model with Notification = None }
            | Ctrl 'x' -> runAction Cut { model with Notification = None }
            | Ctrl 'v' -> runAction Paste { model with Notification = None }
            | CtrlPageDown -> runAction NextBuffer { model with Notification = None }
            | CtrlPageUp -> runAction PrevBuffer { model with Notification = None }
            | CtrlDigit n when n >= 1 && n <= 9 -> runAction (JumpToBuffer n) { model with Notification = None }
            | _ ->
                match model.Focus with
                | Sidebar -> runSidebar key { model with Notification = None }
                | Editor -> runEditor key { model with Notification = None }
                | Prompt -> runPrompt key { model with Notification = None }
```

Notes on parity (do not skip — these are the subtle ones):

- `Ctrl+E` now routes to `FocusEditor`, which clears the workspace search — the
  same as the old inline `Ctrl+E` arm (`Editor.fs:1288-1293`).
- `Ctrl+B`'s `HideSidebar` clears the workspace search, matching the old hide
  branch; `RevealSidebar`/`FocusSidebar` do not touch the workspace, matching
  the old reveal/focus branches.
- `Copy`/`Cut` set their own `Notification`, overriding the `None` the caller
  passes — identical to the old `Ctrl+C`/`Ctrl+X` arms.

- [ ] **Step 2: Run the suite**

Run: `just test`
Expected: PASS. The existing `Ctrl+B` tri-state tests, the buffer-switch tests,
`Ctrl+P/O/F`, and the new clipboard/undo/rescan tests all still pass — that is
the behavior-preservation proof for this task.

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Editor.fs
git commit -m "refactor(editor): route global Ctrl chords through runAction"
```

---

## Task 5: Route `runEditor` through `runAction`

Keep the plugin-chord pre-check and the text fast-path (`Character`/`Enter`/
`Backspace`/`Delete`) inline; replace the motion/edit arms with `runAction`.

**Files:**

- Modify: `src/Fedit/Editor.fs` (`runEditor`, ~lines 839-917)

- [ ] **Step 1: Replace the default-behavior match in `runEditor`**

Leave the plugin-dispatch block (`Editor.fs:840-863`) and the local
`hasSelection`/`editTransform` helpers used by the text fast-path **unchanged**.
Replace the final `match key with` (the block beginning `| Character value ->`
at `Editor.fs:887` through the closing `| _ -> model, []` at `Editor.fs:917`)
with:

```fsharp
            match key with
            // text fast-path — literal input, not keymap actions
            | Character value ->
                updateActiveBuffer (editTransform (Buffer.insertText (string value)) >> Buffer.clearSelection) model, []
            | Enter -> updateActiveBuffer (editTransform Buffer.insertNewline >> Buffer.clearSelection) model, []
            | Backspace when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
            | Backspace -> updateActiveBuffer Buffer.backspace model, []
            | Delete when hasSelection -> updateActiveBuffer Buffer.deleteSelection model, []
            | Delete -> updateActiveBuffer Buffer.deleteForward model, []
            // motions / edits — delegated to the unified interpreter
            | Left -> runAction MoveLeft model
            | Right -> runAction MoveRight model
            | Up -> runAction MoveUp model
            | Down -> runAction MoveDown model
            | Home -> runAction MoveHome model
            | End -> runAction MoveEnd model
            | ShiftLeft -> runAction ExtendLeft model
            | ShiftRight -> runAction ExtendRight model
            | ShiftUp -> runAction ExtendUp model
            | ShiftDown -> runAction ExtendDown model
            | ShiftHome -> runAction ExtendHome model
            | ShiftEnd -> runAction ExtendEnd model
            | PageUp -> runAction MovePageUp model
            | PageDown -> runAction MovePageDown model
            | Tab -> runAction Indent model
            | ShiftTab -> runAction Unindent model
            | AltLeft -> runAction MoveWordLeft model
            | AltRight -> runAction MoveWordRight model
            | CtrlBackspace -> runAction DeleteWordBack model
            | CtrlDelete -> runAction DeleteWordForward model
            | _ -> model, []
```

The `editTransform`/`hasSelection` closures remain defined above this match
(they are still used by the `Character`/`Enter` arms). `runAction`'s
`DeleteWordBack`/`DeleteWordForward` reproduce the old selection-aware
`CtrlBackspace`/`CtrlDelete` arms exactly.

- [ ] **Step 2: Run the suite**

Run: `just test`
Expected: PASS (the motion/edit characterization tests from Task 1 cover these
paths; the `typing a character` test guards the text fast-path).

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Editor.fs
git commit -m "refactor(editor): route runEditor motions and edits through runAction"
```

---

## Task 6: Route `runSidebar` through `runAction`

Keep the incremental-filter arms (`Character`, `Backspace`-while-searching)
inline; replace navigation arms with `runAction`; route `Escape` to
`FocusEditor` (identical transition).

**Files:**

- Modify: `src/Fedit/Editor.fs` (`runSidebar`, lines 770-829)

- [ ] **Step 1: Rewrite `runSidebar`**

Replace the whole `runSidebar` function body (`Editor.fs:770-829`) with:

```fsharp
    let private runSidebar key model =
        match key with
        // incremental-filter fast-path — literal input
        | Character c -> { model with Workspace = Workspace.appendSearch c model.Workspace }, []
        | Backspace when model.Workspace.SearchBuffer.Length > 0 ->
            { model with Workspace = Workspace.backspaceSearch model.Workspace }, []
        // navigation — delegated to the unified interpreter
        | Up -> runAction SidebarUp model
        | Down -> runAction SidebarDown model
        | PageUp -> runAction SidebarPageUp model
        | PageDown -> runAction SidebarPageDown model
        | Home -> runAction SidebarTop model
        | End -> runAction SidebarBottom model
        | Left -> runAction SidebarCollapse model
        | Right -> runAction SidebarExpand model
        | Enter -> runAction SidebarActivate model
        | Escape -> runAction FocusEditor model
        | _ -> model, []
```

Parity note: the old `Escape` arm produced
`{ model with Workspace = Workspace.clearSearch …; Focus = Editor }, []`, which
is exactly `runAction FocusEditor`. `runSidebar` is always called with
`{ model with Notification = None }` from `update`, so notification behavior is
unchanged.

- [ ] **Step 2: Run the suite**

Run: `just test`
Expected: PASS (the sidebar-nav + incremental-filter characterization tests
from Task 1, plus the existing tri-state tests, cover these).

- [ ] **Step 3: Commit**

```bash
git add src/Fedit/Editor.fs
git commit -m "refactor(editor): route runSidebar navigation through runAction"
```

---

## Task 7: Collapse the unifiable `executeCommand` verbs + add `Action.ofCommand`

Make the command verbs whose behavior is byte-identical to a chord action
delegate to `runAction`, so there is a single body. Divergent/prompt-only verbs
stay as-is (see Scope note).

**Files:**

- Modify: `src/Fedit/Actions.fs` (add `Action.ofCommand`)
- Modify: `src/Fedit/Editor.fs` (`executeCommand` arms)

- [ ] **Step 1: Add a partial `Command -> Action` mapping in `Actions.fs`**

Append to `src/Fedit/Actions.fs` (below the `Action` type):

```fsharp
[<RequireQualifiedAccess>]
module Action =
    /// Map the command verbs that have an exact chord-action equivalent.
    /// `None` for prompt-only / divergent verbs, which executeCommand keeps.
    let ofCommand (command: Command) : Action option =
        match command with
        | Command.Write -> Some Save
        | Command.WriteAs path -> Some(SaveAs path)
        | Command.Quit -> Some Quit
        | Command.NextBuffer -> Some NextBuffer
        | Command.PreviousBuffer -> Some PrevBuffer
        | Command.ReloadWorkspace -> Some ReloadWorkspace
        | Command.ToggleSidebar -> Some ToggleSidebar
        | Command.FocusTree -> Some FocusSidebar
        | _ -> None
```

(`Actions.fs` is compiled after `Commands.fs`, so the `Command` type is in
scope. `FocusEditor`/`Theme`/`Goto`/`OpenConfig`/`Open`/`Recent`/
`SwitchBuffer`/`Syntax`/`Plugin`/`PluginInvoke` are intentionally `None` — the
first diverges from its chord cousin, the rest are prompt-only and keep their
bodies in `executeCommand`, which `runAction` already delegates to.)

- [ ] **Step 2: Delegate the matching `executeCommand` arms**

In `src/Fedit/Editor.fs`, replace these `executeCommand` arms with delegations.

Replace `| Write -> saveActiveBuffer None model` (`Editor.fs:530`) and
`| WriteAs path -> saveActiveBuffer (Some path) model` (`Editor.fs:531`) with:

```fsharp
        | Write -> runAction Save model
        | WriteAs path -> runAction (SaveAs path) model
```

Replace `| Quit -> { model with ShouldQuit = true }, [ SaveConfig model.Config ]`
(`Editor.fs:546`) with:

```fsharp
        | Quit -> runAction Action.Quit model
```

Replace the sidebar/focus/buffer/reload arms (`Editor.fs:547-557`):

```fsharp
        | ToggleSidebar -> runAction Action.ToggleSidebar model
        | FocusTree -> runAction FocusSidebar model
        | FocusEditor -> { model with Focus = Editor }, []
        | ReloadWorkspace -> runAction Action.ReloadWorkspace model
        | NextBuffer -> runAction Action.NextBuffer model
        | PreviousBuffer -> runAction PrevBuffer model
```

Note: `FocusEditor` (the `:editor` verb) keeps its original body
`{ model with Focus = Editor }, []` — it does **not** clear the workspace
search, unlike the `Ctrl+E` chord. This divergence is preserved on purpose
(reconciled in Phase 3). `Action.Quit`/`Action.ToggleSidebar`/etc. are
qualified to disambiguate from the same-named `Command` cases in this scope.

Leave `Theme`, `Recent`, `SwitchBuffer`, `Command.Goto`, `OpenConfig`,
`Syntax`, all `Plugin(...)` arms, `Open`, and `PluginInvoke` **unchanged** —
their bodies are the canonical ones `runAction` delegates to.

- [ ] **Step 3: Confirm there is no delegation cycle**

Verify by inspection (no command goes both directions):

- `runAction` → `executeCommand`: `SetTheme`→`Theme`, `Goto`→`Command.Goto`,
  `OpenConfig`→`OpenConfig`, `RunPlugin`→`PluginInvoke`.
- `executeCommand` → `runAction`: `Write`/`WriteAs`/`Quit`/`NextBuffer`/
  `PreviousBuffer`/`ReloadWorkspace`/`ToggleSidebar`/`FocusTree`.
  No verb appears on both lists, so the recursion terminates.

- [ ] **Step 4: Build and run the full suite**

Run: `just check`
Expected: PASS (lint + build + test). This is the pre-commit gate.

- [ ] **Step 5: Add command-parity tests**

Append to `tests/Fedit.Tests/UpdateTests.fs`:

```fsharp
[<Fact>]
let ``Action.ofCommand maps write to Save and leaves theme unmapped`` () =
    Action.ofCommand Command.Write |> should equal (Some Save)
    Action.ofCommand (Command.Theme "x") |> should equal (None: Action option)

[<Fact>]
let ``Ctrl+E focuses the editor and clears the sidebar search`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    let searching, _ = Editor.update (KeyPressed(Character 'q')) inSidebar
    searching.Workspace.SearchBuffer |> should equal "q"
    let focused, _ = Editor.update (KeyPressed(Ctrl 'e')) searching
    focused.Focus |> should equal Editor
    focused.Workspace.SearchBuffer |> should equal ""
```

(The `:editor` command's intentional divergence — focusing without clearing
the search — is documented in Task 7 Step 2 but not unit-tested here, because
`executeCommand` is private and reaching it requires a full prompt round-trip;
the prose note plus the `Ctrl+E` test above are sufficient for Phase 1.)

- [ ] **Step 6: Run and commit**

Run: `just test`
Expected: PASS.

```bash
git add src/Fedit/Actions.fs src/Fedit/Editor.fs tests/Fedit.Tests/UpdateTests.fs
git commit -m "refactor(editor): collapse unifiable command verbs onto runAction"
```

---

## Final verification

- [ ] **Step 1: Full gate**

Run: `just check`
Expected: PASS (fantomas lint clean, build clean, all tests green).

- [ ] **Step 2: Manual smoke (optional but recommended)**

Run: `just run .`
Confirm by hand: typing inserts; arrows/word-motion/page move; `Ctrl+S` saves;
`Ctrl+B` cycles sidebar hidden→focused→hidden; `Ctrl+C/X/V` copy/cut/paste;
`Ctrl+Z/Y` undo/redo; `Ctrl+P`/`Ctrl+F` open the prompt; sidebar arrows + Enter
navigate and open files; `Escape` in the sidebar returns to the editor.

- [ ] **Step 3: Confirm the diff is a pure refactor**

`Model`, `Msg`, `Effect`, `Primitives.KeyInput`, and `Input.tryMap` are
unchanged. `git diff --stat` should show only `Actions.fs` (new),
`Fedit.fsproj`, `Editor.fs`, and `UpdateTests.fs`.

---

## Self-review checklist (done while authoring)

- **Spec coverage:** Implements spec §9 Phase 1 (Action vocabulary +
  `runAction`/`evalCond` + the three chord sites routed + tri-state via
  `When`/`Chain` + parity suite). `Chord` model (Phase 2), user keymap file
  (Phase 3), and macros (Phase 4) are explicitly out of scope.
- **No behavior change:** every arm body is lifted verbatim; the existing
  `UpdateTests.fs` plus the Task-1 additions are the regression net; the one
  intentional non-change (`:editor` vs `Ctrl+E` divergence) is called out and
  test-pinned.
- **Type consistency:** `Action`/`Cond` case names are identical across
  `Actions.fs`, `runAction`, the caller rewrites, and `Action.ofCommand`.
  `runAction`/`evalCond` are public (no `private`) so tests reach them.
- **Cycle-free delegation:** verified in Task 7 Step 3.
- **Compile order:** `Actions.fs` added to `Fedit.fsproj` after `Commands.fs`
  (Task 2) — the CLAUDE.md `FS0225` gotcha.
