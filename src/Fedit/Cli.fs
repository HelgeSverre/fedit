namespace Fedit

open System
open System.Text

type CliValue =
    | NoValue
    | RequiredValue of name: string

type CliOptionSpec<'Option> =
    { Names: string list
      Value: CliValue
      Description: string
      Option: 'Option }

type CliPositional = { Name: string; Description: string }

type CliApp<'Option> =
    { Name: string
      Summary: string
      Positionals: CliPositional list
      Options: CliOptionSpec<'Option> list }

type CliParsed<'Option> =
    | Option of 'Option * value: string option
    | Argument of string

[<RequireQualifiedAccess>]
module Cli =
    let private tryFindSpec specs token =
        specs |> List.tryFind (fun spec -> spec.Names |> List.contains token)

    let parse specs (argv: string[]) =
        let rec loop tokens parsed =
            match tokens with
            | [] -> List.rev parsed

            | token :: value :: rest ->
                match tryFindSpec specs token with
                | Some spec ->
                    match spec.Value with
                    | NoValue -> loop (value :: rest) (Option(spec.Option, None) :: parsed)

                    | RequiredValue _ -> loop rest (Option(spec.Option, Some value) :: parsed)

                | None -> loop (value :: rest) (Argument token :: parsed)

            | token :: rest ->
                match tryFindSpec specs token with
                | Some spec ->
                    match spec.Value with
                    | NoValue -> loop rest (Option(spec.Option, None) :: parsed)

                    | RequiredValue _ -> loop rest parsed

                | None -> loop rest (Argument token :: parsed)

        loop (Array.toList argv) []

    // ─────────────────────────────────────────────────────────────────────
    // Help rendering
    // ─────────────────────────────────────────────────────────────────────

    let private isLong (name: string) =
        name.StartsWith("--", StringComparison.Ordinal)

    let private isShort (name: string) =
        name.StartsWith("-", StringComparison.Ordinal) && not (isLong name)

    let private valuePlaceholder value =
        match value with
        | NoValue -> ""
        | RequiredValue name -> $" <{name}>"

    let private optionLeftColumn (spec: CliOptionSpec<_>) =
        let short = spec.Names |> List.tryFind isShort
        let long = spec.Names |> List.tryFind isLong

        let shortText =
            match short with
            | Some s -> s
            | None -> "  "

        let separator =
            match short, long with
            | Some _, Some _ -> ", "
            | _ -> "  "

        let longText = long |> Option.defaultValue ""

        $"{shortText}{separator}{longText}{valuePlaceholder spec.Value}"

    let formatUsage (app: CliApp<_>) =
        let parts =
            [ yield $"Usage: {app.Name}"

              for positional in app.Positionals do
                  yield $"[<{positional.Name}>]"

              if not (List.isEmpty app.Options) then
                  yield "[options]" ]

        String.Join(' ', parts)

    let formatHelp (app: CliApp<_>) =
        let sb = StringBuilder()

        let appendLine (s: string) = sb.AppendLine s |> ignore

        if String.IsNullOrWhiteSpace app.Summary then
            appendLine app.Name
        else
            appendLine $"{app.Name} — {app.Summary}"

        appendLine ""
        appendLine (formatUsage app)

        if not (List.isEmpty app.Positionals) then
            appendLine ""
            appendLine "Arguments:"

            let positionalLefts = app.Positionals |> List.map (fun p -> $"<{p.Name}>")

            let posWidth = positionalLefts |> List.map String.length |> List.max

            for left, p in List.zip positionalLefts app.Positionals do
                appendLine $"  {left.PadRight(posWidth)}   {p.Description}"

        if not (List.isEmpty app.Options) then
            appendLine ""
            appendLine "Options:"

            let lefts = app.Options |> List.map optionLeftColumn
            let optWidth = lefts |> List.map String.length |> List.max

            for left, spec in List.zip lefts app.Options do
                appendLine $"  {left.PadRight(optWidth)}   {spec.Description}"

        sb.ToString().TrimEnd('\r', '\n')
