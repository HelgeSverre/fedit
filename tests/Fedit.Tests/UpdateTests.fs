module Fedit.Tests.UpdateTests

open Fedit
open Xunit
open FsUnit.Xunit

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } Themes.defaultTheme [] []

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
        Editor.init "/root" { Width = 80; Height = 24 } Themes.defaultTheme [] []

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
