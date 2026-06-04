namespace Fedit

open System

type Color =
    | Default
    /// ANSI 256-color cube index (0–15 standard, 16–231 cube, 232–255 ramp).
    | Indexed of byte
    /// Truecolor RGB (24-bit). Renderer emits `38;2;r;g;b` directly; modern
    /// terminals support this. Use `Color.toIndexed` to quantize when
    /// targeting a 256-only terminal.
    | Rgb of red: byte * green: byte * blue: byte

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
            // Grid invariant: a cell holds exactly one printable column. The
            // renderer emits glyphs raw and tracks the cursor as advancing one
            // column per cell, so a control character (notably '\n' from a
            // multi-line notification) would emit a literal control byte and
            // desync the terminal. Coerce any control char to a space.
            let safe = if System.Char.IsControl glyph then ' ' else glyph
            screen.Cells[y, x] <- { Glyph = safe; Style = style }

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
