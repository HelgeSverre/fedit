module Fedit.Tests.UpdateTests

open Fedit
open Xunit
open FsUnit.Xunit

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

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
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

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
