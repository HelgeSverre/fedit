namespace Fedit

open System
open System.IO
open System.Text

[<RequireQualifiedAccess>]
module Renderer =
    let private sgrColor isForeground color =
        match color with
        | Default -> if isForeground then "39" else "49"
        | Indexed value -> if isForeground then $"38;5;{value}" else $"48;5;{value}"

    let private sgr style =
        let parts =
            [ "0"
              if style.Bold then
                  "1"
              if style.Inverted then
                  "7"
              sgrColor true style.Foreground
              sgrColor false style.Background ]

        let sequence = String.concat ";" parts

        $"\u001b[{sequence}m"


    let enter (writer: TextWriter) =
        writer.Write("\u001b[?1049h\u001b[?25l\u001b[2J")

    let leave (writer: TextWriter) =
        writer.Write("\u001b[0m\u001b[?25h\u001b[?1049l")

    let render (writer: TextWriter) screen =
        let builder = StringBuilder("\u001b[H")

        for row in 0 .. screen.Height - 1 do
            builder.Append($"\u001b[{row + 1};1H") |> ignore

            let mutable currentStyle: Style option = None

            for col in 0 .. screen.Width - 1 do
                let cell = screen.Cells[row, col]

                if currentStyle <> Some cell.Style then
                    builder.Append(sgr cell.Style) |> ignore
                    currentStyle <- Some cell.Style

                builder.Append(cell.Glyph) |> ignore

            builder.Append("\u001b[0m") |> ignore

        match screen.Cursor with
        | Some cursor when cursor.Visible ->
            builder.Append($"\u001b[?25h\u001b[{cursor.Top + 1};{cursor.Left + 1}H")
            |> ignore
        | _ -> builder.Append("\u001b[?25l") |> ignore

        writer.Write(builder.ToString())
        writer.Flush()
