module Fedit.Tests.KeymapTests

open Fedit
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
