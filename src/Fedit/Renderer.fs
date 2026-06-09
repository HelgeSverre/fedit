namespace Fedit

open System.IO
open System.Text

/// Pure screen-diffing → ANSI renderer. All terminal state (capabilities,
/// enabled modes) lives in the Terminal layer; this module only knows how
/// to turn a `Screen` into bytes.
[<RequireQualifiedAccess>]
module Renderer =
    let private esc = "\u001b"
    let private clearScreen = $"{esc}[2J"
    let private homeCursor = $"{esc}[H"
    let private showCursor = $"{esc}[?25h"
    let private hideCursor = $"{esc}[?25l"

    let private cursorPosition row col = $"{esc}[{row + 1};{col + 1}H"

    /// Map a 256-cube index to the nearest standard ANSI 16 color.
    /// The cube is 6×6×6; we re-quantize to 4 coarse levels and map to
    /// the 8 standard colors (0-7) or bright variants (8-15).
    let private ansi16Of (idx: byte) =
        let i = int idx

        if i < 16 then
            idx
        else
            let r, g, b = Color.cubeRgb idx

            let coarse (v: int) =
                if v < 64 then 0
                elif v < 128 then 1
                elif v < 192 then 2
                else 3

            let bright = if r > 128 || g > 128 || b > 128 then 8 else 0

            let ansi =
                match coarse r, coarse g, coarse b with
                | 0, 0, 0 -> 0
                | 3, 0, 0 -> 1
                | 0, 3, 0 -> 2
                | 3, 3, 0 -> 3
                | 0, 0, 3 -> 4
                | 3, 0, 3 -> 5
                | 0, 3, 3 -> 6
                | 3, 3, 3 -> 7
                | _ -> 7

            byte (ansi + bright)

    /// Downgrade a color to match the terminal's claimed color support.
    let private downgradeColor (colorSupport: ColorSupport) color =
        match colorSupport with
        | ColorTrueColor -> color
        | ColorAnsi256 ->
            match color with
            | Rgb _ ->
                match Color.toIndexed color with
                | Some idx -> Indexed idx
                | None -> color
            | _ -> color
        | ColorAnsi16 ->
            match color with
            | Default -> Default
            | Indexed n when n < 16uy -> color
            | Indexed _
            | Rgb _ ->
                match Color.toIndexed color with
                | Some idx -> Indexed(ansi16Of idx)
                | None -> Default
        | ColorNone -> Default

    let private colorToAnsiCode isForeground color =
        match color with
        | Default -> if isForeground then "39" else "49"
        | Indexed value -> if isForeground then $"38;5;{value}" else $"48;5;{value}"
        | Rgb(r, g, b) ->
            if isForeground then
                $"38;2;{r};{g};{b}"
            else
                $"48;2;{r};{g};{b}"

    let private styleToAnsiSequence colorSupport style =
        let parts =
            [ "0"
              if style.Bold then
                  "1"
              if style.Inverted then
                  "7"
              colorToAnsiCode true (downgradeColor colorSupport style.Foreground)
              colorToAnsiCode false (downgradeColor colorSupport style.Background) ]

        let sequence = String.concat ";" parts

        $"{esc}[{sequence}m"

    let private append (builder: StringBuilder) (text: string) = builder.Append(text) |> ignore

    let private appendChar (builder: StringBuilder) (glyph: char) = builder.Append(glyph) |> ignore

    /// Diff cells between `previous` and `next`; emit cursor jumps and
    /// style changes only for cells that actually changed. Style is
    /// tracked across rows so unchanged styles don't re-emit when crossing
    /// a row boundary.
    let private appendDiffedCells
        (builder: StringBuilder)
        (colorSupport: ColorSupport)
        (previous: Screen voption)
        (next: Screen)
        =
        let sameDimensions =
            match previous with
            | ValueSome p -> p.Width = next.Width && p.Height = next.Height
            | ValueNone -> false

        let sameAsPrev row col =
            if not sameDimensions then
                false
            else
                match previous with
                | ValueSome p -> p.Cells[row, col] = next.Cells[row, col]
                | ValueNone -> false

        let mutable currentStyle: string voption = ValueNone
        let mutable lastRow = -2
        let mutable lastCol = -2

        for row in 0 .. next.Height - 1 do
            for col in 0 .. next.Width - 1 do
                if not (sameAsPrev row col) then
                    let cell = next.Cells[row, col]
                    let style = styleToAnsiSequence colorSupport cell.Style

                    if row <> lastRow || col <> lastCol + 1 then
                        append builder (cursorPosition row col)

                    if currentStyle <> ValueSome style then
                        append builder style
                        currentStyle <- ValueSome style

                    appendChar builder cell.Glyph
                    lastRow <- row
                    lastCol <- col

    let private appendCursor (builder: StringBuilder) (screen: Screen) =
        match screen.Cursor with
        | Some cursor when cursor.Visible ->
            append builder showCursor
            append builder (cursorPosition cursor.Top cursor.Left)
        | _ -> append builder hideCursor

    /// Render `next` to the writer. If `previous` is `ValueSome` and has
    /// the same dimensions, only cells that differ are written. On size
    /// change or first render, falls back to a full repaint preceded by a
    /// clear-screen so stale glyphs don't linger.
    let render (writer: TextWriter) (colorSupport: ColorSupport) (previous: Screen voption) (next: Screen) =
        let builder = StringBuilder()

        let dimensionsChanged =
            match previous with
            | ValueSome p -> p.Width <> next.Width || p.Height <> next.Height
            | ValueNone -> true

        if dimensionsChanged then
            append builder clearScreen
            append builder homeCursor
            appendDiffedCells builder colorSupport ValueNone next
        else
            appendDiffedCells builder colorSupport previous next

        appendCursor builder next

        writer.Write(builder.ToString())
        writer.Flush()
