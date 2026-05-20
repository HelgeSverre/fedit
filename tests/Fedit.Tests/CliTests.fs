module Fedit.Tests.CliTests

open Fedit
open Xunit
open FsUnit.Xunit

// ─────────────────────────────────────────────────────────────────────
// Fixture: a small spec covering both option shapes
// ─────────────────────────────────────────────────────────────────────

type private Opt =
    | Flag
    | Log
    | Help

let private specs: CliOptionSpec<Opt> list =
    [ { Names = [ "-h"; "--help" ]
        Value = NoValue
        Description = "Show this help and exit"
        Option = Help }
      { Names = [ "-f"; "--flag" ]
        Value = NoValue
        Description = "A boolean flag"
        Option = Flag }
      { Names = [ "--log" ]
        Value = RequiredValue "path"
        Description = "Append trace to <path>"
        Option = Log } ]

let private app: CliApp<Opt> =
    { Name = "tool"
      Summary = "a test tool"
      Positionals =
        [ { Name = "path"
            Description = "Workspace directory" } ]
      Options = specs }

// ─────────────────────────────────────────────────────────────────────
// parse
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``empty argv yields no parsed items`` () =
    Cli.parse specs [||] |> should equal ([]: CliParsed<Opt> list)

[<Fact>]
let ``single positional is preserved`` () =
    Cli.parse specs [| "foo" |]
    |> should equal ([ Argument "foo" ]: CliParsed<Opt> list)

[<Fact>]
let ``positionals preserve order`` () =
    Cli.parse specs [| "a"; "b" |]
    |> should equal ([ Argument "a"; Argument "b" ]: CliParsed<Opt> list)

[<Fact>]
let ``NoValue flag emits Option with None`` () =
    Cli.parse specs [| "--flag" |] |> should equal [ Option(Flag, None) ]

[<Fact>]
let ``RequiredValue flag consumes the next token`` () =
    Cli.parse specs [| "--log"; "trace.log" |]
    |> should equal [ Option(Log, Some "trace.log") ]

[<Fact>]
let ``RequiredValue flag at end of argv is dropped`` () =
    // Current behavior — no error reporting; the lone --log is swallowed.
    Cli.parse specs [| "--log" |] |> should equal ([]: CliParsed<Opt> list)

[<Fact>]
let ``unknown token is treated as a positional argument`` () =
    Cli.parse specs [| "--unknown" |]
    |> should equal ([ Argument "--unknown" ]: CliParsed<Opt> list)

[<Fact>]
let ``mixed argv preserves overall order`` () =
    Cli.parse specs [| "a"; "--log"; "x"; "b" |]
    |> should equal [ Argument "a"; Option(Log, Some "x"); Argument "b" ]

[<Fact>]
let ``short alias resolves the same as long`` () =
    Cli.parse specs [| "-h" |] |> should equal (Cli.parse specs [| "--help" |])

[<Fact>]
let ``short and long form of a NoValue flag both emit one Option`` () =
    Cli.parse specs [| "-f" |] |> should equal [ Option(Flag, None) ]

// ─────────────────────────────────────────────────────────────────────
// formatUsage
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``formatUsage includes program name`` () =
    Cli.formatUsage app |> should haveSubstring "Usage: tool"

[<Fact>]
let ``formatUsage includes positional placeholder`` () =
    Cli.formatUsage app |> should haveSubstring "[<path>]"

[<Fact>]
let ``formatUsage includes [options] when options exist`` () =
    Cli.formatUsage app |> should haveSubstring "[options]"

[<Fact>]
let ``formatUsage omits [options] when no options are declared`` () =
    let appNoOpts = { app with Options = [] }
    Cli.formatUsage appNoOpts |> should not' (haveSubstring "[options]")

// ─────────────────────────────────────────────────────────────────────
// formatHelp
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``formatHelp includes the summary line`` () =
    Cli.formatHelp app |> should haveSubstring "tool — a test tool"

[<Fact>]
let ``formatHelp includes every option's long name`` () =
    let help = Cli.formatHelp app
    help |> should haveSubstring "--help"
    help |> should haveSubstring "--flag"
    help |> should haveSubstring "--log"

[<Fact>]
let ``formatHelp includes every option's description`` () =
    let help = Cli.formatHelp app

    for spec in specs do
        help |> should haveSubstring spec.Description

[<Fact>]
let ``formatHelp shows the value placeholder for RequiredValue options`` () =
    Cli.formatHelp app |> should haveSubstring "--log <path>"

[<Fact>]
let ``formatHelp omits Arguments section when there are no positionals`` () =
    let appNoArgs = { app with Positionals = [] }
    Cli.formatHelp appNoArgs |> should not' (haveSubstring "Arguments:")

[<Fact>]
let ``formatHelp aligns descriptions across options with and without short flags`` () =
    // The line beginning with "  -h, --help" and the line beginning with
    // "      --log" should put their descriptions in the same column.
    let lines = Cli.formatHelp app |> fun s -> s.Split('\n')

    let descColumn (prefix: string) (desc: string) =
        lines
        |> Array.tryFind (fun line -> line.StartsWith(prefix))
        |> Option.map (fun line -> line.IndexOf(desc))

    let helpCol = descColumn "  -h, --help" "Show this help"
    let logCol = descColumn "      --log" "Append trace"

    helpCol |> should not' (equal (Some -1))
    helpCol |> should equal logCol
