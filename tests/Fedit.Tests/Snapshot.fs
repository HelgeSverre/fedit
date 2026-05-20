module Fedit.Tests.Snapshot

open System.Text
open Fedit

/// Compact, deterministic string projection of a rendered `Screen`. Used as
/// the goldens for Tier 2 snapshot tests.
///
/// Layout per row:
///
///   {style-marker}{run-of-glyphs}{style-marker}{run-of-glyphs}…
///
/// Style markers only appear at style changes within a row. Trailing
/// whitespace is rstripped so PadRight noise doesn't dominate diffs.

let private formatColor =
    function
    | Default -> "_"
    | Indexed n -> string n
    | Rgb(r, g, b) -> $"{r}.{g}.{b}"

let private formatStyle (style: Style) =
    let bold = if style.Bold then "B" else "."
    let inverted = if style.Inverted then "I" else "."
    $"[{formatColor style.Foreground}/{formatColor style.Background} {bold}{inverted}]"

let private trimTrailing (s: string) =
    let mutable last = s.Length - 1

    while last >= 0 && s[last] = ' ' do
        last <- last - 1

    if last < 0 then "" else s.Substring(0, last + 1)

let private appendRow (sb: StringBuilder) (screen: Screen) row =
    let rowBuilder = StringBuilder()
    let mutable lastStyle: Style voption = ValueNone

    for col in 0 .. screen.Width - 1 do
        let cell = screen.Cells[row, col]

        if lastStyle <> ValueSome cell.Style then
            rowBuilder.Append(formatStyle cell.Style) |> ignore
            lastStyle <- ValueSome cell.Style

        rowBuilder.Append cell.Glyph |> ignore

    sb.Append(trimTrailing (rowBuilder.ToString())).Append '\n' |> ignore

let render (screen: Screen) : string =
    let sb = StringBuilder()

    let cursor =
        match screen.Cursor with
        | Some c when c.Visible -> $"cursor={c.Top},{c.Left} visible"
        | Some _ -> "cursor=hidden"
        | None -> "cursor=none"

    sb.Append($"=== {screen.Width}x{screen.Height} {cursor} ===\n") |> ignore

    for row in 0 .. screen.Height - 1 do
        appendRow sb screen row

    sb.ToString()
