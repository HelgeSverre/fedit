module Fedit.Tests.CliTests

open Fedit.Cli
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
    [ { Short = Some 'h'
        Long = "help"
        Value = NoValue
        Description = "Show this help and exit"
        Option = Help
        Completion = NoHint }
      { Short = Some 'f'
        Long = "flag"
        Value = NoValue
        Description = "A boolean flag"
        Option = Flag
        Completion = NoHint }
      { Short = None
        Long = "log"
        Value = RequiredValue "path"
        Description = "Append trace to <path>"
        Option = Log
        Completion = FilePath } ]

let private app: CliApp<Opt> =
    { Name = "tool"
      Summary = "a test tool"
      Positionals =
        [ { Name = "path"
            Description = "Workspace directory"
            Completion = FilePath } ]
      Options = specs
      Subcommands = [] }

// Small helpers so tests stay readable. `okParsed` asserts Result.Ok and
// returns the parsed list; `errors` asserts Result.Error and returns the
// error list. Both call `should` themselves so the test body can focus on
// the interesting assertion.

let private okParsed (result: Result<CliParsed<Opt> list, CliError list>) =
    match result with
    | Result.Ok items -> items
    | Result.Error errs -> failwithf "expected Ok, got Error %A" errs

let private errors (result: Result<CliParsed<Opt> list, CliError list>) =
    match result with
    | Result.Ok items -> failwithf "expected Error, got Ok %A" items
    | Result.Error errs -> errs

// ─────────────────────────────────────────────────────────────────────
// parse — happy paths
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``empty argv yields no parsed items`` () =
    Parser.parse specs [||] |> okParsed |> should equal ([]: CliParsed<Opt> list)

[<Fact>]
let ``single positional is preserved`` () =
    Parser.parse specs [| "foo" |]
    |> okParsed
    |> should equal ([ Argument "foo" ]: CliParsed<Opt> list)

[<Fact>]
let ``positionals preserve order`` () =
    Parser.parse specs [| "a"; "b" |]
    |> okParsed
    |> should equal ([ Argument "a"; Argument "b" ]: CliParsed<Opt> list)

[<Fact>]
let ``NoValue flag emits Option with None`` () =
    Parser.parse specs [| "--flag" |]
    |> okParsed
    |> should equal [ Option(Flag, None) ]

[<Fact>]
let ``RequiredValue flag consumes the next token`` () =
    Parser.parse specs [| "--log"; "trace.log" |]
    |> okParsed
    |> should equal [ Option(Log, Some "trace.log") ]

[<Fact>]
let ``mixed argv preserves overall order`` () =
    Parser.parse specs [| "a"; "--log"; "x"; "b" |]
    |> okParsed
    |> should equal [ Argument "a"; Option(Log, Some "x"); Argument "b" ]

[<Fact>]
let ``short alias resolves the same as long`` () =
    let shortResult = Parser.parse specs [| "-h" |]
    let longResult = Parser.parse specs [| "--help" |]
    shortResult |> should equal longResult

[<Fact>]
let ``short and long form of a NoValue flag both emit one Option`` () =
    Parser.parse specs [| "-f" |] |> okParsed |> should equal [ Option(Flag, None) ]

// ─────────────────────────────────────────────────────────────────────
// parse — `--flag=value` syntax
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``long flag accepts inline =value`` () =
    Parser.parse specs [| "--log=trace.log" |]
    |> okParsed
    |> should equal [ Option(Log, Some "trace.log") ]

[<Fact>]
let ``--flag= accepts empty value for a RequiredValue option`` () =
    Parser.parse specs [| "--log=" |]
    |> okParsed
    |> should equal [ Option(Log, Some "") ]

[<Fact>]
let ``--flag=value is rejected on a NoValue option`` () =
    let errs = Parser.parse specs [| "--flag=true" |] |> errors

    match errs with
    | [ UnknownFlag(token, _) ] -> token |> should equal "--flag=true"
    | other -> failwithf "expected [UnknownFlag …], got %A" other

// ─────────────────────────────────────────────────────────────────────
// parse — end-of-flags sentinel
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``-- sentinel turns subsequent tokens into positionals`` () =
    Parser.parse specs [| "--"; "--whatver"; "foo" |]
    |> okParsed
    |> should equal ([ Argument "--whatver"; Argument "foo" ]: CliParsed<Opt> list)

[<Fact>]
let ``-- sentinel still permits flags before it`` () =
    Parser.parse specs [| "--flag"; "--"; "-x" |]
    |> okParsed
    |> should equal [ Option(Flag, None); Argument "-x" ]

// ─────────────────────────────────────────────────────────────────────
// parse — error cases (the bug fix)
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``unknown long flag yields UnknownFlag error`` () =
    let errs = Parser.parse specs [| "--whatver" |] |> errors

    match errs with
    | [ UnknownFlag(token, _) ] -> token |> should equal "--whatver"
    | other -> failwithf "expected single UnknownFlag, got %A" other

[<Fact>]
let ``unknown short flag yields UnknownFlag error`` () =
    let errs = Parser.parse specs [| "-x" |] |> errors

    match errs with
    | [ UnknownFlag(token, []) ] -> token |> should equal "-x"
    | other -> failwithf "expected UnknownFlag with no suggestions, got %A" other

[<Fact>]
let ``RequiredValue flag at end of argv yields MissingValue error`` () =
    let errs = Parser.parse specs [| "--log" |] |> errors
    errs |> should equal [ MissingValue "--log" ]

[<Fact>]
let ``multiple errors are collected in source order`` () =
    // Two unknown longs in a row; the parser must keep going after the first
    // error so both are reported in one pass.
    let errs = Parser.parse specs [| "--xx"; "--yy" |] |> errors

    match errs with
    | [ UnknownFlag("--xx", _); UnknownFlag("--yy", _) ] -> ()
    | other -> failwithf "expected two UnknownFlag errors in order, got %A" other

[<Fact>]
let ``RequiredValue flag followed by another flag eats it greedily`` () =
    // Pin current behavior: `--log` is greedy and takes whatever follows it,
    // even if that token looks like another flag. Documents the trade-off
    // and protects against accidental change.
    Parser.parse specs [| "--log"; "--whatver" |]
    |> okParsed
    |> should equal [ Option(Log, Some "--whatver") ]

[<Fact>]
let ``unknown flag with similar name suggests the real one`` () =
    let errs = Parser.parse specs [| "--hlp" |] |> errors

    match errs with
    | [ UnknownFlag(_, suggestions) ] -> suggestions |> should contain "--help"
    | other -> failwithf "expected suggestion list, got %A" other

[<Fact>]
let ``unknown flag with very different name has no suggestions`` () =
    let errs = Parser.parse specs [| "--zzzzzz" |] |> errors

    match errs with
    | [ UnknownFlag(_, suggestions) ] -> suggestions |> should equal ([]: string list)
    | other -> failwithf "expected empty suggestion list, got %A" other

// ─────────────────────────────────────────────────────────────────────
// formatUsage
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``formatUsage includes program name`` () =
    Parser.formatUsage app |> should haveSubstring "Usage: tool"

[<Fact>]
let ``formatUsage includes positional placeholder`` () =
    Parser.formatUsage app |> should haveSubstring "[<path>]"

[<Fact>]
let ``formatUsage includes [options] when options exist`` () =
    Parser.formatUsage app |> should haveSubstring "[options]"

[<Fact>]
let ``formatUsage omits [options] when no options are declared`` () =
    let appNoOpts = { app with Options = [] }
    Parser.formatUsage appNoOpts |> should not' (haveSubstring "[options]")

// ─────────────────────────────────────────────────────────────────────
// formatHelp
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``formatHelp includes the summary line`` () =
    Parser.formatHelp app |> should haveSubstring "tool — a test tool"

[<Fact>]
let ``formatHelp includes every option's long name`` () =
    let help = Parser.formatHelp app
    help |> should haveSubstring "--help"
    help |> should haveSubstring "--flag"
    help |> should haveSubstring "--log"

[<Fact>]
let ``formatHelp includes every option's description`` () =
    let help = Parser.formatHelp app

    for spec in specs do
        help |> should haveSubstring spec.Description

[<Fact>]
let ``formatHelp shows the value placeholder for RequiredValue options`` () =
    Parser.formatHelp app |> should haveSubstring "--log <path>"

[<Fact>]
let ``formatHelp omits Arguments section when there are no positionals`` () =
    let appNoArgs = { app with Positionals = [] }
    Parser.formatHelp appNoArgs |> should not' (haveSubstring "Arguments:")

[<Fact>]
let ``formatHelp aligns descriptions across options with and without short flags`` () =
    let lines = Parser.formatHelp app |> fun s -> s.Split('\n')

    let descColumn (prefix: string) (desc: string) =
        lines
        |> Array.tryFind (fun line -> line.StartsWith(prefix))
        |> Option.map (fun line -> line.IndexOf(desc))

    let helpCol = descColumn "  -h, --help" "Show this help"
    let logCol = descColumn "      --log" "Append trace"

    helpCol |> should not' (equal (Some -1))
    helpCol |> should equal logCol

// ─────────────────────────────────────────────────────────────────────
// formatErrors
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``formatErrors renders unknown-flag header`` () =
    let rendered = Parser.formatErrors app [ UnknownFlag("--whatver", []) ]
    rendered |> should haveSubstring "tool: unknown flag '--whatver'"

[<Fact>]
let ``formatErrors includes the Did-you-mean line only when suggestions exist`` () =
    let withHint = Parser.formatErrors app [ UnknownFlag("--hlp", [ "--help" ]) ]
    withHint |> should haveSubstring "Did you mean '--help'?"

    let withoutHint = Parser.formatErrors app [ UnknownFlag("--zzz", []) ]
    withoutHint |> should not' (haveSubstring "Did you mean")

[<Fact>]
let ``formatErrors renders missing-value message`` () =
    let rendered = Parser.formatErrors app [ MissingValue "--log" ]
    rendered |> should haveSubstring "flag '--log' requires a value"

[<Fact>]
let ``formatErrors always appends the usage footer`` () =
    let rendered = Parser.formatErrors app [ MissingValue "--log" ]
    rendered |> should haveSubstring "Run 'tool --help' for usage."

[<Fact>]
let ``formatErrors lists multiple errors`` () =
    let rendered =
        Parser.formatErrors app [ MissingValue "--log"; UnknownFlag("--whatver", []) ]

    rendered |> should haveSubstring "flag '--log' requires a value"
    rendered |> should haveSubstring "unknown flag '--whatver'"

// ─────────────────────────────────────────────────────────────────────
// Subcommand routing
// ─────────────────────────────────────────────────────────────────────

let private subSpecs: CliSubcommandSpec list =
    [ { Name = "plugins"
        Aliases = []
        HiddenAliases = [ "plugin" ]
        Summary = "Manage installed plugins" }
      { Name = "list"
        Aliases = [ "ls" ]
        HiddenAliases = []
        Summary = "List things" } ]

// FsUnit's `should equal` collapses to reference equality for arrays, so
// route tests pattern-match the tuple and compare the remaining argv as a
// list (which has structural equality).

[<Fact>]
let ``route matches canonical name and returns it verbatim with remaining argv`` () =
    match Parser.route subSpecs [| "plugins"; "install"; "/path" |] with
    | Some(name, rest) ->
        name |> should equal "plugins"
        List.ofArray rest |> should equal [ "install"; "/path" ]
    | None -> failwith "expected Some, got None"

[<Fact>]
let ``route matches a visible alias and rewrites to canonical name`` () =
    // `ls` is the visible alias for `list`; callers always branch on the
    // canonical name so they don't repeat the alias table.
    match Parser.route subSpecs [| "ls" |] with
    | Some(name, rest) ->
        name |> should equal "list"
        List.ofArray rest |> should equal ([]: string list)
    | None -> failwith "expected Some, got None"

[<Fact>]
let ``route matches a hidden alias and rewrites to canonical name`` () =
    // The singular form `plugin` is the hidden alias — it must resolve
    // identically to `plugins` so `fedit plugin install …` works.
    match Parser.route subSpecs [| "plugin"; "install"; "/p" |] with
    | Some(name, rest) ->
        name |> should equal "plugins"
        List.ofArray rest |> should equal [ "install"; "/p" ]
    | None -> failwith "expected Some, got None"

[<Fact>]
let ``route returns None when the first token looks like a flag`` () =
    Parser.route subSpecs [| "--help" |] |> Option.isNone |> should be True

[<Fact>]
let ``route returns None for an unknown first token`` () =
    Parser.route subSpecs [| "wat" |] |> Option.isNone |> should be True

[<Fact>]
let ``route returns None for empty argv`` () =
    Parser.route subSpecs [||] |> Option.isNone |> should be True

// ─────────────────────────────────────────────────────────────────────
// Subcommand rendering in help
// ─────────────────────────────────────────────────────────────────────

let private appWithSubs: CliApp<Opt> = { app with Subcommands = subSpecs }

[<Fact>]
let ``formatHelp includes a Commands section when subcommands exist`` () =
    Parser.formatHelp appWithSubs |> should haveSubstring "Commands:"

[<Fact>]
let ``formatHelp renders visible aliases next to the canonical name`` () =
    let help = Parser.formatHelp appWithSubs
    // `list (ls)` — the visible alias is shown in parens.
    help |> should haveSubstring "list (ls)"

[<Fact>]
let ``formatHelp omits hidden aliases entirely`` () =
    let help = Parser.formatHelp appWithSubs
    help |> should haveSubstring "plugins"
    // The singular form is the hidden alias — it must not appear anywhere
    // in the rendered help. Match on the parenthesized form because the
    // canonical `plugins` legitimately contains the substring `plugin`.
    help |> should not' (haveSubstring "(plugin")
    help |> should not' (haveSubstring "plugin,")
    help |> should not' (haveSubstring "plugin)")

[<Fact>]
let ``formatUsage includes [<command>] when subcommands exist`` () =
    Parser.formatUsage appWithSubs |> should haveSubstring "[<command>]"

[<Fact>]
let ``formatUsage omits [<command>] when no subcommands are declared`` () =
    Parser.formatUsage app |> should not' (haveSubstring "[<command>]")
