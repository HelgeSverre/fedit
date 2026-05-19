namespace Fedit

type CliValue =
    | NoValue
    | RequiredValue of name: string

type CliOptionSpec<'Option> =
    { Names: string list
      Value: CliValue
      Option: 'Option }

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
