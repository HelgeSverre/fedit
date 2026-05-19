module Fedit.Tests.CommandsTests

open Fedit
open Xunit
open FsUnit.Xunit

[<Fact>]
let ``empty input parses to Empty`` () =
    match Commands.parse "" with
    | ParsedCommand.Empty -> ()
    | other -> failwithf "expected Empty, got %A" other

    match Commands.parse "   " with
    | ParsedCommand.Empty -> ()
    | other -> failwithf "expected Empty for whitespace, got %A" other

[<Fact>]
let ``write parses to Ready Write`` () =
    Commands.parse "write" |> should equal (Ready Write)

[<Fact>]
let ``quit parses to Ready Quit`` () =
    Commands.parse "quit" |> should equal (Ready Quit)

[<Fact>]
let ``open with path parses to Ready (Open path)`` () =
    Commands.parse "open foo.fs" |> should equal (Ready(Open "foo.fs"))

[<Fact>]
let ``open without path parses to Pending`` () =
    match Commands.parse "open" with
    | Pending _ -> ()
    | other -> failwithf "expected Pending, got %A" other

[<Fact>]
let ``theme with known name parses to Ready`` () =
    Commands.parse "theme green" |> should equal (Ready(Theme "green"))

[<Fact>]
let ``theme with any non-empty name parses to Ready`` () =
    // Validation now happens at execute time so user themes loaded
    // from ~/.config/fedit/themes/*.json can override bundled names.
    match Commands.parse "theme unknown_color" with
    | Ready(Theme name) -> name |> should equal "unknown_color"
    | other -> failwithf "expected Ready(Theme), got %A" other

[<Fact>]
let ``theme with no argument is Pending`` () =
    match Commands.parse "theme" with
    | Pending _ -> ()
    | other -> failwithf "expected Pending, got %A" other

[<Fact>]
let ``recent without path is Pending`` () =
    match Commands.parse "recent" with
    | Pending _ -> ()
    | other -> failwithf "expected Pending, got %A" other

[<Fact>]
let ``buffers without arg is Pending`` () =
    match Commands.parse "buffers" with
    | Pending _ -> ()
    | other -> failwithf "expected Pending, got %A" other

[<Fact>]
let ``unknown command parses to Invalid`` () =
    match Commands.parse "nosuchcommand" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

[<Fact>]
let ``prefix of known command parses to Pending`` () =
    match Commands.parse "wri" with
    | Pending _ -> ()
    | other -> failwithf "expected Pending, got %A" other

[<Fact>]
let ``bare line number parses to Goto with no column`` () =
    Commands.parse "42" |> should equal (Ready(Goto(42, None)))

[<Fact>]
let ``line and column parses to Goto with column`` () =
    Commands.parse "100:6" |> should equal (Ready(Goto(100, Some 6)))

[<Fact>]
let ``goto with extra colons is Invalid`` () =
    match Commands.parse "1:2:3" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

[<Fact>]
let ``goto with zero is Invalid`` () =
    match Commands.parse "0" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

    match Commands.parse "5:0" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid for zero column, got %A" other

[<Fact>]
let ``goto with trailing colon is Invalid`` () =
    match Commands.parse "42:" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

[<Fact>]
let ``completionLimit caps file completions`` () =
    let ctx =
        { RootPath = "/"
          Files = [ "a"; "b"; "c"; "d"; "e" ]
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 2 }

    let comps = Commands.completions ctx "open "
    comps |> List.length |> should equal 2

[<Fact>]
let ``helpLines returns one entry per spec`` () =
    let lines = Commands.helpLines ()
    lines |> List.length |> should be (greaterThanOrEqualTo 11)

[<Fact>]
let ``completions for theme prefix return matches`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    let comps = Commands.completions ctx "theme g"
    comps |> List.exists (fun c -> c.Label = "green") |> should equal true

[<Fact>]
let ``completions for empty input list all command names`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    let comps = Commands.completions ctx ""
    comps |> List.length |> should be (greaterThanOrEqualTo 11)
