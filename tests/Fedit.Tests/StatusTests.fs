module Fedit.Tests.StatusTests

open Fedit
open Xunit
open FsUnit.Xunit

/// Model fixture matching what `Editor.init` produces, so the buffer
/// indicator / dirty / line / column tokens have something concrete to
/// resolve against.
let private freshModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

    model

let private withFormat fmt (model: Model) =
    { model with
        Config = { model.Config with StatusFormat = fmt } }

// ─────────────────────────────────────────────────────────────────────
// Token resolution
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``[MODE] resolves to the focus label`` () =
    let model = freshModel () |> withFormat "[MODE]"
    let rendered = Status.render 10 model
    rendered.TrimEnd() |> should equal "EDIT"

[<Fact>]
let ``[LINE]:[COLUMN] resolves to 1-based cursor position`` () =
    let model = freshModel () |> withFormat "[LINE]:[COLUMN]"
    Status.render 10 model |> should haveSubstring "1:1"

[<Fact>]
let ``[LINE_ENDING] resolves to LF or CRLF`` () =
    let model = freshModel () |> withFormat "[LINE_ENDING]"
    let result = (Status.render 10 model).TrimEnd()
    [ "LF"; "CRLF" ] |> List.contains result |> should equal true

[<Fact>]
let ``[BUFFER] resolves to index slash total`` () =
    let model = freshModel () |> withFormat "[BUFFER]"
    // Fresh model: one scratch buffer, active = 1/1.
    Status.render 10 model |> should haveSubstring "1/1"

[<Fact>]
let ``[CURRENT_FILE] on the scratch buffer renders [scratch]`` () =
    let model = freshModel () |> withFormat "[CURRENT_FILE]"
    Status.render 20 model |> should haveSubstring "[scratch]"

[<Fact>]
let ``[CURRENT_FILE:full] preserves modifier and renders [scratch] when unsaved`` () =
    let model = freshModel () |> withFormat "[CURRENT_FILE:full]"
    Status.render 20 model |> should haveSubstring "[scratch]"

[<Fact>]
let ``[DIRTY] is empty on a clean buffer`` () =
    let model = freshModel () |> withFormat "x[DIRTY]y"
    Status.render 10 model |> should haveSubstring "xy"

[<Fact>]
let ``[DIRTY] shows the marker when the buffer is dirty`` () =
    let baseModel = freshModel ()

    let dirtied =
        { baseModel with
            Editors =
                { baseModel.Editors with
                    Buffers = baseModel.Editors.Buffers |> Map.map (fun _ b -> { b with Dirty = true }) } }
        |> withFormat "x[DIRTY]y"

    Status.render 10 dirtied |> should haveSubstring "[+]"

[<Fact>]
let ``unknown tokens render literally so typos are visible`` () =
    let model = freshModel () |> withFormat "[xyz] [foo:bar]"
    let result = Status.render 30 model
    result |> should haveSubstring "[xyz]"
    result |> should haveSubstring "[foo:bar]"

[<Fact>]
let ``tokens are case-insensitive`` () =
    let lower = freshModel () |> withFormat "[mode]"
    let upper = freshModel () |> withFormat "[MODE]"
    Status.render 10 lower |> should equal (Status.render 10 upper)

// ─────────────────────────────────────────────────────────────────────
// EXPAND layout
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``EXPAND pushes literals to the right edge`` () =
    let model = freshModel () |> withFormat "L<EXPAND>R"
    let rendered = Status.render 10 model
    // L at column 0, R at column 9, padding in the middle.
    rendered |> should equal "L        R"

[<Fact>]
let ``EXPAND is case-insensitive`` () =
    let model = freshModel () |> withFormat "L<expand>R"
    Status.render 10 model |> should equal "L        R"

[<Fact>]
let ``two EXPANDs split the remaining width evenly`` () =
    let model = freshModel () |> withFormat "A<EXPAND>B<EXPAND>C"
    let rendered = Status.render 11 model
    // A + 4 spaces + B + 4 spaces + C = 11 columns.
    rendered |> should equal "A    B    C"

[<Fact>]
let ``odd remainder distributes left to right`` () =
    let model = freshModel () |> withFormat "A<EXPAND>B<EXPAND>C"
    let rendered = Status.render 10 model
    // 7 spaces over 2 expands → 4 + 3.
    rendered |> should equal "A    B   C"

[<Fact>]
let ``EXPAND collapses to zero width when content already fills the bar`` () =
    let model = freshModel () |> withFormat "ABCDE<EXPAND>FGHIJ"
    Status.render 10 model |> should equal "ABCDEFGHIJ"

[<Fact>]
let ``content longer than width is truncated from the right`` () =
    let model = freshModel () |> withFormat "abcdefghij<EXPAND>klmnop"
    let rendered = Status.render 8 model
    rendered.Length |> should equal 8
    rendered |> should equal "abcdefgh"

// ─────────────────────────────────────────────────────────────────────
// Parsing edge cases
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``unclosed bracket falls through as literal text`` () =
    let model = freshModel () |> withFormat "abc [unterminated"
    let rendered = Status.render 30 model
    rendered |> should haveSubstring "abc [unterminated"

[<Fact>]
let ``literal text between tokens is preserved verbatim`` () =
    let model = freshModel () |> withFormat "L:[LINE]  C:[COLUMN]"
    Status.render 30 model |> should haveSubstring "L:1  C:1"

[<Fact>]
let ``empty format string renders as empty (or whitespace within width)`` () =
    let model = freshModel () |> withFormat ""
    Status.render 5 model |> should equal ""

[<Fact>]
let ``default format used by Config.defaults parses without unknown tokens`` () =
    // Sanity: the default format ships with tokens that all resolve to
    // something concrete (no literal `[foo]` leaks through).
    let model = freshModel ()
    let rendered = Status.render 120 model
    rendered |> should not' (haveSubstring "[mode]")
    rendered |> should not' (haveSubstring "[current_file")
    rendered |> should not' (haveSubstring "[line]")
    rendered |> should not' (haveSubstring "[buffer]")
