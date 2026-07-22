module Fedit.Tests.MacroIOTests

open System.IO
open Fedit
open FsCheck
open FsCheck.FSharp
open Xunit
open FsUnit.Xunit

// ── MacroFile: line grammar (render / parse) ─────────────────────────────

let private roundTrip (registers: Map<char, MacroStep list>) =
    MacroFile.parse ((MacroFile.render registers).Split '\n')

[<Fact>]
let ``render emits the commented grammar header`` () =
    MacroFile.render Map.empty
    |> _.StartsWith("# fedit macros")
    |> should equal true

    // The header alone parses back to nothing: every line is a comment.
    let registers, errors = roundTrip Map.empty
    registers |> should equal (Map.empty: Map<char, MacroStep list>)
    errors |> should equal ([]: string list)

[<Fact>]
let ``a rendered file round-trips gnarly payloads verbatim`` () =
    let registers =
        Map.ofList
            [ 'a',
              [ RunAction(InsertText "he said \"hi\"\n\ttab \\ done # not-a-comment = x")
                RunAction DeleteBackward
                RunAction(SearchFor "let x")
                RunAction(Action.Goto(12, Some 4)) ]
              'b', [ RunCommand "open README.md"; RunAction(MoveLinesUp 3); RunCommand "reload" ] ]

    let parsed, errors = roundTrip registers
    errors |> should equal ([]: string list)
    parsed |> should equal registers

[<Fact>]
let ``whole-step quoting round-trips syntaxes that carry whitespace`` () =
    // `run-plugin:src/name arg` and `set-theme:a b` are the only action
    // syntaxes with whitespace outside a quoted payload — the renderer
    // wraps the whole step in quotes so they stay one token.
    let registers =
        Map.ofList
            [ 'p',
              [ RunAction(RunPlugin("wordcount", "wc", "selection and more"))
                RunAction(SetTheme "gruvbox light") ] ]

    let rendered = MacroFile.render registers

    rendered
    |> _.Contains("\"run-plugin:wordcount/wc selection and more\"")
    |> should equal true

    let parsed, errors = roundTrip registers
    errors |> should equal ([]: string list)
    parsed |> should equal registers

[<Fact>]
let ``command steps accept a bare whitespace-free payload`` () =
    let registers, errors = MacroFile.parse [ "a = command:messages undo" ]
    errors |> should equal ([]: string list)

    registers
    |> Map.find 'a'
    |> should equal [ RunCommand "messages"; RunAction Undo ]

[<Fact>]
let ``a malformed line is skipped and reported with its line number`` () =
    let lines =
        [ "# comment"
          ""
          "a = insert-text:\"ok\""
          "b = wat"
          "c = insert-text:\"unterminated" ]

    let registers, errors = MacroFile.parse lines

    registers |> should equal (Map.ofList [ 'a', [ RunAction(InsertText "ok") ] ])
    errors.Length |> should equal 2
    errors[0] |> _.StartsWith("macros:4: ") |> should equal true
    errors[1] |> _.StartsWith("macros:5: ") |> should equal true

[<Fact>]
let ``a later line for the same register wins`` () =
    let registers, errors = MacroFile.parse [ "a = undo"; "a = redo" ]
    errors |> should equal ([]: string list)
    registers |> Map.find 'a' |> should equal [ RunAction Redo ]

[<Fact>]
let ``an empty right-hand side is reported, not treated as a clear`` () =
    let registers, errors = MacroFile.parse [ "a =" ]
    registers |> should equal (Map.empty: Map<char, MacroStep list>)
    errors.Length |> should equal 1
    errors[0] |> _.Contains("delete the line") |> should equal true

[<Fact>]
let ``a line without an equals sign is reported`` () =
    let _, errors = MacroFile.parse [ "just some text" ]
    errors |> should equal [ "macros:1: missing '='" ]

[<Fact>]
let ``a multi-character register is reported`` () =
    let _, errors = MacroFile.parse [ "ab = undo" ]
    errors.Length |> should equal 1
    errors[0] |> _.Contains("single character") |> should equal true

[<Fact>]
let ``the macros file round-trips every recordable step list`` () =
    let commandLineGen =
        Gen.listOf (Gen.elements [ "open "; "a"; "B"; "\""; "\\"; " "; ":"; "="; "#"; "→"; "🎉" ])
        |> Gen.map (String.concat "")

    let stepGen =
        Gen.oneof
            [ Gen.map RunAction KeymapTests.serializableActionGen
              Gen.map RunCommand commandLineGen ]

    Check.QuickThrowOnFailure(
        Prop.forAll (Arb.fromGen (Gen.nonEmptyListOf stepGen)) (fun steps ->
            let expected = Map.ofList [ 'q', steps ]
            let parsed, errors = roundTrip expected
            errors = [] && parsed = expected)
    )

// ── MacroIO: the disk half, against temp paths ───────────────────────────

let private tempMacrosPath () =
    Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

    model

let private press chord m =
    fst (Editor.update (KeyPressed chord) m)

let private chr c : Chord = { Mods = Set.empty; Key = Key.Char c }
let private nk n : Chord = { Mods = Set.empty; Key = Named n }

let private ck c : Chord =
    { Mods = Set.ofList [ Ctrl ]
      Key = Key.Char c }

[<Fact>]
let ``a recorded macro survives the save-load file round trip`` () =
    // Record through the real update loop: typed text (with a quote, a
    // backslash, and a newline) plus a palette command accept — then save
    // the registers to disk and load them back identical.
    let recording, _ = Editor.runAction (RecordMacro 'a') (initModel ())

    let typed =
        recording
        |> press (chr 'x')
        |> press (chr '\\')
        |> press (chr '"')
        |> press (nk Enter)
        |> press (chr 'y')

    let prompted =
        "reload" |> Seq.fold (fun m c -> press (chr c) m) (press (ck 'p') typed)

    let accepted = press (nk Enter) prompted
    let committed, _ = Editor.runAction (RecordMacro 'a') accepted

    committed.Registers
    |> Map.find 'a'
    |> should equal [ RunAction(InsertText "x\\\"\ny"); RunCommand "reload" ]

    let path = tempMacrosPath ()

    try
        MacroIO.saveTo path committed.Registers
        let loaded, errors = MacroIO.loadFrom path
        errors |> should equal ([]: string list)
        loaded |> should equal committed.Registers
    finally
        File.Delete path

[<Fact>]
let ``loading a missing file yields empty registers and no errors`` () =
    let registers, errors = MacroIO.loadFrom (tempMacrosPath ())
    registers |> should equal (Map.empty: Map<char, MacroStep list>)
    errors |> should equal ([]: string list)

[<Fact>]
let ``ensureFileAt creates a missing file with the grammar header`` () =
    let path = tempMacrosPath ()

    try
        MacroIO.ensureFileAt path Map.empty
        File.Exists path |> should equal true
        File.ReadAllText path |> _.StartsWith("# fedit macros") |> should equal true

        let registers, errors = MacroIO.loadFrom path
        registers |> should equal (Map.empty: Map<char, MacroStep list>)
        errors |> should equal ([]: string list)
    finally
        File.Delete path

[<Fact>]
let ``ensureFileAt leaves an existing file untouched`` () =
    let path = tempMacrosPath ()

    try
        File.WriteAllText(path, "a = undo\n")
        MacroIO.ensureFileAt path (Map.ofList [ 'b', [ RunAction Redo ] ])
        File.ReadAllText path |> should equal "a = undo\n"
    finally
        File.Delete path
