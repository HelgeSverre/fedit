module Fedit.Tests.ScreenTests

open Fedit
open Xunit
open FsUnit.Xunit

// Regression: a control character must never reach a screen cell. The renderer
// emits each cell glyph raw and tracks the cursor as advancing exactly one
// column per cell, so a literal '\n' byte (e.g. from a multi-line `:keybind`
// notification rendered into the single-row status line) desyncs the cursor and
// corrupts the TUI until a full repaint. The grid invariant is "every cell holds
// one printable column", enforced at the setCell choke point.

[<Fact>]
let ``setCell coerces a control-character glyph to a space`` () =
    let screen = Screen.create 4 1
    Screen.setCell 0 0 Style.defaultStyle '\n' screen
    screen.Cells[0, 0].Glyph |> should equal ' '

[<Fact>]
let ``writeText never stores control characters as cell glyphs`` () =
    let screen = Screen.create 10 1
    Screen.writeText 0 0 Style.defaultStyle 10 "ab\ncd\te" screen

    for x in 0..9 do
        Assert.False(System.Char.IsControl(screen.Cells[0, x].Glyph))

[<Fact>]
let ``a multi-line notification renders without control glyphs`` () =
    // Reproduces the `:keybind` listing: a multi-line body shown via the
    // [NOTIFICATION] status token. Pre-fix, the embedded newlines land in the
    // status row as control glyphs and corrupt the terminal.
    let model, _ =
        Editor.init "/root" { Width = 120; Height = 12 } (Config.defaults Themes.defaultTheme) [] None

    let model =
        { model with
            Notification = Some(Notification.info "global ctrl+q Quit\neditor ctrl+s Save\nsidebar enter Open") }

    let screen = Layout.render model

    let controlCells =
        [ for row in 0 .. screen.Height - 1 do
              for col in 0 .. screen.Width - 1 do
                  let glyph = screen.Cells[row, col].Glyph

                  if System.Char.IsControl glyph then
                      yield (row, col, int glyph) ]

    Assert.True(List.isEmpty controlCells, sprintf "control glyphs leaked into cells: %A" controlCells)
