module Fedit.Tests.ScreenTests

open Fedit
open Fedit.PromptTypes
open System.IO
open Xunit
open FsUnit.Xunit

// Regression: a control character must never reach a screen cell. The renderer
// emits each cell glyph raw and tracks the cursor as advancing exactly one
// column per cell, so a literal '\n' byte (e.g. from a multi-line `:keybind`
// notification rendered into the single-row status line) desyncs the cursor and
// corrupts the TUI until a full repaint. The grid invariant is "every cell holds
// one printable column", enforced at the setCell choke point.

[<Fact>]
let ``setCell coerces a control-character glyph to a space`` () =
    let screen = Screen.create 4 1
    Screen.setCell 0 0 Style.defaultStyle '\n' screen
    screen.Cells[0, 0].Glyph |> should equal ' '

[<Fact>]
let ``writeText never stores control characters as cell glyphs`` () =
    let screen = Screen.create 10 1
    Screen.writeText 0 0 Style.defaultStyle 10 "ab\ncd\te" screen

    for x in 0..9 do
        Assert.False(System.Char.IsControl(screen.Cells[0, x].Glyph))

// Regression: an `Indexed` background color must interpolate into the SGR escape
// (`48;5;200`), not leak the literal `{value}`. A missing `$` on the background
// branch of `Renderer.colorToAnsiCode` emitted `\x1b[…;48;5;{value}m`, surfacing
// the broken `value}m` token in the rendered frame. Indexed backgrounds occur on
// 256-color terminals (every Rgb background is downgraded to Indexed) and whenever
// a theme uses an indexed background directly.
[<Fact>]
let ``renderer interpolates an indexed background color`` () =
    let screen = Screen.create 1 1

    Screen.setCell
        0
        0
        { Style.defaultStyle with
            Background = Indexed 200uy }
        'x'
        screen

    use writer = new StringWriter()
    Renderer.render writer ColorAnsi256 ValueNone screen
    let output = writer.ToString()
    output |> should haveSubstring "48;5;200"
    output |> should not' (haveSubstring "{value}")

[<Fact>]
let ``a multi-line notification renders without control glyphs`` () =
    // Reproduces the `:keybind` listing: a multi-line body shown via the
    // [NOTIFICATION] status token. Pre-fix, the embedded newlines land in the
    // status row as control glyphs and corrupt the terminal.
    let model, _ =
        Editor.init "/root" { Width = 120; Height = 12 } (Config.defaults Themes.defaultTheme) []

    let model =
        { model with
            Notification = Some(Notification.info "global ctrl+q Quit\neditor ctrl+s Save\nsidebar enter Open") }

    let screen = Layout.render model

    let controlCells =
        [ for row in 0 .. screen.Height - 1 do
              for col in 0 .. screen.Width - 1 do
                  let glyph = screen.Cells[row, col].Glyph

                  if System.Char.IsControl glyph then
                      yield (row, col, int glyph) ]

    Assert.True(List.isEmpty controlCells, sprintf "control glyphs leaked into cells: %A" controlCells)

[<Fact>]
let ``plugin prompt session renders the selected item title, badge, version, path, and prompt label`` () =
    let model, _ =
        Editor.init "/root" { Width = 100; Height = 24 } (Config.defaults Themes.defaultTheme) []

    let manifest =
        { Name = "alpha"
          Version = "1.0.0"
          ApiVersion = "1"
          Description = ""
          Author = ""
          Homepage = ""
          EntryAssembly = "alpha.dll"
          EntryType = "Alpha.Plugin" }

    let plugin =
        { Manifest = manifest
          Path = "/plugins/alpha"
          Status = Loaded
          Commands = []
          Keybindings = []
          Conflicts = [] }

    let model =
        { model with
            Plugins =
                { PluginRegistry.empty with
                    Loaded = Map.ofList [ "alpha", plugin ] }
            Focus = Prompt
            Prompt =
                { model.Prompt with
                    Active = true
                    Session = PromptSessionKind.PluginsSession
                    Text = "alp"
                    Cursor = 3
                    SelectedItemId = Some "alpha"
                    PendingConfirmation = None } }

    let screen = Layout.render model

    let allText =
        [ for row in 0 .. screen.Height - 1 ->
              [ for col in 0 .. screen.Width - 1 -> screen.Cells[row, col].Glyph ]
              |> Array.ofList
              |> System.String ]
        |> String.concat "\n"

    allText |> should haveSubstring "Plugins"
    allText |> should haveSubstring "alpha"
    allText |> should haveSubstring "loaded"
    allText |> should haveSubstring "1.0.0"
    allText |> should haveSubstring "/plugins/alpha"
    allText |> should haveSubstring "Plugins: alp"

    allText
    |> should not' (haveSubstring "+----------------------------------------------------------------------------+")

[<Fact>]
let ``terminal enables and restores Kitty keyboard protocol around the alternate screen`` () =
    use enterWriter = new StringWriter()

    let enterTerm =
        Terminal.createWithCapabilities enterWriter TerminalCapabilities.modern

    Terminal.enter enterTerm

    use leaveWriter = new StringWriter()

    let leaveTerm =
        Terminal.createWithCapabilities leaveWriter TerminalCapabilities.modern

    Terminal.leave leaveTerm

    enterWriter.ToString() |> should haveSubstring "\u001b[>1u"
    leaveWriter.ToString() |> should haveSubstring "\u001b[<u"
