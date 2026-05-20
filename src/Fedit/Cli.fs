namespace Fedit

open System
open System.Text

type CliValue =
    | NoValue
    | RequiredValue of name: string

type CliOptionSpec<'Option> =
    { Short: char option
      Long: string
      Value: CliValue
      Description: string
      Option: 'Option }

type CliPositional = { Name: string; Description: string }

type CliApp<'Option> =
    { Name: string
      Summary: string
      Positionals: CliPositional list
      Options: CliOptionSpec<'Option> list }

type CliError =
    | UnknownFlag of token: string * suggestions: string list
    | MissingValue of flag: string

type CliParsed<'Option> =
    | Option of 'Option * value: string option
    | Argument of string

[<RequireQualifiedAccess>]
module Cli =

    // ─────────────────────────────────────────────────────────────────────
    // Spec lookup
    // ─────────────────────────────────────────────────────────────────────

    let private trimLong (token: string) = token.Substring 2

    let private tryFindLong specs (longName: string) =
        specs |> List.tryFind (fun spec -> spec.Long = longName)

    let private tryFindShort specs (c: char) =
        specs |> List.tryFind (fun spec -> spec.Short = Some c)

    // ─────────────────────────────────────────────────────────────────────
    // Levenshtein-based "did you mean" suggestions
    // ─────────────────────────────────────────────────────────────────────

    let private levenshtein (a: string) (b: string) =
        let la, lb = a.Length, b.Length

        if la = 0 then
            lb
        elif lb = 0 then
            la
        else
            let prev = Array.init (lb + 1) id
            let curr = Array.zeroCreate (lb + 1)

            for i in 1..la do
                curr[0] <- i

                for j in 1..lb do
                    let cost = if a[i - 1] = b[j - 1] then 0 else 1
                    curr[j] <- min (min (curr[j - 1] + 1) (prev[j] + 1)) (prev[j - 1] + cost)

                Array.blit curr 0 prev 0 (lb + 1)

            prev[lb]

    let private suggestionsFor (specs: CliOptionSpec<_> list) (token: string) =
        if not (token.StartsWith("--", StringComparison.Ordinal)) then
            []
        else
            let needle = trimLong token

            specs
            |> List.map (fun spec -> spec.Long, levenshtein needle spec.Long)
            |> List.filter (fun (_, d) -> d <= 2)
            |> List.sortWith (fun (n1, d1) (n2, d2) -> if d1 <> d2 then compare d1 d2 else compare n1 n2)
            |> List.truncate 3
            |> List.map (fun (long, _) -> $"--{long}")

    // ─────────────────────────────────────────────────────────────────────
    // Tokenizer / parser
    // ─────────────────────────────────────────────────────────────────────

    let private isLongToken (token: string) =
        token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2

    let private isShortToken (token: string) =
        token.StartsWith("-", StringComparison.Ordinal)
        && token.Length >= 2
        && not (token.StartsWith("--", StringComparison.Ordinal))

    let parse (specs: CliOptionSpec<'Option> list) (argv: string[]) : Result<CliParsed<'Option> list, CliError list> =

        let rec loop tokens parsed errors =
            match tokens with
            | [] -> List.rev parsed, List.rev errors

            // End-of-flags sentinel: everything after `--` is positional.
            | "--" :: rest ->
                let trailing = rest |> List.map Argument
                (List.rev parsed) @ trailing, List.rev errors

            // Long flag, possibly with inline `=value`.
            | token :: rest when isLongToken token ->
                let name, inlineValue =
                    let body = trimLong token
                    let eq = body.IndexOf '='

                    if eq < 0 then
                        body, None
                    else
                        body.Substring(0, eq), Some(body.Substring(eq + 1))

                match tryFindLong specs name with
                | Some spec ->
                    match spec.Value, inlineValue with
                    | NoValue, None -> loop rest (Option(spec.Option, None) :: parsed) errors
                    | NoValue, Some _ ->
                        // `--flag=value` on a NoValue flag is an error.
                        loop rest parsed (UnknownFlag(token, []) :: errors)
                    | RequiredValue _, Some value -> loop rest (Option(spec.Option, Some value) :: parsed) errors
                    | RequiredValue _, None ->
                        match rest with
                        | next :: tail -> loop tail (Option(spec.Option, Some next) :: parsed) errors
                        | [] -> loop [] parsed (MissingValue $"--{name}" :: errors)
                | None ->
                    let suggestions = suggestionsFor specs $"--{name}"
                    loop rest parsed (UnknownFlag($"--{name}", suggestions) :: errors)

            // Short flag: `-X`. We do not currently support clustering or `=` for shorts.
            | token :: rest when isShortToken token ->
                let c = token[1]

                if token.Length > 2 then
                    // Could be `-Xvalue` or clustering — neither is supported yet.
                    loop rest parsed (UnknownFlag(token, []) :: errors)
                else
                    match tryFindShort specs c with
                    | Some spec ->
                        match spec.Value with
                        | NoValue -> loop rest (Option(spec.Option, None) :: parsed) errors
                        | RequiredValue _ ->
                            match rest with
                            | next :: tail -> loop tail (Option(spec.Option, Some next) :: parsed) errors
                            | [] -> loop [] parsed (MissingValue token :: errors)
                    | None -> loop rest parsed (UnknownFlag(token, []) :: errors)

            // Plain positional.
            | token :: rest -> loop rest (Argument token :: parsed) errors

        let parsed, errors = loop (Array.toList argv) [] []

        if List.isEmpty errors then
            Result.Ok parsed
        else
            Result.Error errors

    // ─────────────────────────────────────────────────────────────────────
    // Help rendering
    // ─────────────────────────────────────────────────────────────────────

    let private valuePlaceholder value =
        match value with
        | NoValue -> ""
        | RequiredValue name -> $" <{name}>"

    let private optionLeftColumn (spec: CliOptionSpec<_>) =
        let shortText =
            match spec.Short with
            | Some c -> $"-{c}"
            | None -> "  "

        let separator =
            match spec.Short with
            | Some _ -> ", "
            | None -> "  "

        $"{shortText}{separator}--{spec.Long}{valuePlaceholder spec.Value}"

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

    // ─────────────────────────────────────────────────────────────────────
    // Error rendering
    // ─────────────────────────────────────────────────────────────────────

    let private formatError (app: CliApp<_>) (error: CliError) =
        let sb = StringBuilder()

        match error with
        | UnknownFlag(token, suggestions) ->
            sb.AppendLine($"{app.Name}: unknown flag '{token}'") |> ignore

            match suggestions with
            | [] -> ()
            | first :: _ ->
                sb.AppendLine() |> ignore
                sb.AppendLine($"  Did you mean '{first}'?") |> ignore

        | MissingValue flag -> sb.AppendLine($"{app.Name}: flag '{flag}' requires a value") |> ignore

        sb.ToString().TrimEnd('\r', '\n')

    let formatErrors (app: CliApp<_>) (errors: CliError list) =
        let blocks = errors |> List.map (formatError app)
        let joined = String.Join("\n\n", blocks)
        $"{joined}\n\nRun '{app.Name} --help' for usage."
