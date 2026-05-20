namespace Fedit

open System.IO
open System.Text

[<RequireQualifiedAccess>]
module Renderer =
    let private escape = "\u001b"
    let private resetStyle = $"{escape}[0m"
    let private enterAlternateScreen = $"{escape}[?1049h"
    let private leaveAlternateScreen = $"{escape}[?1049l"
    let private hideCursor = $"{escape}[?25l"
    let private showCursor = $"{escape}[?25h"
    let private clearScreen = $"{escape}[2J"
    let private homeCursor = $"{escape}[H"

    let private cursorPosition row col = $"{escape}[{row + 1};{col + 1}H"

    let private colorToAnsiCode isForeground color =
        match color with
        | Default -> if isForeground then "39" else "49"
        | Indexed value -> if isForeground then $"38;5;{value}" else $"48;5;{value}"

    let private styleToAnsiSequence style =
        let parts =
            [ "0"
              if style.Bold then
                  "1"
              if style.Inverted then
                  "7"
              colorToAnsiCode true style.Foreground
              colorToAnsiCode false style.Background ]

        let sequence = String.concat ";" parts

        $"{escape}[{sequence}m"

    let private append (builder: StringBuilder) (text: string) = builder.Append(text) |> ignore

    let private appendChar (builder: StringBuilder) (glyph: char) = builder.Append(glyph) |> ignore

    /// Diff cells between `previous` and `next`; emit cursor jumps and
    /// style changes only for cells that actually changed. Style is
    /// tracked across rows so unchanged styles don't re-emit when crossing
    /// a row boundary.
    let private appendDiffedCells (builder: StringBuilder) (previous: Screen voption) (next: Screen) =
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

        let mutable currentStyle: Style voption = ValueNone
        let mutable lastRow = -2
        let mutable lastCol = -2

        for row in 0 .. next.Height - 1 do
            for col in 0 .. next.Width - 1 do
                if not (sameAsPrev row col) then
                    let cell = next.Cells[row, col]

                    if row <> lastRow || col <> lastCol + 1 then
                        append builder (cursorPosition row col)

                    if currentStyle <> ValueSome cell.Style then
                        append builder (styleToAnsiSequence cell.Style)
                        currentStyle <- ValueSome cell.Style

                    appendChar builder cell.Glyph
                    lastRow <- row
                    lastCol <- col

    let private appendCursor (builder: StringBuilder) screen =
        match screen.Cursor with
        | Some cursor when cursor.Visible ->
            append builder showCursor
            append builder (cursorPosition cursor.Top cursor.Left)
        | _ -> append builder hideCursor

    let enter (writer: TextWriter) =
        writer.Write($"{enterAlternateScreen}{hideCursor}{clearScreen}")

    let leave (writer: TextWriter) =
        writer.Write($"{resetStyle}{showCursor}{leaveAlternateScreen}")

    /// Render `next` to the writer. If `previous` is `ValueSome` and has
    /// the same dimensions, only cells that differ are written. On size
    /// change or first render, falls back to a full repaint preceded by a
    /// clear-screen so stale glyphs don't linger.
    let render (writer: TextWriter) (previous: Screen voption) (next: Screen) =
        let builder = StringBuilder()

        let dimensionsChanged =
            match previous with
            | ValueSome p -> p.Width <> next.Width || p.Height <> next.Height
            | ValueNone -> true

        if dimensionsChanged then
            append builder clearScreen
            append builder homeCursor
            appendDiffedCells builder ValueNone next
        else
            appendDiffedCells builder previous next

        appendCursor builder next

        writer.Write(builder.ToString())
        writer.Flush()
