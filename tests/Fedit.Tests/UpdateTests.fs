module Fedit.Tests.UpdateTests

open Fedit
open Xunit
open FsUnit.Xunit

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) [] None

    model

[<Fact>]
let ``init produces a model with the scratch buffer`` () =
    let model = initModel ()
    model.Editors.Buffers.Count |> should equal 1
    model.Editors.ActiveBufferId |> should equal 1
    model.Focus |> should equal Editor

[<Fact>]
let ``init returns a ScanWorkspace startup effect`` () =
    let _, effects =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) [] None

    effects
    |> List.exists (fun e ->
        match e with
        | ScanWorkspace _ -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``KeyPressed Ctrl+q with clean buffers quits immediately`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(Ctrl 'q')) model
    next.ShouldQuit |> should equal true

[<Fact>]
let ``KeyPressed Ctrl+q with dirty buffer arms instead of quitting`` () =
    let model = initModel ()
    // dirty the scratch buffer
    let dirtied =
        { model with
            Editors =
                { model.Editors with
                    Buffers = model.Editors.Buffers |> Map.map (fun _ b -> { b with Dirty = true }) } }

    let armed, _ = Editor.update (KeyPressed(Ctrl 'q')) dirtied
    armed.ShouldQuit |> should equal false
    armed.QuitArmed |> should equal true

[<Fact>]
let ``Resize updates Terminal size on the model`` () =
    let model = initModel ()
    let next, _ = Editor.update (Resize { Width = 120; Height = 40 }) model
    next.Terminal.Width |> should equal 120
    next.Terminal.Height |> should equal 40

[<Fact>]
let ``typing a character into the editor inserts it`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(Character 'a')) model
    let buffer = next.Editors.Buffers[next.Editors.ActiveBufferId]
    Buffer.text buffer |> should equal "a"
    buffer.Dirty |> should equal true

[<Fact>]
let ``Ctrl+P opens the prompt in Command mode with : prefix`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(Ctrl 'p')) model
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal ":"
    next.Prompt.Mode |> should equal PromptMode.Command
    next.Focus |> should equal Prompt

[<Fact>]
let ``Ctrl+O opens the prompt in FilePicker mode with empty text`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(Ctrl 'o')) model
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal ""
    next.Prompt.Mode |> should equal FilePicker
    next.Focus |> should equal Prompt

[<Fact>]
let ``Ctrl+F opens the prompt in Search mode with / prefix`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(Ctrl 'f')) model
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal "/"
    next.Prompt.Mode |> should equal Search
    next.Focus |> should equal Prompt

[<Fact>]
let ``backspace through the command prefix flips to FilePicker and then stays open`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(Ctrl 'p')) model
    opened.Prompt.Text |> should equal ":"
    opened.Prompt.Mode |> should equal PromptMode.Command

    let flipped, _ = Editor.update (KeyPressed Backspace) opened
    flipped.Prompt.Text |> should equal ""
    flipped.Prompt.Mode |> should equal FilePicker

    // Holding backspace past the prefix must not dismiss the prompt — Esc
    // is the only way out so we don't drop the user's session by accident.
    let stillOpen, _ = Editor.update (KeyPressed Backspace) flipped
    stillOpen.Prompt.Active |> should equal true
    stillOpen.Prompt.Text |> should equal ""

[<Fact>]
let ``backspace on empty FilePicker prompt is a no-op`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(Ctrl 'o')) model
    opened.Prompt.Active |> should equal true
    let next, _ = Editor.update (KeyPressed Backspace) opened
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal opened.Prompt.Text

[<Fact>]
let ``Tab fills the prompt with the highlighted completion`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(Ctrl 'p')) model

    // Type `o` so the highlighted completion is `open`.
    let typed, _ = Editor.update (KeyPressed(Character 'o')) opened
    typed.Prompt.Completions |> List.isEmpty |> should equal false

    let firstApply =
        typed.Prompt.Completions.[typed.Prompt.SelectedCompletion].ApplyText

    let filled, _ = Editor.update (KeyPressed Tab) typed
    // Prompt is rewritten to ":<ApplyText>" of the highlighted item.
    filled.Prompt.Text |> should equal (":" + firstApply)
    // Prompt stays open so the user can keep typing arguments.
    filled.Prompt.Active |> should equal true

[<Fact>]
let ``Tab on empty completion list is a no-op`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(Ctrl 'p')) model

    // Type something that matches no command so completions empty out.
    let typed, _ =
        Editor.update (KeyPressed(Character 'z')) opened
        |> fst
        |> fun m -> Editor.update (KeyPressed(Character 'z')) m

    typed.Prompt.Completions |> should equal ([]: CompletionItem list)
    let after, _ = Editor.update (KeyPressed Tab) typed
    after.Prompt.Text |> should equal typed.Prompt.Text

[<Fact>]
let ``Escape closes the prompt and returns focus to the editor`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(Ctrl 'p')) model
    opened.Prompt.Active |> should equal true
    let closed, _ = Editor.update (KeyPressed Escape) opened
    closed.Prompt.Active |> should equal false
    closed.Focus |> should equal Editor

[<Fact>]
let ``Ctrl+B with hidden sidebar shows and focuses it`` () =
    let model =
        { initModel () with
            Panels =
                { (initModel ()).Panels with
                    SidebarVisible = false } }

    let next, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    next.Panels.SidebarVisible |> should equal true
    next.Focus |> should equal Sidebar

[<Fact>]
let ``Ctrl+B in editor focuses the visible sidebar`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    next.Panels.SidebarVisible |> should equal true
    next.Focus |> should equal Sidebar

[<Fact>]
let ``Ctrl+B again while focused on sidebar hides it and returns to editor`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    inSidebar.Focus |> should equal Sidebar
    let hidden, _ = Editor.update (KeyPressed(Ctrl 'b')) inSidebar
    hidden.Panels.SidebarVisible |> should equal false
    hidden.Focus |> should equal Editor

// ─────────────────────────────────────────────────────────────────────
// Buffer-switching chords
// ─────────────────────────────────────────────────────────────────────

/// Add a second empty buffer (id = 2) to a fresh model and return both
/// the new model and the sorted id list. Mirrors how FileOpened wires
/// in a new buffer without dragging in the LoadFile effect.
let private withTwoBuffers () =
    let model = initModel ()
    let second = Buffer.createEmpty 2

    let withBoth =
        { model with
            Editors =
                { model.Editors with
                    Buffers = model.Editors.Buffers |> Map.add second.Id second
                    NextBufferId = 3 } }

    withBoth

[<Fact>]
let ``Ctrl+PageDown cycles to the next buffer`` () =
    let model = withTwoBuffers ()
    model.Editors.ActiveBufferId |> should equal 1
    let next, _ = Editor.update (KeyPressed CtrlPageDown) model
    next.Editors.ActiveBufferId |> should equal 2

[<Fact>]
let ``Ctrl+PageUp cycles to the previous buffer`` () =
    let model = withTwoBuffers ()
    // Start on buffer 2 so PageUp moves back to 1.
    let onSecond =
        { model with
            Editors =
                { model.Editors with
                    ActiveBufferId = 2 } }

    let next, _ = Editor.update (KeyPressed CtrlPageUp) onSecond
    next.Editors.ActiveBufferId |> should equal 1

[<Fact>]
let ``Ctrl+PageDown wraps around at the end`` () =
    let model = withTwoBuffers ()

    let onSecond =
        { model with
            Editors =
                { model.Editors with
                    ActiveBufferId = 2 } }

    let next, _ = Editor.update (KeyPressed CtrlPageDown) onSecond
    next.Editors.ActiveBufferId |> should equal 1

[<Fact>]
let ``Ctrl+1 jumps to the first buffer by index`` () =
    let model = withTwoBuffers ()

    let onSecond =
        { model with
            Editors =
                { model.Editors with
                    ActiveBufferId = 2 } }

    let next, _ = Editor.update (KeyPressed(CtrlDigit 1)) onSecond
    next.Editors.ActiveBufferId |> should equal 1

[<Fact>]
let ``Ctrl+2 jumps to the second buffer by index`` () =
    let model = withTwoBuffers ()
    let next, _ = Editor.update (KeyPressed(CtrlDigit 2)) model
    next.Editors.ActiveBufferId |> should equal 2

[<Fact>]
let ``Ctrl+N where N exceeds buffer count is a silent no-op`` () =
    let model = withTwoBuffers ()
    let next, _ = Editor.update (KeyPressed(CtrlDigit 5)) model
    next.Editors.ActiveBufferId |> should equal model.Editors.ActiveBufferId
    // No notification surfaced — out-of-range presses don't bark.
    next.Notification |> should equal (None: Notification option)

// ─────────────────────────────────────────────────────────────────────
// Newline normalization — the document invariant is LF-only; the buffer's
// Newline field remembers the original ending for round-tripping on save.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``FileOpened with CRLF normalizes to LF and remembers CRLF preference`` () =
    let model = initModel ()
    let next, _ = Editor.update (FileOpened("/x.txt", Result.Ok "a\r\nb\r\n")) model
    let buffer = next.Editors.Buffers[next.Editors.ActiveBufferId]
    (Buffer.text buffer).Contains "\r" |> should equal false
    Buffer.line 0 buffer |> should equal "a"
    Buffer.line 1 buffer |> should equal "b"
    buffer.Newline |> should equal "\r\n"

[<Fact>]
let ``FileOpened with lone CR normalizes to LF and saves as LF`` () =
    let model = initModel ()
    let next, _ = Editor.update (FileOpened("/x.txt", Result.Ok "a\rb\r")) model
    let buffer = next.Editors.Buffers[next.Editors.ActiveBufferId]
    (Buffer.text buffer).Contains "\r" |> should equal false
    Buffer.line 0 buffer |> should equal "a"
    Buffer.line 1 buffer |> should equal "b"
    buffer.Newline |> should equal "\n"

[<Fact>]
let ``ClipboardPasted strips CRLF before inserting`` () =
    let model = initModel ()
    let next, _ = Editor.update (ClipboardPasted(Result.Ok "a\r\nb")) model
    let buffer = next.Editors.Buffers[next.Editors.ActiveBufferId]
    (Buffer.text buffer).Contains "\r" |> should equal false
    Buffer.text buffer |> should equal "a\nb"

// ─────────────────────────────────────────────────────────────────────
// Phase-1 characterization net: these pin current behavior so the
// runAction refactor is provably behavior-preserving.
// ─────────────────────────────────────────────────────────────────────

let private withText (s: string) =
    // Type each char of s into a fresh model, return the resulting model.
    s
    |> Seq.fold (fun m c -> fst (Editor.update (KeyPressed(Character c)) m)) (initModel ())

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
    |> List.exists (function
        | ClipboardCopy _ -> true
        | _ -> false)
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
    |> List.exists (function
        | ScanWorkspace _ -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``Down in the focused sidebar moves the tree selection`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    inSidebar.Focus |> should equal Sidebar
    let moved, _ = Editor.update (KeyPressed Down) inSidebar
    moved.Focus |> should equal Sidebar

[<Fact>]
let ``typing in the focused sidebar is consumed, not inserted into the editor buffer`` () =
    // With no workspace tree loaded the incremental filter retains nothing,
    // but the keystroke must still be consumed by the sidebar — never routed
    // to the editor buffer. That routing is what the refactor must preserve.
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    let before = Buffer.text (Editor.activeBufferState inSidebar)
    let after, _ = Editor.update (KeyPressed(Character 's')) inSidebar
    Buffer.text (Editor.activeBufferState after) |> should equal before
    after.Focus |> should equal Sidebar

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
    let next, _ = Editor.runAction (When(SidebarVisible, FocusSidebar, Action.FocusEditor)) model
    next.Focus |> should equal Sidebar

[<Fact>]
let ``runAction When picks the else-branch when the cond fails`` () =
    let model =
        { initModel () with
            Panels =
                { (initModel ()).Panels with
                    SidebarVisible = false } }

    let next, _ = Editor.runAction (When(SidebarVisible, FocusSidebar, Action.FocusEditor)) model
    next.Focus |> should equal Editor

[<Fact>]
let ``runAction HideSidebar clears the incremental search`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(Ctrl 'b')) model
    let searching, _ = Editor.update (KeyPressed(Character 'x')) inSidebar
    let hidden, _ = Editor.runAction HideSidebar searching
    hidden.Workspace.SearchBuffer |> should equal ""
    hidden.Panels.SidebarVisible |> should equal false
