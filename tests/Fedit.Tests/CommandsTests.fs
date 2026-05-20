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
let ``parseGoto with bare line number returns Goto with no column`` () =
    Commands.parseGoto "42" |> should equal (Ready(Command.Goto(42, None)))

[<Fact>]
let ``parseGoto with line and column returns Goto with column`` () =
    Commands.parseGoto "100:6" |> should equal (Ready(Command.Goto(100, Some 6)))

[<Fact>]
let ``parseGoto rejects extra colons`` () =
    match Commands.parseGoto "1:2:3" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

[<Fact>]
let ``parseGoto rejects zero line or column`` () =
    match Commands.parseGoto "0" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

    match Commands.parseGoto "5:0" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid for zero column, got %A" other

[<Fact>]
let ``parseGoto rejects trailing colon`` () =
    match Commands.parseGoto "42:" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

[<Fact>]
let ``parseGoto with empty input is Pending`` () =
    match Commands.parseGoto "" with
    | Pending _ -> ()
    | other -> failwithf "expected Pending, got %A" other

[<Fact>]
let ``bare numeric is no longer a command (goto requires colon prefix at prompt layer)`` () =
    match Commands.parse "42" with
    | Invalid _ -> ()
    | other -> failwithf "expected Invalid for bare '42', got %A" other

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

[<Fact>]
let ``config parses to Ready OpenConfig`` () =
    match Commands.parse "config" with
    | ParsedCommand.Ready Command.OpenConfig -> ()
    | other -> failwithf "expected Ready OpenConfig, got %A" other

[<Fact>]
let ``hidden commands stay parseable but never appear in completions`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    let comps = Commands.completions ctx ""
    let labels = comps |> List.map (fun c -> c.Label)

    // Keyboard-first verbs are hidden so the menu doesn't grow stale.
    labels |> should not' (contain "sidebar")
    labels |> should not' (contain "tree")
    labels |> should not' (contain "editor")

    // ...but typing them still works.
    match Commands.parse "sidebar" with
    | ParsedCommand.Ready Command.ToggleSidebar -> ()
    | other -> failwithf "expected Ready ToggleSidebar, got %A" other

[<Fact>]
let ``hidden commands are omitted from helpLines`` () =
    let lines = Commands.helpLines ()
    lines |> List.exists (fun l -> l.StartsWith("sidebar")) |> should equal false
    lines |> List.exists (fun l -> l.StartsWith("editor")) |> should equal false
    lines |> List.exists (fun l -> l.StartsWith("tree")) |> should equal false
