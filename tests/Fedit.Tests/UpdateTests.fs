module Fedit.Tests.UpdateTests

open Fedit
open Fedit.PromptTypes
open Xunit
open FsUnit.Xunit

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

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
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

    effects
    |> List.exists (fun e ->
        match e with
        | ScanWorkspace _ -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``initWithInitialFile queues the file open after startup effects`` () =
    let _, effects =
        Editor.initWithInitialFile
            "/root"
            (Some "/root/file.fs")
            { Width = 80; Height = 24 }
            (Config.defaults Themes.defaultTheme)
            []

    effects
    |> List.last
    |> should equal (LoadFile("/root/file.fs", OpenPermanent, None))

[<Fact>]
let ``keybind command opens the keybinding prompt session`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let opened = initModel () |> press (ck 'p')

    let submitted =
        "keybind" |> Seq.fold (fun m c -> press (chr c) m) opened |> press (nk Enter)

    submitted.Prompt.Active |> should equal true
    submitted.Prompt.Session |> should equal PromptSessionKind.KeybindingsSession
    submitted.Prompt.SelectedItemId.IsSome |> should equal true
    submitted.Prompt.Text |> should equal ""
    submitted.Prompt.PendingConfirmation |> should equal None
    submitted.Focus |> should equal Prompt
    submitted.Editors.Buffers.Count |> should equal 1

[<Fact>]
let ``typing in the search prompt emits RunSearch carrying the document`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let withText = initModel () |> press (chr 'a') |> press (chr 'b')
    let inSearch = withText |> press (ck 'f')
    let _, effects = Editor.update (KeyPressed(chr 'a')) inSearch

    effects
    |> List.exists (fun e ->
        match e with
        | RunSearch(1, "a", document) -> PieceTable.toString document = "ab"
        | _ -> false)
    |> should equal true

[<Fact>]
let ``FileOpened schedules a highlight parse for the new buffer`` () =
    let _, effects =
        Editor.update (FileOpened("/root/x.fs", OpenPermanent, None, Result.Ok "let x = 1")) (initModel ())

    effects
    |> List.exists (fun e ->
        match e with
        | ParseHighlight(_, "fsharp", _, 0) -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``editing a highlighted buffer schedules a fresh parse at the new tick`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/x.fs", OpenPermanent, None, Result.Ok "let x = 1")) (initModel ())

    let _, effects = Editor.update (KeyPressed(chr 'y')) opened

    effects
    |> List.exists (fun e ->
        match e with
        | ParseHighlight(id, "fsharp", _, 1) -> id = opened.Editors.ActiveBufferId
        | _ -> false)
    |> should equal true

[<Fact>]
let ``HighlightParsed stores spans only for the current edit tick`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/x.fs", OpenPermanent, None, Result.Ok "let x = 1")) (initModel ())

    let bufferId = opened.Editors.ActiveBufferId

    let spans: HighlightSpan array =
        [| { Capture = Keyword
             StartByte = 0
             EndByte = 3 } |]

    let stale, _ = Editor.update (HighlightParsed(bufferId, 99, spans)) opened
    stale.HighlightStates.ContainsKey bufferId |> should equal false

    let fresh, _ = Editor.update (HighlightParsed(bufferId, 0, spans)) opened
    fresh.HighlightStates[bufferId] |> should equal spans

// "let x = 1\n" is 10 chars, so this lands just past the parse cap.
let private overCapText () =
    String.replicate (Highlight.maxParseChars / 10 + 1) "let x = 1\n"

let private dummySpans: HighlightSpan array =
    [| { Capture = Keyword
         StartByte = 0
         EndByte = 3 } |]

[<Fact>]
let ``editing a buffer over the parse cap emits no highlight effect and clears its spans`` () =
    let big = Buffer.fromText 1 (Some "/root/big.fs") "big.fs" (overCapText ()) "\n"
    let model = initModel ()

    let seeded =
        { model with
            Editors =
                { model.Editors with
                    Buffers = Map.ofList [ 1, big ] }
            HighlightStates = Map.ofList [ 1, dummySpans ] }

    let next, effects = Editor.update (KeyPressed(chr 'y')) seeded

    effects
    |> List.forall (fun e ->
        match e with
        | ParseHighlight _ -> false
        | _ -> true)
    |> should equal true

    next.HighlightStates.ContainsKey 1 |> should equal false

[<Fact>]
let ``a file path change reschedules highlighting without an edit`` () =
    // The scratch buffer has no path and EditTick 0; saving it as a .fs file
    // flips the detected language without an edit, so the chokepoint must
    // reschedule on the FilePath diff alone.
    let _, effects =
        Editor.update (BufferSaved(1, "/root/scratch.fs", 0, Result.Ok())) (initModel ())

    effects
    |> List.exists (fun e ->
        match e with
        | ParseHighlight(1, "fsharp", _, 0) -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``a rename to an unsupported extension clears stored spans`` () =
    let buf = Buffer.fromText 1 (Some "/root/x.fs") "x.fs" "let x = 1" "\n"
    let model = initModel ()

    let seeded =
        { model with
            Editors =
                { model.Editors with
                    Buffers = Map.ofList [ 1, buf ] }
            HighlightStates = Map.ofList [ 1, dummySpans ] }

    let next, effects =
        Editor.update (BufferSaved(1, "/root/x.xyz", 0, Result.Ok())) seeded

    next.HighlightStates.ContainsKey 1 |> should equal false

    effects
    |> List.forall (fun e ->
        match e with
        | ParseHighlight _ -> false
        | _ -> true)
    |> should equal true

[<Fact>]
let ``syntax on reports buffers skipped for size`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let big = Buffer.fromText 2 (Some "/root/big.fs") "big.fs" (overCapText ()) "\n"
    let model = initModel ()

    let seeded =
        { model with
            Editors =
                { model.Editors with
                    Buffers = model.Editors.Buffers |> Map.add 2 big } }

    let final =
        "syntax on"
        |> Seq.fold (fun m c -> press (chr c) m) (seeded |> press (ck 'p'))
        |> press (nk Enter)

    final.Notification
    |> Option.map (fun n -> n.Message)
    |> should equal (Some "Syntax highlighting on (1 buffer(s) too large to parse).")

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

/// Wheel position inside the editor text area: column 40 is right of the
/// sidebar (width 26 on the default 80x24 model) and its gutter.
let private inEditor: Position = { Line = 0; Column = 40 }

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

let private commandModel text =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let opened = initModel () |> press (ck 'p')
    text |> Seq.fold (fun m c -> press (chr c) m) opened |> press (nk Enter)

let private testPlugin name status =
    let manifest =
        { Name = name
          Version = "1.0.0"
          ApiVersion = "1"
          Description = ""
          Author = ""
          Homepage = ""
          EntryAssembly = $"{name}.dll"
          EntryType = "Test.Plugin" }

    { Manifest = manifest
      Path = $"/tmp/{name}"
      Status = status
      Commands = []
      Keybindings = []
      Conflicts = [] }

[<Fact>]
let ``MouseScrolled down in viewport mode moves the viewport`` () =
    // default config: ScrollViewport, scrolloff 5, 3 lines/tick; height 24-8-2 = 14
    let model = modelWithLines 100
    let next, _ = Editor.update (MouseScrolled(1, inEditor)) model
    (activeBuffer next).ViewportTop |> should equal 3

[<Fact>]
let ``MouseScrolled up at the top of the file is a no-op in viewport mode`` () =
    let model = modelWithLines 100
    let next, _ = Editor.update (MouseScrolled(-1, inEditor)) model
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

    let next, _ = Editor.update (MouseScrolled(1, inEditor)) model
    (activeBuffer next).Cursor.Line |> should equal 3

[<Fact>]
let ``typing a character into the editor inserts it`` () =
    let model = initModel ()
    let next, _ = Editor.update (KeyPressed(chr 'a')) model
    let buffer = next.Editors.Buffers[next.Editors.ActiveBufferId]
    Buffer.text buffer |> should equal "a"
    buffer.Dirty |> should equal true

[<Fact>]
let ``plugins command opens the plugin prompt session`` () =
    let next = commandModel "plugins"

    next.Prompt.Active |> should equal true
    next.Prompt.Session |> should equal PromptSessionKind.PluginsSession
    next.Prompt.SelectedItemId |> should equal None
    next.Prompt.Text |> should equal ""
    next.Prompt.PendingConfirmation |> should equal None
    next.Focus |> should equal Prompt

[<Fact>]
let ``macros command opens the macro prompt session`` () =
    let next = commandModel "macros"

    next.Prompt.Active |> should equal true
    next.Prompt.Session |> should equal PromptSessionKind.MacrosSession
    next.Prompt.SelectedItemId |> should equal (Some "a")
    next.Prompt.Text |> should equal ""
    next.Prompt.PendingConfirmation |> should equal None
    next.Focus |> should equal Prompt

[<Fact>]
let ``manager key handling blocks editor text insertion`` () =
    let model = commandModel "macros"
    let next, _ = Editor.update (KeyPressed(chr 'z')) model

    Buffer.text (activeBuffer next) |> should equal ""
    next.Prompt.Text |> should equal "z"

[<Fact>]
let ``prompt session input is not recorded into an active macro`` () =
    let model =
        { initModel () with
            Recording = Some 'q'
            Registers = Map.ofList [ 'q', [ chr 'x' ] ]
            Focus = Prompt
            Prompt =
                { (initModel ()).Prompt with
                    Active = true
                    Session = PromptSessionKind.MacrosSession
                    Text = ""
                    Cursor = 0
                    SelectedItemId = Some "a"
                    PendingConfirmation = None } }

    let next, _ = Editor.update (KeyPressed(chr 'z')) model

    next.Prompt.Text |> should equal "z"
    next.Registers |> Map.find 'q' |> should equal [ chr 'x' ]

[<Fact>]
let ``prompt session action keys are not shadowed by prompt keymap bindings`` () =
    let plugin = testPlugin "alpha" Loaded

    let model =
        { initModel () with
            Config =
                { (Config.defaults Themes.defaultTheme) with
                    DisabledPlugins = Set.empty }
            Plugins =
                { PluginRegistry.empty with
                    Loaded = Map.ofList [ "alpha", plugin ] }
            Keymap =
                (initModel ()).Keymap
                @ [ { Stroke = [ chr 'd' ]
                      Context = Context.Prompt
                      Action = Some OpenPalette } ]
            Focus = Prompt
            Prompt =
                { (initModel ()).Prompt with
                    Active = true
                    Session = PromptSessionKind.PluginsSession
                    Text = ""
                    Cursor = 0
                    SelectedItemId = Some "alpha"
                    PendingConfirmation = None } }

    let next, effects = Editor.update (KeyPressed(chr 'd')) model

    next.Config.DisabledPlugins |> should equal (Set.ofList [ "alpha" ])
    next.Prompt.Session |> should equal PromptSessionKind.PluginsSession

    effects
    |> should equal [ SaveConfig next.Config; ScanPlugins next.Config.DisabledPlugins ]

[<Fact>]
let ``plugin manager disable updates config and rescans with disabled set`` () =
    let plugin = testPlugin "alpha" Loaded

    let model =
        { initModel () with
            Config =
                { (Config.defaults Themes.defaultTheme) with
                    DisabledPlugins = Set.empty }
            Plugins =
                { PluginRegistry.empty with
                    Loaded = Map.ofList [ "alpha", plugin ] }
            Focus = Prompt
            Prompt =
                { (initModel ()).Prompt with
                    Active = true
                    Session = PromptSessionKind.PluginsSession
                    Text = ""
                    Cursor = 0
                    SelectedItemId = Some "alpha"
                    PendingConfirmation = None } }

    let next, effects = Editor.update (KeyPressed(chr 'd')) model

    next.Config.DisabledPlugins |> should equal (Set.ofList [ "alpha" ])

    effects
    |> should equal [ SaveConfig next.Config; ScanPlugins next.Config.DisabledPlugins ]

[<Fact>]
let ``plugin prompt session clamps selection after plugin scans`` () =
    let alpha = testPlugin "alpha" Loaded

    let registry =
        { PluginRegistry.empty with
            Loaded = Map.ofList [ "alpha", alpha ] }

    let model =
        { initModel () with
            Focus = Prompt
            Prompt =
                { (initModel ()).Prompt with
                    Active = true
                    Session = PromptSessionKind.PluginsSession
                    Text = ""
                    Cursor = 0
                    SelectedItemId = Some "beta"
                    PendingConfirmation = None } }

    let next, effects = Editor.update (PluginsScanned(Result.Ok registry)) model

    next.Prompt.SelectedItemId |> should equal (Some "alpha")
    Assert.Empty effects

[<Fact>]
let ``macro manager replays a non-empty selected register`` () =
    let chords = [ chr 'a'; chr 'b' ]

    let model =
        { initModel () with
            Registers = Map.ofList [ 'b', chords ]
            Focus = Prompt
            Prompt =
                { (initModel ()).Prompt with
                    Active = true
                    Session = PromptSessionKind.MacrosSession
                    Text = ""
                    Cursor = 0
                    SelectedItemId = Some "b"
                    PendingConfirmation = None } }

    let next, effects = Editor.update (KeyPressed(nk Enter)) model

    next.LastMacro |> should equal (Some 'b')
    next.Prompt.Active |> should equal false
    effects |> should equal [ ReplayKeys(chords, 1) ]

[<Fact>]
let ``macro manager starts recording and closes on overwrite`` () =
    let model =
        { initModel () with
            Focus = Prompt
            Prompt =
                { (initModel ()).Prompt with
                    Active = true
                    Session = PromptSessionKind.MacrosSession
                    Text = ""
                    Cursor = 0
                    SelectedItemId = Some "c"
                    PendingConfirmation = None } }

    let next, effects = Editor.update (KeyPressed(chr 'r')) model

    next.Recording |> should equal (Some 'c')
    Assert.Empty(next.Registers |> Map.find 'c')
    next.Prompt.Active |> should equal false
    Assert.Empty effects

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

    let next, _ =
        Editor.update (FileOpened("/x.txt", OpenPermanent, None, Result.Ok "a\r\nb\r\n")) model

    let buffer = next.Editors.Buffers[next.Editors.ActiveBufferId]
    (Buffer.text buffer).Contains "\r" |> should equal false
    Buffer.line 0 buffer |> should equal "a"
    Buffer.line 1 buffer |> should equal "b"
    buffer.Newline |> should equal "\r\n"

[<Fact>]
let ``FileOpened with lone CR normalizes to LF and saves as LF`` () =
    let model = initModel ()

    let next, _ =
        Editor.update (FileOpened("/x.txt", OpenPermanent, None, Result.Ok "a\rb\r")) model

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
    let tree: FileNode =
        { Path = "/root"
          Name = "root"
          IsDirectory = true
          Children =
            [ { Path = "/root/a.fs"
                Name = "a.fs"
                IsDirectory = false
                Children = [] }
              { Path = "/root/b.fs"
                Name = "b.fs"
                IsDirectory = false
                Children = [] } ] }

    let model = initModel ()

    let withTree =
        { model with
            Workspace = Workspace.setTree tree model.Workspace }

    let inSidebar, _ = Editor.update (KeyPressed(ck 'b')) withTree
    inSidebar.Focus |> should equal Sidebar
    // setTree selects the root; Down moves to the first child.
    inSidebar.Workspace.SelectedPath |> should equal (Some "/root")
    let moved, _ = Editor.update (KeyPressed(nk Down)) inSidebar
    moved.Workspace.SelectedPath |> should equal (Some "/root/a.fs")
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
let ``runAction MoveLinesDown delegates to the active buffer`` () =
    let model = modelWithLines 4
    let next, effects = Editor.runAction (MoveLinesDown 2) model

    Buffer.text (activeBuffer next) |> should equal "line1\nline2\nline0\nline3"
    effects |> should equal ([]: Effect list)

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
// reveal-in-sidebar + autoReveal
// ─────────────────────────────────────────────────────────────────────

/// /root/sub/deep/d.fs sits two collapsed directories below the
/// auto-expanded root.
let private nestedTree () : FileNode =
    { Path = "/root"
      Name = "root"
      IsDirectory = true
      Children =
        [ { Path = "/root/sub"
            Name = "sub"
            IsDirectory = true
            Children =
              [ { Path = "/root/sub/deep"
                  Name = "deep"
                  IsDirectory = true
                  Children =
                    [ { Path = "/root/sub/deep/d.fs"
                        Name = "d.fs"
                        IsDirectory = false
                        Children = [] } ] } ] } ] }

let private withNestedTree () =
    let model = initModel ()

    { model with
        Workspace = Workspace.setTree (nestedTree ()) model.Workspace }

[<Fact>]
let ``FileOpened reveals collapsed ancestors when autoReveal is on`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/sub/deep/d.fs", OpenPermanent, None, Result.Ok "x")) (withNestedTree ())

    opened.Workspace.SelectedPath |> should equal (Some "/root/sub/deep/d.fs")
    opened.Workspace.Expanded |> Set.contains "/root/sub" |> should equal true

    opened.Workspace.Expanded |> Set.contains "/root/sub/deep" |> should equal true

[<Fact>]
let ``FileOpened with autoReveal off keeps ancestors collapsed`` () =
    let model = withNestedTree ()

    let model =
        { model with
            Config = { model.Config with AutoReveal = false } }

    let opened, _ =
        Editor.update (FileOpened("/root/sub/deep/d.fs", OpenPermanent, None, Result.Ok "x")) model

    opened.Workspace.Expanded |> should equal model.Workspace.Expanded
    // selectPath's ensureSelected falls back to the first visible entry (the
    // root) because the deep path stays hidden under collapsed ancestors —
    // today's select-only behaviour, pinned.
    opened.Workspace.SelectedPath |> should equal (Some "/root")

[<Fact>]
let ``reveal-in-sidebar reveals the active file and focuses the sidebar`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/sub/deep/d.fs", OpenPermanent, None, Result.Ok "x")) (withNestedTree ())

    // Collapse everything back and hide the panel so the action has work to do.
    let collapsed =
        { opened with
            Panels =
                { opened.Panels with
                    SidebarVisible = false }
            Workspace =
                { opened.Workspace with
                    Expanded = Set.singleton "/root"
                    SelectedPath = Some "/root" } }

    let revealed, _ = Editor.update (KeyPressed(csk 'e')) collapsed
    revealed.Panels.SidebarVisible |> should equal true
    revealed.Focus |> should equal Sidebar
    revealed.Workspace.SelectedPath |> should equal (Some "/root/sub/deep/d.fs")

    revealed.Workspace.Expanded
    |> Set.contains "/root/sub/deep"
    |> should equal true

[<Fact>]
let ``reveal-in-sidebar on a scratch buffer notifies and changes nothing else`` () =
    let model = initModel ()
    let next, effects = Editor.runAction RevealInSidebar model
    effects |> should equal ([]: Effect list)

    next.Notification
    |> should equal (Some(Notification.info "Scratch buffer has no file to reveal."))

    // Only the notification changed — focus, panels, workspace, and buffers
    // are intact. (Model itself has no structural equality — the plugin
    // registry carries functions — so the fields are pinned one by one.)
    next.Focus |> should equal model.Focus
    next.Panels |> should equal model.Panels
    next.Workspace |> should equal model.Workspace
    next.Editors |> should equal model.Editors

[<Fact>]
let ``:reveal dispatches the action`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let opened, _ =
        Editor.update (FileOpened("/root/sub/deep/d.fs", OpenPermanent, None, Result.Ok "x")) (withNestedTree ())

    let collapsed =
        { opened with
            Workspace =
                { opened.Workspace with
                    Expanded = Set.singleton "/root"
                    SelectedPath = Some "/root" } }

    let submitted =
        "reveal"
        |> Seq.fold (fun m c -> press (chr c) m) (press (ck 'p') collapsed)
        |> press (nk Enter)

    submitted.Focus |> should equal Sidebar
    submitted.Workspace.SelectedPath |> should equal (Some "/root/sub/deep/d.fs")

    submitted.Workspace.Expanded
    |> Set.contains "/root/sub/deep"
    |> should equal true

// ─────────────────────────────────────────────────────────────────────
// wip #8 — Ctrl+arrows word motion (added alongside Alt+arrows)
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``Ctrl+Right moves forward by a word`` () =
    let model = withText "hello world"
    let home, _ = Editor.update (KeyPressed(nk Home)) model
    let viaCtrl, _ = Editor.update (KeyPressed(cnk Right)) home
    // Default WordMotion is WordEnd: from column 0 the cursor lands at the
    // end of "hello".
    (Editor.activeBufferState viaCtrl).Cursor.Column |> should equal 5

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
            PendingPrefix = Some [ kc 'k' ] }

    let next, _ = Editor.update SequenceTimedOut model
    next.PendingPrefix |> should equal (None: Chord list option)

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

// --- Prompt Tab/Enter interaction ---

[<Fact>]
let ``Enter on pending command with completions applies the first completion`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let model = initModel ()
    let opened = press (ck 'p') model
    let typed = press (chr 'o') opened

    typed.Prompt.Completions |> List.isEmpty |> should equal false
    typed.Prompt.Parsed |> should equal (Pending "Command is incomplete.")

    let entered = press (nk Enter) typed
    // Enter on a pending command with completions applies the first completion
    // instead of showing the pending message or executing
    entered.Prompt.Text |> should equal ":open"
    entered.Prompt.Parsed |> should equal (Pending "Path is required.")
    entered.Prompt.Active |> should equal true

[<Fact>]
let ``Tab then Enter on open command shows pending message instead of silent no-op`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let model = initModel ()
    let opened = press (ck 'p') model
    let typed = press (chr 'o') opened
    let tabbed = press (nk Tab) typed

    tabbed.Prompt.Text |> should equal ":open"
    tabbed.Prompt.Parsed |> should equal (Pending "Path is required.")

    let entered, effects = Editor.update (KeyPressed(nk Enter)) tabbed
    // After Tab completes `:open`, Enter should show the pending message
    // instead of silently re-applying the same completion.
    entered.Prompt.Active |> should equal true
    entered.Prompt.Text |> should equal ":open"

    entered.Notification
    |> should equal (Some(Notification.warning "Path is required."))

    effects |> should equal ([]: Effect list)

// ─────────────────────────────────────────────────────────────────────
// Config-file and plugin-validate effects — the I/O moved out of
// `update` into the EnsureConfigFile / ValidatePlugin interpreters.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``config command emits EnsureConfigFile instead of writing inline`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let opened = initModel () |> press (ck 'p')
    let typed = "config" |> Seq.fold (fun m c -> press (chr c) m) opened
    let _, effects = Editor.update (KeyPressed(nk Enter)) typed
    effects |> should equal [ EnsureConfigFile typed.Config ]

[<Fact>]
let ``ConfigFileReady Ok focuses the editor and loads the config file`` () =
    let model = initModel ()

    let next, effects =
        Editor.update (ConfigFileReady(Result.Ok "/root/config.json")) model

    next.Focus |> should equal Editor
    effects |> should equal [ LoadFile("/root/config.json", OpenPermanent, None) ]

[<Fact>]
let ``ConfigFileReady Error surfaces a warning notification`` () =
    let model = initModel ()
    let next, effects = Editor.update (ConfigFileReady(Result.Error "disk full")) model

    next.Notification
    |> should equal (Some(Notification.warning "Could not create config file: disk full"))

    effects |> should equal ([]: Effect list)

[<Fact>]
let ``plugin validate command emits a ValidatePlugin effect`` () =
    let press chord m =
        fst (Editor.update (KeyPressed chord) m)

    let opened = initModel () |> press (ck 'p')

    let typed =
        "plugin validate /tmp/demo"
        |> Seq.fold (fun m c -> press (if c = ' ' then nk Space else chr c) m) opened

    let _, effects = Editor.update (KeyPressed(nk Enter)) typed
    effects |> should equal [ ValidatePlugin "/tmp/demo" ]

[<Fact>]
let ``PluginValidated Ok surfaces the report as an info notification`` () =
    let report = "OK: demo 1.0.0 (apiVersion 1); entryType=Demo.Plugin"
    let next, effects = Editor.update (PluginValidated(Result.Ok report)) (initModel ())
    next.Notification |> should equal (Some(Notification.info report))
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``PluginValidated Error surfaces the report as an error notification`` () =
    let report = "No plugin.json found in /tmp/demo."

    let next, effects =
        Editor.update (PluginValidated(Result.Error report)) (initModel ())

    next.Notification |> should equal (Some(Notification.error report))
    effects |> should equal ([]: Effect list)

// ─────────────────────────────────────────────────────────────────────
// Mouse pipeline — click-to-position, drag-to-select, release. Editor
// coordinates derive from Dock.metrics so the tests can't drift from
// the painted layout.
// ─────────────────────────────────────────────────────────────────────

let private mouseEvent button action line column : MouseEvent =
    { Button = button
      Action = action
      Position = { Line = line; Column = column }
      Modifiers = Set.empty }

/// Screen column of the first text cell (right of sidebar + gutter).
let private textAreaX (model: Model) =
    (Dock.metrics model).EditorX + Buffer.gutterWidth (activeBuffer model)

[<Fact>]
let ``MousePressed in the text area moves the cursor to the clicked cell`` () =
    let model = modelWithLines 10 // lines "line0".."line9"
    let contentX = textAreaX model

    let next, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press 2 (contentX + 3))) model

    (activeBuffer next).Cursor |> should equal { Line = 2; Column = 3 }
    next.Focus |> should equal Editor

    let expectedDrag =
        { AnchorBufferId = 1
          AnchorPosition = { Line = 2; Column = 3 } }

    next.MouseDrag |> should equal (Some expectedDrag)

[<Fact>]
let ``MousePressed then MouseDragged selects between press and drag cells`` () =
    let model = modelWithLines 10
    let contentX = textAreaX model

    let pressed, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press 0 contentX)) model

    let dragged, _ =
        Editor.update (MouseDragged(mouseEvent LeftButton Drag 1 (contentX + 2))) pressed

    let buffer = activeBuffer dragged
    // Inclusive drag: the cursor lands one cell past the hovered "n" so its
    // character is selected, not left at the cell's left edge.
    buffer.Cursor |> should equal { Line = 1; Column = 3 }
    // Anchor at the press cell (index 0), head one past the drag cell
    // ("line0\n" = 6 chars, +2 = cell 8, +1 to include it = 9).
    Buffer.selectionRange buffer |> should equal (Some(0, 9))

[<Fact>]
let ``MouseDragged across a word selects the trailing character`` () =
    let model = initModel ()
    let buf = Buffer.fromText 1 None "test" "hello world" "\n"

    let model =
        { model with
            Editors =
                { model.Editors with
                    Buffers = Map.ofList [ 1, buf ] } }

    let contentX = textAreaX model

    // Press on "h" (cell 0), drag onto the last "o" of "hello" (cell 4).
    let pressed, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press 0 contentX)) model

    let dragged, _ =
        Editor.update (MouseDragged(mouseEvent LeftButton Drag 0 (contentX + 4))) pressed

    let buffer = activeBuffer dragged
    Buffer.selectionText buffer |> should equal "hello"
    Buffer.selectionRange buffer |> should equal (Some(0, 5))

[<Fact>]
let ``MouseReleased clears the drag anchor so later drags do not extend`` () =
    let model = modelWithLines 10
    let contentX = textAreaX model

    let pressed, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press 0 contentX)) model

    pressed.MouseDrag |> Option.isSome |> should equal true

    let released, _ =
        Editor.update (MouseReleased(mouseEvent LeftButton Release 0 contentX)) pressed

    released.MouseDrag |> should equal (None: MouseDragState option)

    // With no anchor a drag is a no-op — the selection cannot extend.
    let draggedAfter, _ =
        Editor.update (MouseDragged(mouseEvent LeftButton Drag 2 (contentX + 3))) released

    draggedAfter |> should equal released

[<Fact>]
let ``MousePressed in the gutter or empty sidebar leaves the model unchanged`` () =
    let model = modelWithLines 10
    let contentX = textAreaX model

    // Last gutter cell, just left of the text area.
    let inGutter, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press 0 (contentX - 1))) model

    inGutter |> should equal model

    // Inside the sidebar with no workspace tree (no entry under the click):
    // no cursor move, no focus change, no drag anchor.
    let inSidebar, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press 0 0)) model

    inSidebar |> should equal model

// ─────────────────────────────────────────────────────────────────────
// Detached selection — the span lives independently of the cursor, so a
// viewport-led wheel scroll (which drags the cursor for scrolloff) can
// never silently grow a selection.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``wheel scroll preserves an existing selection exactly`` () =
    // default config: ScrollViewport, scrolloff 5, 3 lines/tick; height 24-8-2 = 14
    let model = modelWithLines 100

    let selected =
        [ snk Down; snk Down ]
        |> List.fold (fun m c -> fst (Editor.update (KeyPressed c) m)) model

    let before = Buffer.selectionRange (activeBuffer selected)
    before |> should equal (Some(0, 12)) // two "lineN\n" rows of 6 chars

    let scrolled, _ = Editor.update (MouseScrolled(2, inEditor)) selected
    let buffer = activeBuffer scrolled
    buffer.ViewportTop |> should equal 6
    // The cursor was dragged into the scrolloff band (top + 5)...
    buffer.Cursor.Line |> should equal 11
    // ...but the selection span did not move with it.
    Buffer.selectionRange buffer |> should equal before

[<Fact>]
let ``wheel scroll while dragging keeps the drag anchor`` () =
    let model = modelWithLines 100
    let contentX = textAreaX model

    let pressed, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press 0 contentX)) model

    let scrolled, _ = Editor.update (MouseScrolled(1, inEditor)) pressed
    (activeBuffer scrolled).ViewportTop |> should equal 3

    // Drag onto visible row 2 → buffer line 3 + 2 = 5, column 3.
    let dragged, _ =
        Editor.update (MouseDragged(mouseEvent LeftButton Drag 2 (contentX + 3))) scrolled

    let buffer = activeBuffer dragged
    // Inclusive drag: cursor lands one cell past the hovered character.
    buffer.Cursor |> should equal { Line = 5; Column = 4 }
    // Press anchor (index 0) → one past the drag cell: five "lineN\n" rows of
    // 6 chars + 3 = cell 33, +1 to include the hovered character = 34.
    Buffer.selectionRange buffer |> should equal (Some(0, 34))

[<Fact>]
let ``shift+motion after a detached scroll extends from the selection head`` () =
    let model = modelWithLines 100
    let sel1, _ = Editor.update (KeyPressed(snk Right)) model
    Buffer.selectionRange (activeBuffer sel1) |> should equal (Some(0, 1))

    // One wheel tick: viewport to 3, cursor dragged to the scrolloff band.
    let scrolled, _ = Editor.update (MouseScrolled(1, inEditor)) sel1
    (activeBuffer scrolled).Cursor.Line |> should equal 8

    let sel2, _ = Editor.update (KeyPressed(snk Right)) scrolled
    let buffer = activeBuffer sel2
    Buffer.selectionRange buffer |> should equal (Some(0, 2))
    // The cursor snapped back to the head before extending — it did not
    // extend from the drifted line-8 position.
    buffer.Cursor |> should equal { Line = 0; Column = 2 }

[<Fact>]
let ``typing after a detached scroll replaces the selection`` () =
    let model = modelWithLines 100
    let sel, _ = Editor.update (KeyPressed(snk Right)) model
    let scrolled, _ = Editor.update (MouseScrolled(1, inEditor)) sel
    (activeBuffer scrolled).Cursor.Line |> should equal 8

    let typed, _ = Editor.update (KeyPressed(chr 'x')) scrolled
    let buffer = activeBuffer typed
    // The span (0,1) was replaced — not a range up to the drifted cursor.
    Buffer.line 0 buffer |> should equal "xine0"
    Buffer.line 1 buffer |> should equal "line1"
    buffer.Selection |> should equal None
    buffer.Cursor |> should equal { Line = 0; Column = 1 }

// ─────────────────────────────────────────────────────────────────────
// Preview buffer — the single VSCode-style reusable slot. Space in the
// sidebar peeks a file (focus stays put); editing or Enter promotes it;
// a second preview replaces the slot's content under the same id.
// ─────────────────────────────────────────────────────────────────────

/// Sidebar-focused model whose workspace tree holds `files` under /root,
/// with the first file selected. Mirrors the Space-in-sidebar fixture.
let private sidebarModelWithFiles (files: string list) =
    let tree: FileNode =
        { Path = "/root"
          Name = "root"
          IsDirectory = true
          Children =
            files
            |> List.map (fun name ->
                { Path = $"/root/{name}"
                  Name = name
                  IsDirectory = false
                  Children = [] }) }

    let model = initModel ()

    { model with
        Focus = Sidebar
        Workspace =
            model.Workspace
            |> Workspace.setTree tree
            |> Workspace.selectPath $"/root/{List.head files}" }

[<Fact>]
let ``space on a sidebar file emits a preview load`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]
    let _, effects = Editor.update (KeyPressed(nk Space)) model
    effects |> should equal [ LoadFile("/root/a.fs", OpenPreview, None) ]

[<Fact>]
let ``space with a type-ahead query stays a search character`` () =
    let model = sidebarModelWithFiles [ "my file.fs" ]
    let m, _ = Editor.update (KeyPressed(chr 'm')) model
    let y, _ = Editor.update (KeyPressed(chr 'y')) m
    let spaced, effects = Editor.update (KeyPressed(nk Space)) y
    spaced.Workspace.SearchBuffer |> should equal "my "
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``preview FileOpened creates the preview buffer and keeps focus`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]

    let next, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPreview, None, Result.Ok "x")) model

    next.Editors.PreviewBufferId |> should equal (Some next.Editors.ActiveBufferId)

    next.Editors.ActiveBufferId |> should not' (equal model.Editors.ActiveBufferId)
    next.Focus |> should equal Sidebar

[<Fact>]
let ``a second preview reuses the buffer id`` () =
    let model = sidebarModelWithFiles [ "a.fs"; "b.fs" ]

    let first, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPreview, None, Result.Ok "alpha")) model

    let previewId = first.Editors.ActiveBufferId

    // Seed a highlight entry so the slot-reuse path provably drops it.
    let seeded =
        { first with
            HighlightStates = first.HighlightStates |> Map.add previewId [||] }

    let second, _ =
        Editor.update (FileOpened("/root/b.fs", OpenPreview, None, Result.Ok "beta")) seeded

    second.Editors.ActiveBufferId |> should equal previewId
    second.Editors.PreviewBufferId |> should equal (Some previewId)
    second.Editors.Buffers.Count |> should equal first.Editors.Buffers.Count

    let buffer = second.Editors.Buffers[previewId]
    buffer.FilePath |> should equal (Some "/root/b.fs")
    Buffer.text buffer |> should equal "beta"
    Assert.Empty buffer.Undo
    second.HighlightStates.ContainsKey previewId |> should equal false

[<Fact>]
let ``editing the preview promotes it`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]

    let previewed, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPreview, None, Result.Ok "x")) model

    let typed, _ = Editor.update (KeyPressed(chr 'y')) { previewed with Focus = Editor }

    typed.Editors.PreviewBufferId |> should equal (None: int option)

[<Fact>]
let ``enter on the previewed file promotes it without reloading`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]

    let previewed, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPreview, None, Result.Ok "x")) model

    previewed.Focus |> should equal Sidebar
    let next, effects = Editor.update (KeyPressed(nk Enter)) previewed

    next.Editors.PreviewBufferId |> should equal (None: int option)
    next.Editors.ActiveBufferId |> should equal previewed.Editors.ActiveBufferId
    next.Focus |> should equal Editor
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``space on a file open in a normal buffer activates it without previewing`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]

    let opened, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPermanent, None, Result.Ok "x")) model

    let inSidebar = { opened with Focus = Sidebar }
    let next, effects = Editor.update (KeyPressed(nk Space)) inSidebar

    next.Editors.PreviewBufferId |> should equal (None: int option)
    next.Editors.ActiveBufferId |> should equal opened.Editors.ActiveBufferId
    next.Focus |> should equal Sidebar
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``space on the previewed file is a no-op activation`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]

    let previewed, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPreview, None, Result.Ok "x")) model

    let next, effects = Editor.update (KeyPressed(nk Space)) previewed

    next.Editors.PreviewBufferId |> should equal previewed.Editors.PreviewBufferId
    next.Editors.ActiveBufferId |> should equal previewed.Editors.ActiveBufferId
    next.Focus |> should equal Sidebar
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``FileOpened OpenPermanent of an already-open path activates instead of duplicating`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]

    let opened, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPermanent, None, Result.Ok "x")) model

    let again, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPermanent, None, Result.Ok "x")) opened

    again.Editors.Buffers.Count |> should equal opened.Editors.Buffers.Count
    again.Editors.ActiveBufferId |> should equal opened.Editors.ActiveBufferId

// ─────────────────────────────────────────────────────────────────────
// Sidebar mouse — click selects, click-on-selected activates (dir →
// toggle expand, file → preview peek), wheel moves the tree selection.
// Row→entry mapping goes through Dock.sidebarRows, the same pass the
// painter uses, so hit-testing can't drift from what's on screen.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``wheel over the sidebar moves the tree selection by ticks times mouseScrollLines`` () =
    // Five root files, all visible: root row 0, a.fs..e.fs rows 1..5.
    let model =
        { sidebarModelWithFiles [ "a.fs"; "b.fs"; "c.fs"; "d.fs"; "e.fs" ] with
            Focus = Editor }

    let before = activeBuffer model

    let next, effects = Editor.update (MouseScrolled(1, { Line = 2; Column = 1 })) model

    // a.fs (entry 1) + 1 tick × 3 lines/tick (default) = d.fs (entry 4).
    next.Workspace.SelectedPath |> should equal (Some "/root/d.fs")
    // The wheel routed to the tree: the editor viewport did not move...
    (activeBuffer next).ViewportTop |> should equal before.ViewportTop
    // ...and the focus stayed where it was.
    next.Focus |> should equal Editor
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``click on an unselected sidebar row selects it and focuses the sidebar`` () =
    let model =
        { sidebarModelWithFiles [ "a.fs"; "b.fs"; "c.fs" ] with
            Focus = Editor }

    let entries, startIndex = Dock.sidebarRows model (Dock.metrics model).MainHeight
    let idx = entries |> List.findIndex (fun entry -> entry.Path = "/root/c.fs")

    let next, effects =
        Editor.update (MousePressed(mouseEvent LeftButton Press (idx - startIndex) 1)) model

    next.Workspace.SelectedPath |> should equal (Some "/root/c.fs")
    next.Focus |> should equal Sidebar
    effects |> should equal ([]: Effect list)

[<Fact>]
let ``click on the selected directory row toggles its expansion`` () =
    let tree: FileNode =
        { Path = "/root"
          Name = "root"
          IsDirectory = true
          Children =
            [ { Path = "/root/src"
                Name = "src"
                IsDirectory = true
                Children =
                  [ { Path = "/root/src/a.fs"
                      Name = "a.fs"
                      IsDirectory = false
                      Children = [] } ] } ] }

    let model = initModel ()

    let model =
        { model with
            Workspace = model.Workspace |> Workspace.setTree tree |> Workspace.selectPath "/root/src" }

    model.Workspace.Expanded |> Set.contains "/root/src" |> should equal false

    let entries, startIndex = Dock.sidebarRows model (Dock.metrics model).MainHeight

    let row =
        (entries |> List.findIndex (fun entry -> entry.Path = "/root/src")) - startIndex

    let expanded, effects =
        Editor.update (MousePressed(mouseEvent LeftButton Press row 1)) model

    expanded.Workspace.Expanded |> Set.contains "/root/src" |> should equal true
    expanded.Focus |> should equal Sidebar
    effects |> should equal ([]: Effect list)

    // The row is still selected, so a second click collapses it again.
    let collapsed, _ =
        Editor.update (MousePressed(mouseEvent LeftButton Press row 1)) expanded

    collapsed.Workspace.Expanded |> Set.contains "/root/src" |> should equal false

[<Fact>]
let ``click on the selected file row opens it as a preview`` () =
    let model = sidebarModelWithFiles [ "a.fs" ]
    let entries, startIndex = Dock.sidebarRows model (Dock.metrics model).MainHeight

    let row =
        (entries |> List.findIndex (fun entry -> entry.Path = "/root/a.fs"))
        - startIndex

    let next, effects =
        Editor.update (MousePressed(mouseEvent LeftButton Press row 1)) model

    effects |> should equal [ LoadFile("/root/a.fs", OpenPreview, None) ]
    next.Focus |> should equal Sidebar

[<Fact>]
let ``sidebar row mapping honours the centered scroll origin`` () =
    // Enough entries that the selection-centering scroll kicks in: 41
    // visible entries on a 22-row main area with a deep selection pins
    // the window to the tree's tail (startIndex > 0).
    let files = [ for i in 0..39 -> sprintf "f%02d.fs" i ]

    let model =
        let m = sidebarModelWithFiles files

        { m with
            Workspace = Workspace.selectPath "/root/f35.fs" m.Workspace }

    let entries, startIndex = Dock.sidebarRows model (Dock.metrics model).MainHeight
    startIndex > 0 |> should equal true

    // Screen row 0 is the entry painted at the scroll origin, not entry 0.
    let next, _ = Editor.update (MousePressed(mouseEvent LeftButton Press 0 1)) model
    next.Workspace.SelectedPath |> should equal (Some entries[startIndex].Path)

[<Fact>]
let ``click below the last sidebar entry does nothing`` () =
    // Two entries (root + a.fs); row 20 is inside the sidebar but past
    // the last painted row.
    let model = sidebarModelWithFiles [ "a.fs" ]

    let next, effects =
        Editor.update (MousePressed(mouseEvent LeftButton Press 20 1)) model

    next |> should equal model
    effects |> should equal ([]: Effect list)

// ─────────────────────────────────────────────────────────────────────
// Bracketed paste (PastedText): one undo entry, selection replacement,
// focus-aware routing (editor / prompt first line / sidebar ignored).
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``PastedText inserts as a single undo entry`` () =
    let next, _ = Editor.update (PastedText "a\r\nb") (initModel ())

    // Newlines normalize to the LF-only document invariant.
    Buffer.text (activeBuffer next) |> should equal "a\nb"

    // One insertText call = one undo entry: a single undo restores empty.
    let undone, _ = Editor.update (KeyPressed(ck 'z')) next
    Buffer.text (activeBuffer undone) |> should equal ""

[<Fact>]
let ``PastedText replaces a selection`` () =
    let model = withText "abc"
    let selected, _ = Editor.update (KeyPressed(ck 'a')) model
    let next, _ = Editor.update (PastedText "xy") selected

    Buffer.text (activeBuffer next) |> should equal "xy"
    (activeBuffer next).Selection |> should equal (None: SelectionSpan option)

[<Fact>]
let ``PastedText into the prompt inserts only the first line`` () =
    let opened, _ = Editor.update (KeyPressed(ck 'p')) (initModel ())
    opened.Focus |> should equal Prompt

    let next, _ = Editor.update (PastedText "theme x\nquit") opened

    // Single-line input: only the first line lands in the prompt text; the
    // newline must not act as Enter, so nothing executes and the prompt
    // stays open.
    next.Prompt.Active |> should equal true
    next.Prompt.Text |> should equal (opened.Prompt.Text + "theme x")
    next.ShouldQuit |> should equal false
    Buffer.text (activeBuffer next) |> should equal ""

[<Fact>]
let ``PastedText with sidebar focus is ignored`` () =
    let model = { initModel () with Focus = Sidebar }
    let next, effects = Editor.update (PastedText "hello") model

    Buffer.text (activeBuffer next) |> should equal ""
    next.Focus |> should equal Sidebar
    effects |> should equal ([]: Effect list)

// ─────────────────────────────────────────────────────────────────────
// Quit guard shared across routes (Ctrl+Q, `:quit`, `quit force`) and
// close-buffer lifecycle (two-step dirty confirm, last-buffer scratch,
// MRU fallback activation).
// ─────────────────────────────────────────────────────────────────────

let private pressKey chord m =
    fst (Editor.update (KeyPressed chord) m)

/// Open the palette on an existing model, type `text`, and press Enter.
let private runCommandText (text: string) model =
    let opened = pressKey (ck 'p') model
    text |> Seq.fold (fun m c -> pressKey (chr c) m) opened |> pressKey (nk Enter)

let private notificationMessage (model: Model) =
    model.Notification |> Option.map (fun n -> n.Message)

[<Fact>]
let ``palette quit with a dirty buffer arms instead of quitting`` () =
    let next = runCommandText "quit" (withText "x")

    next.ShouldQuit |> should equal false
    next.QuitArmed |> should equal true

    notificationMessage next
    |> should equal (Some "Unsaved changes in scratch. Quit again to discard.")

[<Fact>]
let ``second palette quit quits despite dirty buffers`` () =
    let armed = runCommandText "quit" (withText "x")
    let final = runCommandText "quit" armed
    final.ShouldQuit |> should equal true

[<Fact>]
let ``quit force quits immediately despite dirty buffers`` () =
    let final = runCommandText "quit force" (withText "x")
    final.ShouldQuit |> should equal true

[<Fact>]
let ``Ctrl+q pressed twice with a dirty buffer quits`` () =
    let armed = pressKey (ck 'q') (withText "x")
    armed.ShouldQuit |> should equal false

    let final = pressKey (ck 'q') armed
    final.ShouldQuit |> should equal true

[<Fact>]
let ``a non-quit key disarms the quit warning`` () =
    let armed = pressKey (ck 'q') (withText "x")
    let moved = pressKey (nk Right) armed
    moved.QuitArmed |> should equal false

    // The next quit warns again instead of discarding.
    let rewarned = pressKey (ck 'q') moved
    rewarned.ShouldQuit |> should equal false
    rewarned.QuitArmed |> should equal true

[<Fact>]
let ``quit warning names up to three dirty buffers then counts the rest`` () =
    let model = initModel ()

    let dirtyBuffer id name =
        { Buffer.fromText id None name "x" "\n" with
            Dirty = true }

    let seeded =
        { model with
            Editors =
                { model.Editors with
                    Buffers =
                        Map.ofList
                            [ for i, name in List.indexed [ "a.fs"; "b.fs"; "c.fs"; "d.fs"; "e.fs" ] ->
                                  i + 1, dirtyBuffer (i + 1) name ] } }

    let warned = pressKey (ck 'q') seeded

    notificationMessage warned
    |> should equal (Some "Unsaved changes in a.fs, b.fs, c.fs +2 more. Quit again to discard.")

[<Fact>]
let ``close-buffer activates the most recently active buffer`` () =
    let model = initModel ()

    let openedA, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPermanent, None, Result.Ok "a")) model

    let openedB, _ =
        Editor.update (FileOpened("/root/b.fs", OpenPermanent, None, Result.Ok "b")) openedA

    // Buffers: 1 scratch, 2 a.fs, 3 b.fs (active). Jump to the scratch,
    // then close it: the fallback must be the most recently active b.fs
    // (3), not the adjacent a.fs (2).
    let onScratch = pressKey (ck '1') openedB
    onScratch.Editors.ActiveBufferId |> should equal 1

    let closed = pressKey (ck 'w') onScratch
    closed.Editors.Buffers.Count |> should equal 2
    closed.Editors.Buffers.ContainsKey 1 |> should equal false
    closed.Editors.ActiveBufferId |> should equal 3

[<Fact>]
let ``closing the last buffer leaves a fresh scratch`` () =
    let closed = pressKey (ck 'w') (initModel ())

    closed.Editors.Buffers.Count |> should equal 1
    closed.Editors.ActiveBufferId |> should equal 2

    let scratch = Editor.activeBufferState closed
    scratch.Dirty |> should equal false
    Buffer.text scratch |> should equal ""

[<Fact>]
let ``Ctrl+w on a dirty buffer arms then closes on repeat`` () =
    let warned = pressKey (ck 'w') (withText "x")

    warned.Editors.Buffers.Count |> should equal 1
    warned.CloseArmed |> should equal (Some 1)

    notificationMessage warned
    |> should equal (Some "Unsaved changes in scratch. Close again to discard.")

    let closed = pressKey (ck 'w') warned
    closed.CloseArmed |> should equal (None: int option)
    closed.Editors.ActiveBufferId |> should equal 2
    Buffer.text (Editor.activeBufferState closed) |> should equal ""

[<Fact>]
let ``a non-close key disarms the close warning`` () =
    let warned = pressKey (ck 'w') (withText "x")
    let moved = pressKey (nk Right) warned
    moved.CloseArmed |> should equal (None: int option)

    // The next close warns again instead of discarding.
    let rewarned = pressKey (ck 'w') moved
    rewarned.Editors.Buffers.Count |> should equal 1
    rewarned.CloseArmed |> should equal (Some 1)

[<Fact>]
let ``palette close on a dirty buffer arms then closes on repeat`` () =
    let warned = runCommandText "close" (withText "x")
    warned.Editors.Buffers.Count |> should equal 1
    warned.CloseArmed |> should equal (Some 1)

    let closed = runCommandText "close" warned
    closed.Editors.ActiveBufferId |> should equal 2
    Buffer.text (Editor.activeBufferState closed) |> should equal ""

[<Fact>]
let ``palette close by name closes the named buffer without switching`` () =
    let model = initModel ()

    let openedA, _ =
        Editor.update (FileOpened("/root/a.fs", OpenPermanent, None, Result.Ok "a")) model

    let openedB, _ =
        Editor.update (FileOpened("/root/b.fs", OpenPermanent, None, Result.Ok "b")) openedA

    let closed = runCommandText "close a.fs" openedB

    closed.Editors.Buffers
    |> Map.exists (fun _ buffer -> buffer.Name = "a.fs")
    |> should equal false

    closed.Editors.ActiveBufferId |> should equal openedB.Editors.ActiveBufferId

[<Fact>]
let ``palette close with an unknown name reports an error`` () =
    let next = runCommandText "close nosuch" (initModel ())
    next.Editors.Buffers.Count |> should equal 1
    (notificationMessage next).Value.Contains "Unknown buffer" |> should equal true

[<Fact>]
let ``buffer switcher completions mark dirty buffers`` () =
    let prompt = withText "x" |> pressKey (ck 'o') |> pressKey (chr '@')

    prompt.Prompt.Completions
    |> List.map (fun item -> item.Label)
    |> should contain "1  scratch [+]"
