namespace Fedit.Cli

open System
open System.Text

type CliValue =
    | NoValue
    | RequiredValue of name: string

/// What the shell should suggest when the user tabs at this position.
/// `DynamicCommand` shells out to the running binary to enumerate
/// candidates (e.g. installed plugin names) on every Tab.
type CliCompletionKind =
    | NoHint
    | FilePath
    | DirectoryPath
    | DynamicCommand of args: string list
    | Choices of values: string list

type CliOptionSpec<'Option> =
    { Short: char option
      Long: string
      Value: CliValue
      Description: string
      Option: 'Option
      Completion: CliCompletionKind }

type CliPositional =
    { Name: string
      Description: string
      Completion: CliCompletionKind }

/// One subcommand exposed by a `CliApp`. `Aliases` are rendered in help;
/// `HiddenAliases` resolve at the router but never appear in help text —
/// the canonical `Name` is what the caller branches on.
type CliSubcommandSpec =
    { Name: string
      Aliases: string list
      HiddenAliases: string list
      Summary: string }

/// Type-erased projection of `CliOptionSpec<'Option>` used by the
/// completions generator. Built via `Parser.toOptionDescriptor` from the
/// real spec so the descriptor can't lie about parser config.
type CliOptionDescriptor =
    { Short: char option
      Long: string
      Value: CliValue
      Description: string
      Completion: CliCompletionKind }

/// Recursive description of a command + all its subcommands, with
/// enough metadata for the completions generator to emit shell scripts.
/// Each call site builds this tree by projecting from its own
/// `CliApp<_>` and nesting child descriptors.
type CliCommandDescriptor =
    { Name: string
      Aliases: string list
      HiddenAliases: string list
      Summary: string
      Positionals: CliPositional list
      Options: CliOptionDescriptor list
      Subcommands: CliCommandDescriptor list }

type CliApp<'Option> =
    { Name: string
      Summary: string
      Positionals: CliPositional list
      Options: CliOptionSpec<'Option> list
      Subcommands: CliSubcommandSpec list }

type CliError =
    | UnknownFlag of token: string * suggestions: string list
    | MissingValue of flag: string

type CliParsed<'Option> =
    | Option of 'Option * value: string option
    | Argument of string

[<RequireQualifiedAccess>]
module Parser =

    // ─────────────────────────────────────────────────────────────────────
    // Descriptor projection
    // ─────────────────────────────────────────────────────────────────────

    /// Erase the `'Option` type parameter so the spec can sit inside a
    /// `CliCommandDescriptor` tree alongside specs from other subcommands.
    let toOptionDescriptor (spec: CliOptionSpec<_>) : CliOptionDescriptor =
        { Short = spec.Short
          Long = spec.Long
          Value = spec.Value
          Description = spec.Description
          Completion = spec.Completion }

    // ─────────────────────────────────────────────────────────────────────
    // Spec lookup
    // ─────────────────────────────────────────────────────────────────────

    let private trimLong (token: string) = token.Substring 2

    let private tryFindLong (specs: CliOptionSpec<'a> list) (longName: string) =
        specs |> List.tryFind (fun spec -> spec.Long = longName)

    let private tryFindShort (specs: CliOptionSpec<'a> list) (c: char) =
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
    // Subcommand routing
    // ─────────────────────────────────────────────────────────────────────

    /// Match `argv.[0]` against a subcommand spec's `Name`, `Aliases`, or
    /// `HiddenAliases`. On match, returns the canonical `Name` plus the
    /// remaining argv (so callers always branch on one string regardless of
    /// which surface form the user typed). Returns `None` when argv is
    /// empty, the first token looks like a flag (`-…`), or no spec matches.
    let route (specs: CliSubcommandSpec list) (argv: string[]) : (string * string[]) option =
        match Array.tryHead argv with
        | None -> None
        | Some token when token.StartsWith("-", StringComparison.Ordinal) -> None
        | Some token ->
            specs
            |> List.tryPick (fun spec ->
                if
                    spec.Name = token
                    || List.contains token spec.Aliases
                    || List.contains token spec.HiddenAliases
                then
                    Some(spec.Name, Array.skip 1 argv)
                else
                    None)

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

              if not (List.isEmpty app.Subcommands) then
                  yield "[<command>]"

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

        if not (List.isEmpty app.Subcommands) then
            appendLine ""
            appendLine "Commands:"

            let subLeft (spec: CliSubcommandSpec) =
                match spec.Aliases with
                | [] -> spec.Name
                | aliases -> $"""{spec.Name} ({String.Join(", ", aliases)})"""

            let lefts = app.Subcommands |> List.map subLeft
            let subWidth = lefts |> List.map String.length |> List.max

            for left, spec in List.zip lefts app.Subcommands do
                appendLine $"  {left.PadRight(subWidth)}   {spec.Summary}"

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
