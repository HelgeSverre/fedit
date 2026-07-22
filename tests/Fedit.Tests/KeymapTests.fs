module Fedit.Tests.KeymapTests

open Fedit
open FsCheck
open FsCheck.FSharp
open Xunit
open FsUnit.Xunit

let private cs = Keymap.chord [ Ctrl ] (Key.Char 's')
let private ctrlG = Keymap.chord [ Ctrl ] (Key.Char 'g')

// ── resolve (spec §6.2) ───────────────────────────────────────────────

[<Fact>]
let ``context binding beats a global binding for the same stroke`` () =
    let km =
        [ { Stroke = [ ctrlG ]
            Context = Context.Global
            Action = Some Save }
          { Stroke = [ ctrlG ]
            Context = Context.Editor
            Action = Some Undo } ]

    Keymap.resolve Context.Editor [ ctrlG ] km |> should equal (Bound Undo)

[<Fact>]
let ``a global binding resolves in a context that has no own binding`` () =
    let km =
        [ { Stroke = [ ctrlG ]
            Context = Context.Global
            Action = Some Save } ]

    Keymap.resolve Context.Editor [ ctrlG ] km |> should equal (Bound Save)

[<Fact>]
let ``later binding wins within the same tier`` () =
    let km =
        [ { Stroke = [ ctrlG ]
            Context = Context.Editor
            Action = Some Save }
          { Stroke = [ ctrlG ]
            Context = Context.Editor
            Action = Some Undo } ]

    Keymap.resolve Context.Editor [ ctrlG ] km |> should equal (Bound Undo)

[<Fact>]
let ``unbind in context suppresses the global fallback`` () =
    let km =
        [ { Stroke = [ ctrlG ]
            Context = Context.Global
            Action = Some Save }
          { Stroke = [ ctrlG ]
            Context = Context.Editor
            Action = None } ]

    Keymap.resolve Context.Editor [ ctrlG ] km |> should equal Unbound

[<Fact>]
let ``no matching binding resolves to NotBound`` () =
    Keymap.resolve Context.Editor [ ctrlG ] [] |> should equal NotBound

// ── prefix-conflict detection (spec §6.2, §7) ─────────────────────────

[<Fact>]
let ``a standalone that prefixes a bound sequence is flagged as a conflict`` () =
    let ck = Keymap.chord [ Ctrl ] (Key.Char 'k')
    let cc = Keymap.chord [ Ctrl ] (Key.Char 'c')

    let km =
        [ { Stroke = [ ck ]
            Context = Context.Editor
            Action = Some Save }
          { Stroke = [ ck; cc ]
            Context = Context.Editor
            Action = Some Copy } ]

    let conflicts = Keymap.prefixConflicts km
    conflicts |> List.length |> should equal 1
    (conflicts |> List.head |> fst).Stroke |> should equal [ ck ]

// ── continuations (which-key, stage U5) ───────────────────────────────

let private ctrlK = Keymap.chord [ Ctrl ] (Key.Char 'k')
let private ctrlC = Keymap.chord [ Ctrl ] (Key.Char 'c')
let private ctrlU = Keymap.chord [ Ctrl ] (Key.Char 'u')

[<Fact>]
let ``continuations lists context and global extensions sorted by remainder`` () =
    let km =
        [ { Stroke = [ ctrlK; ctrlU ]
            Context = Context.Global
            Action = Some Undo }
          { Stroke = [ ctrlK; ctrlC ]
            Context = Context.Editor
            Action = Some Copy }
          { Stroke = [ ctrlK; ctrlG ]
            Context = Context.Sidebar
            Action = Some Save } ]

    Keymap.continuations Context.Editor [ ctrlK ] km
    |> should equal [ "ctrl+c", "copy"; "ctrl+u", "undo" ]

[<Fact>]
let ``continuations renders a multi-chord remainder in full`` () =
    let km =
        [ { Stroke = [ ctrlK; ctrlC; ctrlU ]
            Context = Context.Editor
            Action = Some Save } ]

    Keymap.continuations Context.Editor [ ctrlK ] km
    |> should equal [ "ctrl+c ctrl+u", "save" ]

[<Fact>]
let ``continuations drops a stroke whose effective binding is an unbind`` () =
    let km =
        [ { Stroke = [ ctrlK; ctrlC ]
            Context = Context.Editor
            Action = Some Copy }
          { Stroke = [ ctrlK; ctrlC ]
            Context = Context.Editor
            Action = None } ]

    Keymap.continuations Context.Editor [ ctrlK ] km
    |> should equal ([]: (string * string) list)

[<Fact>]
let ``continuations resolves an overridden stroke to its effective action`` () =
    let km =
        [ { Stroke = [ ctrlK; ctrlC ]
            Context = Context.Global
            Action = Some Save }
          { Stroke = [ ctrlK; ctrlC ]
            Context = Context.Editor
            Action = Some Copy } ]

    Keymap.continuations Context.Editor [ ctrlK ] km
    |> should equal [ "ctrl+c", "copy" ]

[<Fact>]
let ``contextOf maps every focus target onto its keymap context`` () =
    Keymap.contextOf FocusTarget.Editor |> should equal Context.Editor
    Keymap.contextOf FocusTarget.Sidebar |> should equal Context.Sidebar
    Keymap.contextOf FocusTarget.Prompt |> should equal Context.Prompt

// ── parser (spec §6.6 + run-plugin grammar) ───────────────────────────

[<Theory>]
[<InlineData("editor  ctrl+s = save")>]
[<InlineData("ctrl+s = save")>] // default context = editor
[<InlineData("# a comment")>]
[<InlineData("")>] // blank
[<InlineData("editor  ctrl+k ctrl+c = no-op")>] // sequence
[<InlineData("editor  ctrl+x =")>] // unbind
[<InlineData("editor  f6 = set-theme:gruvbox")>] // arg-taking
[<InlineData("editor  alt+up = move-lines-up:3")>]
[<InlineData("editor  alt+down = move-lines-down")>]
[<InlineData("editor  ctrl+k ctrl+w = run-plugin:wordcount/wc")>]
[<InlineData("editor  ctrl+k ctrl+e = run-plugin:wordcount/wc selection")>]
[<InlineData("editor  ctrl+k ctrl+t = insert-text:\"TODO: \"")>] // quoted payload
[<InlineData("editor  ctrl+k ctrl+f = insert-text:fn")>] // bare payload
[<InlineData("editor  backspace = delete-backward")>]
[<InlineData("editor  delete = delete-forward")>]
[<InlineData("editor  escape = clear-selection")>]
[<InlineData("editor  ctrl+w = close-buffer")>]
[<InlineData("global  f3 = search-next")>]
[<InlineData("global  shift+f3 = search-previous")>]
let ``parseLine accepts valid forms`` (line: string) =
    Keymap.parseLine line |> Result.isOk |> should equal true

[<Theory>]
[<InlineData("ctrl+s save")>] // no '='
[<InlineData("editor  boguskey = save")>] // unparseable chord
[<InlineData("editor  ctrl+s = no-such-action")>] // unknown action
[<InlineData("editor  ctrl+j = run-plugin:wordcount")>] // run-plugin missing '/'
let ``parseLine rejects malformed forms`` (line: string) =
    Keymap.parseLine line |> Result.isError |> should equal true

[<Fact>]
let ``unbind parses to a binding with Action None`` () =
    match Keymap.parseLine "editor  ctrl+x =" with
    | Ok(Some b) -> b.Action |> should equal (None: Action option)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``run-plugin parses source name and arg`` () =
    match Keymap.parseLine "editor  ctrl+j = run-plugin:wordcount/wc selection" with
    | Ok(Some b) -> b.Action |> should equal (Some(RunPlugin("wordcount", "wc", "selection")))
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``run-plugin preserves an embedded slash in the plugin arg`` () =
    match Keymap.parseLine "editor  ctrl+j = run-plugin:fs/find a/b" with
    | Ok(Some b) -> b.Action |> should equal (Some(RunPlugin("fs", "find", "a/b")))
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``move-lines actions parse a positive count and default to one`` () =
    match Keymap.parseLine "editor  alt+up = move-lines-up:3" with
    | Ok(Some b) -> b.Action |> should equal (Some(MoveLinesUp 3))
    | other -> failwithf "unexpected %A" other

    match Keymap.parseLine "editor  alt+down = move-lines-down" with
    | Ok(Some b) -> b.Action |> should equal (Some(MoveLinesDown 1))
    | other -> failwithf "unexpected %A" other

[<Theory>]
[<InlineData("editor  alt+up = move-lines-up:0")>]
[<InlineData("editor  alt+down = move-lines-down:-2")>]
let ``move-lines actions reject non-positive counts`` (line: string) =
    Keymap.parseLine line |> Result.isError |> should equal true

[<Fact>]
let ``Alt Up and Alt Down move lines by one in the default editor keymap`` () =
    let altUp = Keymap.chord [ Alt ] (Named Up)
    let altDown = Keymap.chord [ Alt ] (Named Down)

    Keymap.resolve Context.Editor [ altUp ] Keymap.defaults
    |> should equal (Bound(MoveLinesUp 1))

    Keymap.resolve Context.Editor [ altDown ] Keymap.defaults
    |> should equal (Bound(MoveLinesDown 1))

[<Fact>]
let ``a context word selects the binding context`` () =
    match Keymap.parseLine "sidebar  enter = sidebar-activate" with
    | Ok(Some b) -> b.Context |> should equal Context.Sidebar
    | other -> failwithf "unexpected %A" other

// ── parse (whole file) + overlay + conflict drop ──────────────────────

[<Fact>]
let ``parse overlays user bindings on defaults and later wins`` () =
    // Rebind ctrl+s (a default Save) to no-op in the editor context.
    let km, errors = Keymap.parse [ "editor  ctrl+s = no-op" ]
    errors |> should equal ([]: string list)
    Keymap.resolve Context.Editor [ cs ] km |> should equal (Bound NoOp)

[<Fact>]
let ``parse collects errors with line numbers and still returns a working keymap`` () =
    let km, errors = Keymap.parse [ "garbage line"; "editor  ctrl+s = save" ]
    errors |> List.length |> should equal 1
    (errors |> List.head).StartsWith "keybinds:1:" |> should equal true
    // defaults are still present
    Keymap.resolve Context.Global [ cs ] km |> should equal (Bound Save)

[<Fact>]
let ``parse drops a user standalone that conflicts with a user sequence`` () =
    let km, errors =
        Keymap.parse [ "editor  ctrl+j = save"; "editor  ctrl+j ctrl+k = copy" ]
    // the standalone ctrl+j is dropped (it prefixes the sequence) and reported
    errors
    |> List.exists (fun e -> e.Contains "prefix of a bound sequence")
    |> should equal true

    Keymap.resolve Context.Editor [ Keymap.chord [ Ctrl ] (Key.Char 'j') ] km
    |> should equal NotBound

// ── index ─────────────────────────────────────────────────────────────

[<Fact>]
let ``index maps Save to its default ctrl+s stroke`` () =
    let idx = Keymap.index Keymap.defaults
    idx |> Map.tryFind Save |> Option.defaultValue [] |> should contain [ cs ]

// ── defaults parity ───────────────────────────────────────────────────

[<Theory>]
[<InlineData("ctrl+s", "Save")>]
[<InlineData("ctrl+p", "OpenPalette")>]
[<InlineData("ctrl+o", "OpenFilePicker")>]
[<InlineData("ctrl+c", "Copy")>]
let ``global defaults resolve to the expected action`` (strokeText: string) (actionName: string) =
    let stroke = [ (Chord.parse strokeText).Value ]

    match Keymap.resolve Context.Global stroke Keymap.defaults with
    | Bound a -> (sprintf "%A" a) |> should equal actionName
    | other -> failwithf "expected Bound %s, got %A" actionName other

[<Fact>]
let ``editor motion defaults resolve in the editor context`` () =
    let left = [ Keymap.chord [] (Named Left) ]

    Keymap.resolve Context.Editor left Keymap.defaults
    |> should equal (Bound MoveLeft)

[<Fact>]
let ``terminal-reported Command+Left resolves to line home`` () =
    let cmdLeft = [ Keymap.chord [ Super ] (Named Left) ]

    Keymap.resolve Context.Editor cmdLeft Keymap.defaults
    |> should equal (Bound MoveHome)

// ── plugin precedence flip (spec §6.7.4, §11.2) ───────────────────────

[<Fact>]
let ``the user keymap takes precedence over a plugin chord on the same stroke`` () =
    // ctrl+k is bound in the keymap (to write/Save) AND in the plugin registry
    // (to open). The keymap must win.
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

    let pathed = Buffer.fromText 1 (Some "/root/file.txt") "file.txt" "x" "\n"

    let km =
        Keymap.defaults
        @ [ { Stroke = [ Keymap.chord [ Ctrl ] (Key.Char 'k') ]
              Context = Context.Editor
              Action = Some Save } ]

    let model =
        { model with
            Keymap = km
            Editors =
                { model.Editors with
                    Buffers = Map.ofList [ 1, pathed ] }
            Plugins =
                { model.Plugins with
                    Keybindings = [ (Fedit.PluginApi.KeyChord.Ctrl 'k', "open") ] } }

    let _, effects =
        Editor.update
            (KeyPressed
                { Mods = Set.ofList [ Ctrl ]
                  Key = Key.Char 'k' })
            model

    // keymap → Save → SaveBuffer; if the plugin had won, it would open a prompt
    // (no SaveBuffer effect).
    effects
    |> List.exists (function
        | SaveBuffer _ -> true
        | _ -> false)
    |> should equal true

// ── renderDoc (the `:keybind` buffer body) ────────────────────────────

[<Fact>]
let ``renderDoc groups effective bindings by context and shows a known default`` () =
    let doc = Keymap.renderDoc Keymap.defaults

    Assert.Contains("## global", doc)
    Assert.Contains("## editor", doc)
    Assert.Contains("## sidebar", doc)
    // ctrl+s → save is a built-in default and should appear with its kebab-case
    // action name (same names the keybinds config file and `fedit keybinds` use).
    Assert.Contains("ctrl+s", doc)
    Assert.Contains("save", doc)
    // The body holds no control characters other than its line separators.
    doc
    |> Seq.exists (fun c -> System.Char.IsControl c && c <> '\n')
    |> should equal false

[<Fact>]
let ``renderDoc lists each context+stroke once, reflecting later-wins overrides`` () =
    // Override the default ctrl+s (Save) with Quit; the effective doc must show
    // ctrl+s exactly once, bound to Quit.
    let overridden =
        Keymap.defaults
        @ [ { Stroke = [ cs ]
              Context = Context.Global
              Action = Some Action.Quit } ]

    let doc = Keymap.renderDoc overridden

    // Match the stroke column exactly ("ctrl+s" then padding) — a loose
    // substring also matches "ctrl+shift+m" etc.
    let csLines =
        doc.Split('\n') |> Array.filter (fun l -> l.Trim().StartsWith("ctrl+s "))

    csLines.Length |> should equal 1
    Assert.Contains("quit", csLines[0])

// ── action syntax: parseAction / Action.toSyntax (macros stage M1) ────

[<Fact>]
let ``parseAction accepts the text-editing and search verbs`` () =
    [ "delete-backward", DeleteBackward
      "delete-forward", DeleteForward
      "clear-selection", ClearSelection
      "close-buffer", CloseBuffer
      "search-next", SearchNext
      "search-previous", SearchPrevious ]
    |> List.iter (fun (syntax, expected) ->
        Keymap.parseAction syntax |> should equal (Ok expected: Result<Action, string>))

[<Fact>]
let ``insert-text decodes a quoted payload with every escape`` () =
    Keymap.parseAction "insert-text:\"a \\\"b\\\" \\\\ \\n\\t\\r:c\""
    |> should equal (Ok(InsertText "a \"b\" \\ \n\t\r:c"): Result<Action, string>)

[<Fact>]
let ``insert-text accepts a bare whitespace-free payload`` () =
    Keymap.parseAction "insert-text:fn"
    |> should equal (Ok(InsertText "fn"): Result<Action, string>)

    // Only the FIRST ':' splits verb from payload; later ones are literal.
    Keymap.parseAction "insert-text:a:b"
    |> should equal (Ok(InsertText "a:b"): Result<Action, string>)

[<Fact>]
let ``insert-text decodes an empty quoted payload`` () =
    Keymap.parseAction "insert-text:\"\""
    |> should equal (Ok(InsertText ""): Result<Action, string>)

[<Fact>]
let ``search-for shares the free-text payload grammar`` () =
    Keymap.parseAction "search-for:\"let x\""
    |> should equal (Ok(SearchFor "let x"): Result<Action, string>)

    Keymap.parseAction "search-for:needle"
    |> should equal (Ok(SearchFor "needle"): Result<Action, string>)

    Keymap.parseAction "search-for:" |> Result.isError |> should equal true

    Keymap.parseAction "search-for:\"unterminated"
    |> Result.isError
    |> should equal true

[<Fact>]
let ``toSyntax quotes a search-for query`` () =
    Action.toSyntax (SearchFor "let x")
    |> should equal (Some "search-for:\"let x\"")

[<Theory>]
[<InlineData("insert-text")>] // no payload
[<InlineData("insert-text:")>] // empty payload
[<InlineData("insert-text:a b")>] // unquoted whitespace
[<InlineData("insert-text:\"unterminated")>] // unterminated quote
[<InlineData("insert-text:\"a\" b")>] // text after the closing quote
[<InlineData("insert-text:\"a\\q\"")>] // unknown escape
[<InlineData("insert-text:\"a\\")>] // dangling backslash
let ``insert-text rejects malformed payloads`` (syntax: string) =
    Keymap.parseAction syntax |> Result.isError |> should equal true

[<Fact>]
let ``parseLine binds insert-text with a quoted payload`` () =
    match Keymap.parseLine "editor  ctrl+k ctrl+t = insert-text:\"TODO: \"" with
    | Ok(Some b) -> b.Action |> should equal (Some(InsertText "TODO: "))
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``toSyntax quotes an insert-text payload with escapes`` () =
    Action.toSyntax (InsertText "he said \"hi\"\n")
    |> should equal (Some "insert-text:\"he said \\\"hi\\\"\\n\"")

[<Fact>]
let ``toSyntax preserves payloads for the arg-taking actions`` () =
    Action.toSyntax (MoveLinesUp 3) |> should equal (Some "move-lines-up:3")
    Action.toSyntax (MoveLinesDown 2) |> should equal (Some "move-lines-down:2")
    Action.toSyntax (JumpToBuffer 7) |> should equal (Some "jump-to-buffer:7")
    Action.toSyntax (SetTheme "gruvbox") |> should equal (Some "set-theme:gruvbox")
    Action.toSyntax (Action.Goto(12, None)) |> should equal (Some "goto:12")
    Action.toSyntax (Action.Goto(12, Some 4)) |> should equal (Some "goto:12:4")

    Action.toSyntax (RunPlugin("wordcount", "wc", ""))
    |> should equal (Some "run-plugin:wordcount/wc")

    Action.toSyntax (RunPlugin("wordcount", "wc", "selection"))
    |> should equal (Some "run-plugin:wordcount/wc selection")

    Action.toSyntax (RecordMacro 'q') |> should equal (Some "record-macro:q")
    Action.toSyntax (ReplayMacro('q', 3)) |> should equal (Some "replay-macro:q:3")

[<Fact>]
let ``toSyntax returns None only for the cases without parse syntax`` () =
    Action.toSyntax (Chain []) |> should equal (None: string option)

    Action.toSyntax (When(HasSelection, NoOp, NoOp))
    |> should equal (None: string option)

    Action.toSyntax (SaveAs "/tmp/x") |> should equal (None: string option)

// ── round-trip property: parseAction (toSyntax a) = Ok a ──────────────

/// Cases whose syntax carries a payload (or has none at all) — excluded
/// when deriving the payload-free pool from `Keybinds.allActions`, whose
/// own coverage test guarantees it lists every `Action` case, so a future
/// payload-free case joins the round-trip pool automatically.
let private carriesPayload (action: Action) =
    match action with
    | InsertText _
    | SearchFor _
    | MoveLinesUp _
    | MoveLinesDown _
    | JumpToBuffer _
    | SetTheme _
    | Action.Goto _
    | RunPlugin _
    | RecordMacro _
    | ReplayMacro _
    | Chain _
    | When _
    | SaveAs _ -> true
    | _ -> false

let private payloadFreeActions =
    Fedit.Cli.Commands.Keybinds.allActions |> List.filter (carriesPayload >> not)

/// Generator over the serializable subset of `Action`, with payloads drawn
/// from each verb's accepted domain. `InsertText` payloads deliberately mix
/// quotes, backslashes, the escape letters, the ':'/'='/'#' syntax
/// characters, whitespace, combining marks, and an astral-plane emoji.
/// Public: `MacroIOTests` reuses it for the macros-file round-trip.
let serializableActionGen: Gen<Action> =
    let registerGen = Gen.elements ([ 'a' .. 'z' ] @ [ '0' .. '9' ])

    let wordGen =
        Gen.nonEmptyListOf (Gen.elements ([ 'a' .. 'z' ] @ [ '-' ]))
        |> Gen.map (List.toArray >> System.String)

    let insertTextPayloadGen =
        Gen.listOf (
            Gen.elements
                [ "\""
                  "\\"
                  "\n"
                  "\t"
                  "\r"
                  " "
                  ":"
                  "="
                  "#"
                  "a"
                  "B"
                  "0"
                  "æ"
                  "→"
                  "🎉"
                  "e\u0301" ]
        )
        |> Gen.map (String.concat "")

    let pluginArgGen =
        Gen.oneof [ Gen.constant ""; Gen.nonEmptyListOf wordGen |> Gen.map (String.concat " ") ]

    Gen.oneof
        [ Gen.elements payloadFreeActions
          Gen.map InsertText insertTextPayloadGen
          Gen.map SearchFor insertTextPayloadGen
          Gen.map MoveLinesUp (Gen.choose (1, 99))
          Gen.map MoveLinesDown (Gen.choose (1, 99))
          Gen.map JumpToBuffer (Gen.choose (1, 9))
          Gen.map SetTheme wordGen
          Gen.map2
              (fun line column -> Action.Goto(line, column))
              (Gen.choose (1, 9999))
              (Gen.optionOf (Gen.choose (1, 999)))
          Gen.map3 (fun source name arg -> RunPlugin(source, name, arg)) wordGen wordGen pluginArgGen
          Gen.map RecordMacro registerGen
          Gen.map2 (fun register count -> ReplayMacro(register, count)) registerGen (Gen.choose (1, 99)) ]

[<Fact>]
let ``parseAction inverts toSyntax for every serializable action`` () =
    Check.QuickThrowOnFailure(
        Prop.forAll (Arb.fromGen serializableActionGen) (fun action ->
            let syntax =
                match Action.toSyntax action with
                | Some s -> s
                | None -> failwithf "toSyntax returned None for %A" action

            match Keymap.parseAction syntax with
            | Ok parsed -> parsed = action
            | Result.Error message -> failwithf "parseAction rejected %s (from %A): %s" syntax action message)
    )

[<Fact>]
let ``insert-text payloads survive the round trip verbatim`` () =
    Check.QuickThrowOnFailure(fun (payload: string) ->
        let safe = if isNull payload then "" else payload
        let syntax = (Action.toSyntax (InsertText safe)).Value

        match Keymap.parseAction syntax with
        | Ok(InsertText decoded) -> decoded = safe
        | other -> failwithf "unexpected %A for %s" other syntax)
