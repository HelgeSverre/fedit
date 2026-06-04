module Fedit.Tests.UpdateTests

open Fedit
open Xunit
open FsUnit.Xunit

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) [] None

    model

// Terse Chord constructors for migrating the Phase-1 characterization net
// from the old KeyInput literals.
let private ck c : Chord =
    { Mods = Set.ofList [ Ctrl ]
      Key = Key.Char c } // ctrl+<char>

let private chr c : Chord = { Mods = Set.empty; Key = Key.Char c } // bare printable
let private nk n : Chord = { Mods = Set.empty; Key = Named n } // bare named key

let private snk n : Chord =
    { Mods = Set.ofList [ Shift ]
      Key = Named n } // shift+<named>

let private cnk n : Chord =
    { Mods = Set.ofList [ Ctrl ]
      Key = Named n } // ctrl+<named>

let private csk c : Chord =
    { Mods = Set.ofList [ Ctrl; Shift ]
      Key = Key.Char c } // ctrl+shift+<char>

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
let ``keybind command opens a formatted keybindings buffer in the editor`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    // Ctrl+P → type "keybind" → Enter.
    let opened = initModel () |> press (ck 'p')
    let typed = "keybind" |> Seq.fold (fun m c -> press (chr c) m) opened
    let submitted = press (nk Enter) typed

    let active = submitted.Editors.Buffers[submitted.Editors.ActiveBufferId]
    active.Name |> should equal "keybindings"
    submitted.Focus |> should equal Editor
    Assert.Contains("## global", Buffer.text active)
    Assert.Contains("ctrl+s", Buffer.text active)

[<Fact>]
let ``KeyPressed Ctrl+q with clean buffers quits immediately`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(ck 'q')) model
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

    let armed, _ = Editor.update (KeyPressed(ck 'q')) dirtied
    armed.ShouldQuit |> should equal false
    armed.QuitArmed |> should equal true

[<Fact>]
let ``Resize updates Terminal size on the model`` () =
    let model = initModel ()
    let next, _ = Editor.update (Resize { Width = 120; Height = 40 }) model
    next.Terminal.Width |> should equal 120
    next.Terminal.Height |> should equal 40

// --- Mouse wheel: viewport-led (default) vs line mode ---

let private modelWithLines n =
    let model = initModel ()
    let text = String.concat "\n" [ for i in 0 .. n - 1 -> sprintf "line%d" i ]
    let buf = Buffer.fromText 1 None "test" text "\n"

    { model with
        Editors =
            { model.Editors with
                Buffers = Map.ofList [ 1, buf ] } }

let private activeBuffer (model: Model) =
    model.Editors.Buffers[model.Editors.ActiveBufferId]

[<Fact>]
let ``MouseScrolled down in viewport mode moves the viewport`` () =
    // default config: ScrollViewport, scrolloff 5, 3 lines/tick; height 24-8-2 = 14
    let model = modelWithLines 100
    let next, _ = Editor.update (MouseScrolled 1) model
    (activeBuffer next).ViewportTop |> should equal 3

[<Fact>]
let ``MouseScrolled up at the top of the file is a no-op in viewport mode`` () =
    let model = modelWithLines 100
    let next, _ = Editor.update (MouseScrolled -1) model
    (activeBuffer next).ViewportTop |> should equal 0
    (activeBuffer next).Cursor.Line |> should equal 0

[<Fact>]
let ``MouseScrolled down in line mode moves the cursor`` () =
    let baseModel = modelWithLines 100

    let model =
        { baseModel with
            Config =
                { baseModel.Config with
                    ScrollMode = ScrollLine } }

    let next, _ = Editor.update (MouseScrolled 1) model
    (activeBuffer next).Cursor.Line |> should equal 3

[<Fact>]
let ``typing a character into the editor inserts it`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(chr 'a')) model
    let buffer = next.Editors.Buffers[next.Editors.ActiveBufferId]
    Buffer.text buffer |> should equal "a"
    buffer.Dirty |> should equal true

[<Fact>]
let ``pressing Space inserts a space into the editor buffer`` () =
    let model = initModel ()
    let a, _ = Editor.update (KeyPressed(chr 'a')) model
    let spaced, _ = Editor.update (KeyPressed(nk Space)) a
    let b, _ = Editor.update (KeyPressed(chr 'b')) spaced
    let buffer = b.Editors.Buffers[b.Editors.ActiveBufferId]
    Buffer.text buffer |> should equal "a b"

[<Fact>]
let ``pressing Space in the sidebar reaches the incremental search`` () =
    // The sidebar search is jump-to-match, not a free-text filter: a space is
    // only retained when it extends a query that still matches a tree entry.
    // A filename with an embedded space proves Space routes through dispatch.
    let tree: FileNode =
        { Path = "/root"
          Name = "root"
          IsDirectory = true
          Children =
            [ { Path = "/root/my file.fs"
                Name = "my file.fs"
                IsDirectory = false
                Children = [] } ] }

    let model = initModel ()

    let focused =
        { model with
            Focus = Sidebar
            Workspace = Workspace.setTree tree model.Workspace }

    let m, _ = Editor.update (KeyPressed(chr 'm')) focused
    let y, _ = Editor.update (KeyPressed(chr 'y')) m
    let spaced, _ = Editor.update (KeyPressed(nk Space)) y
    spaced.Workspace.SearchBuffer |> should equal "my "

[<Fact>]
let ``Ctrl+P opens the prompt in Command mode with : prefix`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(ck 'p')) model
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal ":"
    next.Prompt.Mode |> should equal PromptMode.Command
    next.Focus |> should equal Prompt

[<Fact>]
let ``Ctrl+O opens the prompt in FilePicker mode with empty text`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(ck 'o')) model
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal ""
    next.Prompt.Mode |> should equal FilePicker
    next.Focus |> should equal Prompt

[<Fact>]
let ``Ctrl+F opens the prompt in Search mode with / prefix`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(ck 'f')) model
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal "/"
    next.Prompt.Mode |> should equal Search
    next.Focus |> should equal Prompt

[<Fact>]
let ``backspace through the command prefix flips to FilePicker and then stays open`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(ck 'p')) model
    opened.Prompt.Text |> should equal ":"
    opened.Prompt.Mode |> should equal PromptMode.Command

    let flipped, _ = Editor.update (KeyPressed(nk Backspace)) opened
    flipped.Prompt.Text |> should equal ""
    flipped.Prompt.Mode |> should equal FilePicker

    // Holding backspace past the prefix must not dismiss the prompt — Esc
    // is the only way out so we don't drop the user's session by accident.
    let stillOpen, _ = Editor.update (KeyPressed(nk Backspace)) flipped
    stillOpen.Prompt.Active |> should equal true
    stillOpen.Prompt.Text |> should equal ""

[<Fact>]
let ``backspace on empty FilePicker prompt is a no-op`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(ck 'o')) model
    opened.Prompt.Active |> should equal true
    let next, _ = Editor.update (KeyPressed(nk Backspace)) opened
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal opened.Prompt.Text

[<Fact>]
let ``Tab fills the prompt with the highlighted completion`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(ck 'p')) model

    // Type `o` so the highlighted completion is `open`.
    let typed, _ = Editor.update (KeyPressed(chr 'o')) opened
    typed.Prompt.Completions |> List.isEmpty |> should equal false

    let firstApply =
        typed.Prompt.Completions.[typed.Prompt.SelectedCompletion].ApplyText

    let filled, _ = Editor.update (KeyPressed(nk Tab)) typed
    // Prompt is rewritten to ":<ApplyText>" of the highlighted item.
    filled.Prompt.Text |> should equal (":" + firstApply)
    // Prompt stays open so the user can keep typing arguments.
    filled.Prompt.Active |> should equal true

[<Fact>]
let ``Space inserts a space so command arguments can be typed`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(ck 'p')) model

    // ck 'p' already seeds the prompt with ":".
    let typed =
        "theme"
        |> Seq.fold (fun m c -> fst (Editor.update (KeyPressed(chr c)) m)) opened

    let spaced, _ = Editor.update (KeyPressed(nk Space)) typed
    spaced.Prompt.Text |> should equal ":theme "

[<Fact>]
let ``Tab on empty completion list is a no-op`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(ck 'p')) model

    // Type something that matches no command so completions empty out.
    let typed, _ =
        Editor.update (KeyPressed(chr 'z')) opened
        |> fst
        |> fun m -> Editor.update (KeyPressed(chr 'z')) m

    typed.Prompt.Completions |> should equal ([]: CompletionItem list)
    let after, _ = Editor.update (KeyPressed(nk Tab)) typed
    after.Prompt.Text |> should equal typed.Prompt.Text

[<Fact>]
let ``Escape closes the prompt and returns focus to the editor`` () =
    let model = initModel ()
    let opened, _ = Editor.update (KeyPressed(ck 'p')) model
    opened.Prompt.Active |> should equal true
    let closed, _ = Editor.update (KeyPressed(nk Escape)) opened
    closed.Prompt.Active |> should equal false
    closed.Focus |> should equal Editor

[<Fact>]
let ``Ctrl+B with hidden sidebar shows and focuses it`` () =
    let model =
        { initModel () with
            Panels =
                { (initModel ()).Panels with
                    SidebarVisible = false } }

    let next, _ = Editor.update (KeyPressed(ck 'b')) model
    next.Panels.SidebarVisible |> should equal true
    next.Focus |> should equal Sidebar

[<Fact>]
let ``Ctrl+B in editor focuses the visible sidebar`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(ck 'b')) model
    next.Panels.SidebarVisible |> should equal true
    next.Focus |> should equal Sidebar

[<Fact>]
let ``Ctrl+B again while focused on sidebar hides it and returns to editor`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(ck 'b')) model
    inSidebar.Focus |> should equal Sidebar
    let hidden, _ = Editor.update (KeyPressed(ck 'b')) inSidebar
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
    let next, _ = Editor.update (KeyPressed(cnk PageDown)) model
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

    let next, _ = Editor.update (KeyPressed(cnk PageUp)) onSecond
    next.Editors.ActiveBufferId |> should equal 1

[<Fact>]
let ``Ctrl+PageDown wraps around at the end`` () =
    let model = withTwoBuffers ()

    let onSecond =
        { model with
            Editors =
                { model.Editors with
                    ActiveBufferId = 2 } }

    let next, _ = Editor.update (KeyPressed(cnk PageDown)) onSecond
    next.Editors.ActiveBufferId |> should equal 1

[<Fact>]
let ``Ctrl+1 jumps to the first buffer by index`` () =
    let model = withTwoBuffers ()

    let onSecond =
        { model with
            Editors =
                { model.Editors with
                    ActiveBufferId = 2 } }

    let next, _ = Editor.update (KeyPressed(ck '1')) onSecond
    next.Editors.ActiveBufferId |> should equal 1

[<Fact>]
let ``Ctrl+2 jumps to the second buffer by index`` () =
    let model = withTwoBuffers ()
    let next, _ = Editor.update (KeyPressed(ck '2')) model
    next.Editors.ActiveBufferId |> should equal 2

[<Fact>]
let ``Ctrl+N where N exceeds buffer count is a silent no-op`` () =
    let model = withTwoBuffers ()
    let next, _ = Editor.update (KeyPressed(ck '5')) model
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
    |> Seq.fold (fun m c -> fst (Editor.update (KeyPressed(chr c)) m)) (initModel ())

[<Fact>]
let ``Right then Left leaves the cursor where it started`` () =
    let model = withText "abc"
    let home, _ = Editor.update (KeyPressed(nk Home)) model
    let right, _ = Editor.update (KeyPressed(nk Right)) home
    (Editor.activeBufferState right).Cursor.Column |> should equal 1
    let back, _ = Editor.update (KeyPressed(nk Left)) right
    (Editor.activeBufferState back).Cursor.Column |> should equal 0

[<Fact>]
let ``Shift+Right selects one character`` () =
    let model = withText "abc"
    let home, _ = Editor.update (KeyPressed(nk Home)) model
    let sel, _ = Editor.update (KeyPressed(snk Right)) home
    (Editor.activeBufferState sel).Selection.IsSome |> should equal true

[<Fact>]
let ``Ctrl+A selects the whole buffer`` () =
    let model = withText "abc"
    let sel, _ = Editor.update (KeyPressed(ck 'a')) model
    (Editor.activeBufferState sel).Selection.IsSome |> should equal true

[<Fact>]
let ``Ctrl+C with a selection emits a ClipboardCopy effect`` () =
    let model = withText "abc"
    let selected, _ = Editor.update (KeyPressed(ck 'a')) model
    let _, effects = Editor.update (KeyPressed(ck 'c')) selected

    effects
    |> List.exists (function
        | ClipboardCopy _ -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``Ctrl+C with no selection emits no effect`` () =
    let model = initModel ()
    let _, effects = Editor.update (KeyPressed(ck 'c')) model
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``Ctrl+V emits a ClipboardPaste effect`` () =
    let model = initModel ()
    let _, effects = Editor.update (KeyPressed(ck 'v')) model
    effects |> should equal [ ClipboardPaste ]

[<Fact>]
let ``Ctrl+Z then Ctrl+Y round-trips an edit`` () =
    // Undo granularity is an internal detail; a round-trip is robust to it.
    let model = withText "ab"
    (Buffer.text (Editor.activeBufferState model)) |> should equal "ab"
    let undone, _ = Editor.update (KeyPressed(ck 'z')) model
    (Buffer.text (Editor.activeBufferState undone)) |> should not' (equal "ab")
    let redone, _ = Editor.update (KeyPressed(ck 'y')) undone
    (Buffer.text (Editor.activeBufferState redone)) |> should equal "ab"

[<Fact>]
let ``Tab indents by the configured tab width`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(nk Tab)) model

    (Buffer.text (Editor.activeBufferState next)).Length
    |> should equal model.Config.TabWidth

[<Fact>]
let ``Ctrl+R requests a workspace rescan`` () =
    let model = initModel ()
    let _, effects = Editor.update (KeyPressed(ck 'r')) model

    effects
    |> List.exists (function
        | ScanWorkspace _ -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``Down in the focused sidebar moves the tree selection`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(ck 'b')) model
    inSidebar.Focus |> should equal Sidebar
    let moved, _ = Editor.update (KeyPressed(nk Down)) inSidebar
    moved.Focus |> should equal Sidebar

[<Fact>]
let ``typing in the focused sidebar is consumed, not inserted into the editor buffer`` () =
    // With no workspace tree loaded the incremental filter retains nothing,
    // but the keystroke must still be consumed by the sidebar — never routed
    // to the editor buffer. That routing is what the refactor must preserve.
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(ck 'b')) model
    let before = Buffer.text (Editor.activeBufferState inSidebar)
    let after, _ = Editor.update (KeyPressed(chr 's')) inSidebar
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

    let next, _ =
        Editor.runAction (When(SidebarVisible, FocusSidebar, Action.FocusEditor)) model

    next.Focus |> should equal Sidebar

[<Fact>]
let ``runAction When picks the else-branch when the cond fails`` () =
    let model =
        { initModel () with
            Panels =
                { (initModel ()).Panels with
                    SidebarVisible = false } }

    let next, _ =
        Editor.runAction (When(SidebarVisible, FocusSidebar, Action.FocusEditor)) model

    next.Focus |> should equal Editor

[<Fact>]
let ``runAction HideSidebar clears the incremental search`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(ck 'b')) model
    let searching, _ = Editor.update (KeyPressed(chr 'x')) inSidebar
    let hidden, _ = Editor.runAction HideSidebar searching
    hidden.Workspace.SearchBuffer |> should equal ""
    hidden.Panels.SidebarVisible |> should equal false

[<Fact>]
let ``Action.ofCommand maps write to Save and leaves theme unmapped`` () =
    Action.ofCommand Command.Write |> should equal (Some Save)
    Action.ofCommand (Command.Theme "x") |> should equal (None: Action option)

[<Fact>]
let ``Ctrl+E focuses the editor (FocusEditor action)`` () =
    let model = initModel ()
    let inSidebar, _ = Editor.update (KeyPressed(ck 'b')) model
    inSidebar.Focus |> should equal Sidebar
    let focused, _ = Editor.update (KeyPressed(ck 'e')) inSidebar
    focused.Focus |> should equal Editor

// ─────────────────────────────────────────────────────────────────────
// wip #8 — Ctrl+arrows word motion (added alongside Alt+arrows)
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``Ctrl+Right moves forward by a word`` () =
    let model = withText "hello world"
    let home, _ = Editor.update (KeyPressed(nk Home)) model
    let viaCtrl, _ = Editor.update (KeyPressed(cnk Right)) home
    (Editor.activeBufferState viaCtrl).Cursor.Column |> should be (greaterThan 0)

// ─────────────────────────────────────────────────────────────────────
// Sequence engine (Sequence.step) — pure, tested against a synthetic
// prefix predicate. No live keymap binds a sequence in Phase 2.
// ─────────────────────────────────────────────────────────────────────

let private kc c : Chord =
    { Mods = Set.ofList [ Ctrl ]
      Key = Key.Char c }

[<Fact>]
let ``Sequence.step from empty with no prefixes Fires the single stroke`` () =
    Sequence.step (fun _ -> false) [] (kc 'k')
    |> should equal (Sequence.Fire [ kc 'k' ])

[<Fact>]
let ``Sequence.step keeps a known prefix Pending`` () =
    let isPrefix = (=) [ kc 'k' ]
    Sequence.step isPrefix [] (kc 'k') |> should equal (Sequence.Pending [ kc 'k' ])

[<Fact>]
let ``Sequence.step surfaces the completed candidate (caller resolves it against the keymap)`` () =
    // Pending [ctrl+k], feed ctrl+c. [ctrl+k; ctrl+c] is not a *proper* prefix
    // of anything longer, so step returns Failed carrying the full candidate —
    // the caller resolves that candidate (it may be a bound 2-chord sequence)
    // and, crucially, does NOT fall through to text insert.
    Sequence.step ((=) [ kc 'k' ]) [ kc 'k' ] (kc 'c')
    |> should equal (Sequence.Failed [ kc 'k'; kc 'c' ])

[<Fact>]
let ``Sequence.step Fails (does not insert) when a pending prefix is not extended`` () =
    match Sequence.step ((=) [ kc 'k' ]) [ kc 'k' ] (chr 'z') with
    | Sequence.Failed _ -> ()
    | other -> failwithf "expected Failed, got %A" other

[<Fact>]
let ``SequenceTimedOut clears any pending prefix`` () =
    let model =
        { initModel () with
            PendingPrefix = Some([ kc 'k' ], 1L) }

    let next, _ = Editor.update SequenceTimedOut model
    next.PendingPrefix |> should equal (None: (Chord list * int64) option)

// ─────────────────────────────────────────────────────────────────────
// Chord.toKeyChord — bridge to the frozen plugin-API KeyChord
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``Chord.toKeyChord maps the expressible subset and rejects the rest`` () =
    Chord.toKeyChord (kc 'c')
    |> should equal (Some(Fedit.PluginApi.KeyChord.Ctrl 'c'))

    Chord.toKeyChord
        { Mods = Set.ofList [ Ctrl; Shift ]
          Key = Key.Char 'p' }
    |> should equal (Some(Fedit.PluginApi.KeyChord.CtrlShift 'p'))

    Chord.toKeyChord
        { Mods = Set.ofList [ Alt ]
          Key = Key.Char 'x' }
    |> should equal (Some(Fedit.PluginApi.KeyChord.Alt 'x'))

    Chord.toKeyChord { Mods = Set.empty; Key = Fn 5 }
    |> should equal (Some(Fedit.PluginApi.KeyChord.F 5))

    Chord.toKeyChord { Mods = Set.empty; Key = Char 'a' }
    |> should equal (Some(Fedit.PluginApi.KeyChord.Char 'a'))

    Chord.toKeyChord
        { Mods = Set.ofList [ Super ]
          Key = Key.Char 'x' }
    |> should equal (None: Fedit.PluginApi.KeyChord option)

    Chord.toKeyChord (nk Left)
    |> should equal (None: Fedit.PluginApi.KeyChord option)

[<Fact>]
let ``a plugin keybinding on a non-global Ctrl chord still fires through the editor`` () =
    // Bind Ctrl+K (not a built-in global chord) to the built-in `write`
    // command via the plugin registry, give the active buffer a path so Save
    // emits a SaveBuffer effect, and assert the chord routes through.
    let model = initModel ()

    let pathed = Buffer.fromText 1 (Some "/root/file.txt") "file.txt" "content" "\n"

    let model =
        { model with
            Editors =
                { model.Editors with
                    Buffers = Map.ofList [ 1, pathed ] }
            Plugins =
                { model.Plugins with
                    Keybindings = [ (Fedit.PluginApi.KeyChord.Ctrl 'k', "write") ] } }

    let _, effects = Editor.update (KeyPressed(ck 'k')) model

    effects
    |> List.exists (function
        | SaveBuffer _ -> true
        | _ -> false)
    |> should equal true

// ── macros: record / replay / repeat (keybindings phase 4) ───────────────

[<Fact>]
let ``RecordMacro starts recording into the named register`` () =
    let model = initModel ()
    let recording, _ = Editor.runAction (RecordMacro 'a') model
    recording.Recording |> should equal (Some 'a')

[<Fact>]
let ``recording captures each subsequent chord into the register`` () =
    let model = initModel ()
    let recording, _ = Editor.runAction (RecordMacro 'a') model
    let after, _ = Editor.update (KeyPressed(chr 'x')) recording
    (after.Registers |> Map.find 'a') |> should equal [ chr 'x' ]

[<Fact>]
let ``RecordMacro again stops recording and remembers the register`` () =
    let model = initModel ()
    let on, _ = Editor.runAction (RecordMacro 'a') model
    let off, _ = Editor.runAction (RecordMacro 'a') on
    off.Recording |> should equal None
    off.LastMacro |> should equal (Some 'a')

[<Fact>]
let ``ReplayMacro emits a ReplayKeys effect carrying the recorded chords`` () =
    let model =
        { initModel () with
            Registers = Map.ofList [ 'a', [ chr 'x'; chr 'y' ] ] }

    let _, effects = Editor.runAction (ReplayMacro('a', 2)) model

    match effects with
    | [ ReplayKeys(chords, count) ] ->
        chords |> should equal [ chr 'x'; chr 'y' ]
        count |> should equal 2
    | other -> failwithf "expected one ReplayKeys effect, got %A" other

[<Fact>]
let ``replaying an empty register produces no effect`` () =
    let model = initModel ()
    let _, effects = Editor.runAction (ReplayMacro('z', 1)) model
    effects |> List.isEmpty |> should equal true

[<Fact>]
let ``replaying the register currently being recorded is refused`` () =
    let model =
        { initModel () with
            Recording = Some 'a'
            Registers = Map.ofList [ 'a', [ chr 'x' ] ] }

    let _, effects = Editor.runAction (ReplayMacro('a', 1)) model
    effects |> List.isEmpty |> should equal true

[<Fact>]
let ``ReplayMacro does not start a nested replay while already replaying`` () =
    let model =
        { initModel () with
            Replaying = true
            Registers = Map.ofList [ 'a', [ chr 'x' ] ] }

    let _, effects = Editor.runAction (ReplayMacro('a', 1)) model
    effects |> List.isEmpty |> should equal true

[<Fact>]
let ``keys injected during replay are not appended to a recording register`` () =
    // Recording @b while a replay is in flight must not capture the injected
    // keys — the Replaying flag suppresses the record-append hook.
    let model =
        { initModel () with
            Recording = Some 'b'
            Replaying = true }

    let after, _ = Editor.update (KeyPressed(chr 'z')) model

    (after.Registers |> Map.tryFind 'b' |> Option.defaultValue [])
    |> List.isEmpty
    |> should equal true

[<Fact>]
let ``MacroReplayStart and MacroReplayEnd bracket the replaying flag`` () =
    let model = initModel ()
    let started, _ = Editor.update MacroReplayStart model
    started.Replaying |> should equal true
    let ended, _ = Editor.update MacroReplayEnd started
    ended.Replaying |> should equal false

[<Fact>]
let ``replaying a recorded edit re-applies it through the marker bracket`` () =
    // Record "xy", stop, then drive the runtime's replay sequence by hand
    // (MacroReplayStart, the keys, MacroReplayEnd) and confirm the edit repeats
    // without re-recording the injected keys.
    let model = initModel ()
    let rec0, _ = Editor.runAction (RecordMacro 'a') model
    let r1, _ = Editor.update (KeyPressed(chr 'x')) rec0
    let r2, _ = Editor.update (KeyPressed(chr 'y')) r1
    let recorded, _ = Editor.runAction (RecordMacro 'a') r2
    let chords = recorded.Registers |> Map.find 'a'

    let afterStart, _ = Editor.update MacroReplayStart recorded

    let afterKeys =
        chords |> List.fold (fun m c -> fst (Editor.update (KeyPressed c) m)) afterStart

    let afterEnd, _ = Editor.update MacroReplayEnd afterKeys

    let buffer = afterEnd.Editors.Buffers[afterEnd.Editors.ActiveBufferId]
    Buffer.text buffer |> should equal "xyxy"
    // injected keys were not re-recorded into the still-present register
    (afterEnd.Registers |> Map.find 'a') |> should equal chords

[<Fact>]
let ``the stop-recording chord is not captured into the register`` () =
    // Drive through the bound default chord (Ctrl+Shift+M → record-macro:a) so
    // the record-append hook's record-toggle guard is exercised end to end.
    let model = initModel ()
    let on, _ = Editor.update (KeyPressed(csk 'm')) model
    on.Recording |> should equal (Some 'a')
    let typed, _ = Editor.update (KeyPressed(chr 'x')) on
    let off, _ = Editor.update (KeyPressed(csk 'm')) typed
    off.Recording |> should equal None
    (off.Registers |> Map.find 'a') |> should equal [ chr 'x' ]

[<Fact>]
let ``RepeatLastMacro replays the last recorded register`` () =
    let model =
        { initModel () with
            Registers = Map.ofList [ 'a', [ chr 'x' ] ]
            LastMacro = Some 'a' }

    let _, effects = Editor.runAction RepeatLastMacro model

    match effects with
    | [ ReplayKeys(chords, count) ] ->
        chords |> should equal [ chr 'x' ]
        count |> should equal 1
    | other -> failwithf "expected one ReplayKeys effect, got %A" other

[<Fact>]
let ``RepeatLastMacro with no prior macro produces no effect`` () =
    let model = initModel ()
    let _, effects = Editor.runAction RepeatLastMacro model
    effects |> List.isEmpty |> should equal true
