namespace Fedit

open System

type Color =
    | Default
    | Indexed of int

[<Struct>]
type Style =
    { Foreground: Color
      Background: Color
      Bold: bool
      Inverted: bool }

[<Struct>]
type Cell = { Glyph: char; Style: Style }

[<Struct>]
type Cursor = { Left: int; Top: int; Visible: bool }

type Screen =
    { Width: int
      Height: int
      Cells: Cell[,]
      Cursor: Cursor option }

[<RequireQualifiedAccess>]
module Style =
    let defaultStyle =
        { Foreground = Default
          Background = Default
          Bold = false
          Inverted = false }

    let withColors foreground background =
        { defaultStyle with
            Foreground = foreground
            Background = background }

[<RequireQualifiedAccess>]
module Screen =
    let private blank =
        { Glyph = ' '
          Style = Style.defaultStyle }

    let create width height =
        { Width = max 1 width
          Height = max 1 height
          Cells = Array2D.create (max 1 height) (max 1 width) blank
          Cursor = None }

    let private inBounds x y screen =
        x >= 0 && y >= 0 && x < screen.Width && y < screen.Height

    let setCell x y style glyph screen =
        if inBounds x y screen then
            screen.Cells[y, x] <- { Glyph = glyph; Style = style }

    let writeText x y style maxWidth (text: string) screen =
        if y >= 0 && y < screen.Height && maxWidth > 0 then
            text
            |> Seq.truncate maxWidth
            |> Seq.iteri (fun index glyph -> setCell (x + index) y style glyph screen)

    let fillRect x y width height style glyph screen =
        for row in y .. y + height - 1 do
            for col in x .. x + width - 1 do
                setCell col row style glyph screen

    let drawVerticalLine x y height style glyph screen =
        fillRect x y 1 height style glyph screen

    let withCursor cursor (screen: Screen) = { screen with Cursor = Some cursor }
