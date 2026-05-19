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

    let private appendScreenRows (builder: StringBuilder) screen =
        for row in 0 .. screen.Height - 1 do
            append builder (cursorPosition row 0)

            let mutable currentStyle: Style option = None

            for col in 0 .. screen.Width - 1 do
                let cell = screen.Cells[row, col]

                if currentStyle <> Some cell.Style then
                    append builder (styleToAnsiSequence cell.Style)
                    currentStyle <- Some cell.Style

                appendChar builder cell.Glyph

            append builder resetStyle

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

    let render (writer: TextWriter) screen =
        let builder = StringBuilder(homeCursor)

        appendScreenRows builder screen
        appendCursor builder screen

        writer.Write(builder.ToString())
        writer.Flush()
