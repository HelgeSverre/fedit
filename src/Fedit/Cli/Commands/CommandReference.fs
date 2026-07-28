/// `fedit commands [--json]` — dump the built-in command-palette specs.
///
/// The website consumes the JSON form so its command reference is generated
/// from the same `Commands.specs` values the prompt parser and completion menu
/// use. Constructors are deliberately excluded: the reference surface is the
/// stable name/usage/summary/visibility metadata.
module Fedit.Cli.Commands.CommandReference

open System.Text
open Fedit
open Fedit.Cli

let private jsonEscape (value: string) =
    let sb = StringBuilder()

    for c in value do
        match c with
        | '"' -> sb.Append("\\\"") |> ignore
        | '\\' -> sb.Append("\\\\") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> sb.Append("\\r") |> ignore
        | '\t' -> sb.Append("\\t") |> ignore
        | c when int c < 0x20 -> sb.Append("\\u" + (int c).ToString("x4")) |> ignore
        | c -> sb.Append(c) |> ignore

    sb.ToString()

let private field name value =
    "\"" + name + "\": \"" + jsonEscape value + "\""

let private specJson (spec: Commands.Spec) =
    let pairs =
        [ field "name" spec.Name
          field "usage" spec.Usage
          field "summary" spec.Summary
          "\"hidden\": " + (if spec.Hidden then "true" else "false") ]

    "  { " + String.concat ", " pairs + " }"

/// Serialize command specs to a JSON array. Ends with a newline.
let toJson (specs: Commands.Spec list) : string =
    "[\n" + (specs |> List.map specJson |> String.concat ",\n") + "\n]\n"

let private renderTable (specs: Commands.Spec list) =
    let usageWidth =
        specs
        |> List.map (fun spec -> spec.Usage.Length)
        |> (fun lengths -> if List.isEmpty lengths then 0 else List.max lengths)

    let sb = StringBuilder()

    for spec in specs do
        let visibility = if spec.Hidden then "  [hidden]" else ""

        sb.AppendLine(spec.Usage.PadRight usageWidth + "  " + spec.Summary + visibility)
        |> ignore

    sb.ToString()

type private CommandReferenceOpt =
    | CommandReferenceHelp
    | CommandReferenceJson

let private commandsApp: CliApp<CommandReferenceOpt> =
    { Name = "fedit commands"
      Summary = "Print the built-in command-palette reference"
      Positionals = []
      Options =
        [ { Short = Some 'h'
            Long = "help"
            Value = NoValue
            Description = "Show this help and exit"
            Option = CommandReferenceHelp
            Completion = NoHint }
          { Short = None
            Long = "json"
            Value = NoValue
            Description = "Emit the commands as a JSON array"
            Option = CommandReferenceJson
            Completion = NoHint } ]
      Subcommands = [] }

/// Descriptor for the `commands` subcommand. Exported so the top-level
/// descriptor in `Program.fs` can nest it.
let descriptor: CliCommandDescriptor =
    { Name = "commands"
      Aliases = []
      HiddenAliases = []
      Summary = commandsApp.Summary
      Positionals = commandsApp.Positionals
      Options = commandsApp.Options |> List.map Parser.toOptionDescriptor
      Subcommands = [] }

let private wantsHelp items =
    items
    |> List.exists (function
        | Option(CommandReferenceHelp, _) -> true
        | _ -> false)

let private wantsJson items =
    items
    |> List.exists (function
        | Option(CommandReferenceJson, _) -> true
        | _ -> false)

let run (argv: string[]) : int =
    match Parser.parse commandsApp.Options argv with
    | Result.Error errors ->
        System.Console.Error.WriteLine(Parser.formatErrors commandsApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        System.Console.Out.WriteLine(Parser.formatHelp commandsApp)
        0
    | Result.Ok items ->
        if wantsJson items then
            System.Console.Out.Write(toJson Commands.specs)
        else
            System.Console.Out.Write(renderTable Commands.specs)

        0
