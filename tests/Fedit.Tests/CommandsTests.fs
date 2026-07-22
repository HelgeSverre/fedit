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
let ``open completions match case-insensitive substrings like the file picker`` () =
    let ctx =
        { RootPath = "/"
          Files = [ "src/Fedit/Editor.fs"; "README.md" ]
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    // Neither a prefix of the relative path nor exact case — must still hit,
    // matching the Ctrl+O picker's substring semantics.
    let comps = Commands.completions ctx "open editor"
    comps |> List.map (fun c -> c.Label) |> should equal [ "src/Fedit/Editor.fs" ]

    comps
    |> List.head
    |> fun c -> c.ApplyText |> should equal "open src/Fedit/Editor.fs"

[<Fact>]
let ``writeas completions use the same substring matcher`` () =
    let ctx =
        { RootPath = "/"
          Files = [ "src/Fedit/Editor.fs"; "README.md" ]
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    let comps = Commands.completions ctx "writeas editor"
    comps |> List.map (fun c -> c.Label) |> should equal [ "src/Fedit/Editor.fs" ]

[<Fact>]
let ``recent completions match substrings of the file name or path`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = [ "/home/user/notes.md"; "/home/user/todo.txt" ]
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    let comps = Commands.completions ctx "recent otes"
    comps |> List.map (fun c -> c.Detail) |> should equal [ "/home/user/notes.md" ]

[<Fact>]
let ``matchesFileQuery matches file name or path, and everything on empty`` () =
    Commands.matchesFileQuery "" "src/Editor.fs" |> should equal true
    Commands.matchesFileQuery "editor" "src/Editor.fs" |> should equal true
    Commands.matchesFileQuery "src/edi" "src/Editor.fs" |> should equal true
    Commands.matchesFileQuery "buffer" "src/Editor.fs" |> should equal false

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

[<Fact>]
let ``parses 'syntax toggle' as Ready (Syntax "toggle")`` () =
    match Commands.parse "syntax toggle" with
    | ParsedCommand.Ready(Command.Syntax "toggle") -> ()
    | other -> failwithf "expected Ready (Syntax toggle), got %A" other

[<Fact>]
let ``parses 'syntax on' and 'syntax off'`` () =
    match Commands.parse "syntax on" with
    | ParsedCommand.Ready(Command.Syntax "on") -> ()
    | other -> failwithf "expected Ready (Syntax on), got %A" other

    match Commands.parse "syntax off" with
    | ParsedCommand.Ready(Command.Syntax "off") -> ()
    | other -> failwithf "expected Ready (Syntax off), got %A" other

[<Fact>]
let ``'syntax' with no argument is Pending`` () =
    match Commands.parse "syntax" with
    | ParsedCommand.Pending _ -> ()
    | other -> failwithf "expected Pending, got %A" other

[<Fact>]
let ``unknown 'syntax' verb is Invalid`` () =
    match Commands.parse "syntax wat" with
    | ParsedCommand.Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

[<Fact>]
let ``syntax completions suggest on/off/toggle`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    let comps = Commands.completions ctx "syntax "
    let labels = comps |> List.map (fun c -> c.Label)
    labels |> List.sort |> should equal [ "off"; "on"; "toggle" ]

[<Fact>]
let ``plugins parses to Ready Plugins`` () =
    match Commands.parse "plugins" with
    | ParsedCommand.Ready Command.Plugins -> ()
    | other -> failwithf "expected Ready Plugins, got %A" other

[<Fact>]
let ``macros parses to Ready Macros`` () =
    match Commands.parse "macros" with
    | ParsedCommand.Ready Command.Macros -> ()
    | other -> failwithf "expected Ready Macros, got %A" other

[<Fact>]
let ``plugins and macros appear in command completions`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 32 }

    let labels = Commands.completions ctx "" |> List.map _.Label
    labels |> should contain "plugins"
    labels |> should contain "macros"

[<Fact>]
let ``quit force parses to Ready ForceQuit`` () =
    Commands.parse "quit force" |> should equal (Ready ForceQuit)

[<Fact>]
let ``unknown quit argument is Invalid`` () =
    match Commands.parse "quit now" with
    | ParsedCommand.Invalid _ -> ()
    | other -> failwithf "expected Invalid, got %A" other

[<Fact>]
let ``close parses to Ready (Close None)`` () =
    Commands.parse "close" |> should equal (Ready(Close None))

[<Fact>]
let ``close with id or name parses to the matching buffer reference`` () =
    Commands.parse "close 2" |> should equal (Ready(Close(Some(ById 2))))
    Commands.parse "close a.fs" |> should equal (Ready(Close(Some(ByName "a.fs"))))

[<Fact>]
let ``quit completions suggest force`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = []
          Themes = Themes.all
          CompletionLimit = 8 }

    Commands.completions ctx "quit " |> List.map _.Label |> should equal [ "force" ]

[<Fact>]
let ``buffers completions mark dirty buffers`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = [ 1, "a.fs", Some "/root/a.fs", true; 2, "b.fs", None, false ]
          Themes = Themes.all
          CompletionLimit = 8 }

    Commands.completions ctx "buffers "
    |> List.map _.Label
    |> should equal [ "1  a.fs [+]"; "2  b.fs" ]

[<Fact>]
let ``close completions list open buffers with the close verb`` () =
    let ctx =
        { RootPath = "/"
          Files = []
          Recent = []
          Buffers = [ 1, "a.fs", Some "/root/a.fs", true; 2, "b.fs", None, false ]
          Themes = Themes.all
          CompletionLimit = 8 }

    let comps = Commands.completions ctx "close "
    comps |> List.map _.ApplyText |> should equal [ "close 1"; "close 2" ]
    comps |> List.map _.Label |> should equal [ "1  a.fs [+]"; "2  b.fs" ]
