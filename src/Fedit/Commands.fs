namespace Fedit

open System
open System.IO


type Command =
    | Open of string
    | Write
    | WriteAs of string
    | Quit
    | ToggleSidebar
    | FocusTree
    | FocusEditor
    | ReloadWorkspace
    | NextBuffer
    | PreviousBuffer
    | Theme of string
    | Recent of string
    | SwitchBuffer of string
    | Goto of line: int * column: int option

type ParsedCommand =
    | Empty
    | Ready of Command
    | Pending of string
    | Invalid of string

type CommandContext =
    { RootPath: string
      Files: string list
      Recent: string list
      Buffers: (int * string * string option) list
      Themes: Theme list
      CompletionLimit: int }

[<RequireQualifiedAccess>]
module Commands =
    type Spec =
        { Name: string
          Usage: string
          Summary: string
          Constructor: string -> ParsedCommand }

    let private simple command argument =
        if String.IsNullOrWhiteSpace argument then
            Ready command
        else
            Invalid "This command does not take arguments."

    // todo: could probably be named better
    let specs =
        [ { Name = "open"
            Usage = "open <path>"
            Summary = "Open a file from the workspace."
            Constructor =
              fun argument ->
                  if String.IsNullOrWhiteSpace argument then
                      Pending "Path is required."
                  else
                      Ready(Open(argument.Trim())) }
          { Name = "write"
            Usage = "write"
            Summary = "Save the active buffer."
            Constructor = simple Write }
          { Name = "writeas"
            Usage = "writeas <path>"
            Summary = "Save the active buffer to a new path."
            Constructor =
              fun argument ->
                  if String.IsNullOrWhiteSpace argument then
                      Pending "Target path is required."
                  else
                      Ready(WriteAs(argument.Trim())) }
          { Name = "quit"
            Usage = "quit"
            Summary = "Exit fedit."
            Constructor = simple Quit }
          { Name = "sidebar"
            Usage = "sidebar"
            Summary = "Toggle the sidebar."
            Constructor = simple ToggleSidebar }
          { Name = "tree"
            Usage = "tree"
            Summary = "Focus the file tree."
            Constructor = simple FocusTree }
          { Name = "editor"
            Usage = "editor"
            Summary = "Focus the editor."
            Constructor = simple FocusEditor }
          { Name = "reload"
            Usage = "reload"
            Summary = "Reload the workspace tree."
            Constructor = simple ReloadWorkspace }
          { Name = "next"
            Usage = "next"
            Summary = "Activate the next open buffer."
            Constructor = simple NextBuffer }
          { Name = "prev"
            Usage = "prev"
            Summary = "Activate the previous open buffer."
            Constructor = simple PreviousBuffer }
          { Name = "theme"
            Usage = "theme <name>"
            Summary = "Switch accent color. Bundled palettes plus any user themes from ~/.config/fedit/themes/*.json."
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Theme name required."
                  else
                      Ready(Theme trimmed) }
          { Name = "recent"
            Usage = "recent <path>"
            Summary = "Open a recently used file."
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Recent file required."
                  else
                      Ready(Recent trimmed) }
          { Name = "buffers"
            Usage = "buffers <id-or-name>"
            Summary = "Switch to an open buffer."
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Buffer id or name required."
                  else
                      Ready(SwitchBuffer trimmed) } ]

    /// Parse a `:LINE` or `:LINE:COL` argument (the text after the `:` prefix).
    /// Used by the prompt's Goto mode.
    let parseGoto (argument: string) =
        let trimmed = argument.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            Pending "Line number required."
        else
            let parts = trimmed.Split ':'

            let tryPositive (s: string) =
                match Int32.TryParse s with
                | true, n when n > 0 -> Some n
                | _ -> None

            match parts with
            | [| lineText |] ->
                match tryPositive lineText with
                | Some line -> Ready(Goto(line, None))
                | None -> Invalid $"'{trimmed}' is not a valid line number."
            | [| lineText; colText |] ->
                match tryPositive lineText, tryPositive colText with
                | Some line, Some col -> Ready(Goto(line, Some col))
                | _ -> Invalid $"'{trimmed}' must be <line>:<column> with positive numbers."
            | _ -> Invalid $"'{trimmed}' has too many ':' separators."

    let parse (input: string) =
        let trimmed = input.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            Empty
        else
            let firstSpace = trimmed.IndexOf ' '

            let name, argument =
                if firstSpace < 0 then
                    trimmed, ""
                else
                    trimmed[.. firstSpace - 1], trimmed[firstSpace + 1 ..]

            match specs |> List.tryFind (fun spec -> spec.Name = name.ToLowerInvariant()) with
            | Some spec -> spec.Constructor argument
            | None when
                specs
                |> List.exists (fun spec -> spec.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                ->
                Pending "Command is incomplete."
            | None -> Invalid $"Unknown command '{name}'."

    let completions context (input: string) =
        let trimmed = input.TrimStart()

        if String.IsNullOrWhiteSpace trimmed then
            specs
            |> List.map (fun spec ->
                { Label = spec.Name
                  ApplyText = spec.Name
                  Detail = spec.Summary
                  Kind = Command })
        else
            let firstSpace = trimmed.IndexOf ' '

            if firstSpace < 0 then
                specs
                |> List.filter (fun spec -> spec.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                |> List.map (fun spec ->
                    { Label = spec.Name
                      ApplyText = spec.Name
                      Detail = spec.Summary
                      Kind = Command })
            else
                let commandName = trimmed[.. firstSpace - 1].ToLowerInvariant()
                let argument = trimmed[firstSpace + 1 ..].TrimStart()

                match commandName with
                | "open"
                | "writeas" ->
                    context.Files
                    |> List.filter (fun filePath -> filePath.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.truncate context.CompletionLimit
                    |> List.map (fun filePath ->
                        { Label = filePath
                          ApplyText = $"{commandName} {filePath}"
                          Detail = "workspace file"
                          Kind = PathItem })
                | "theme" ->
                    context.Themes
                    |> List.filter (fun theme -> theme.Name.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun theme ->
                        { Label = theme.Name
                          ApplyText = $"theme {theme.Name}"
                          Detail = theme.Description
                          Kind = PathItem })
                | "recent" ->
                    context.Recent
                    |> List.filter (fun filePath ->
                        let display =
                            Path.GetFileName filePath |> Option.ofObj |> Option.defaultValue filePath

                        display.StartsWith(argument, StringComparison.OrdinalIgnoreCase)
                        || filePath.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.truncate context.CompletionLimit
                    |> List.map (fun filePath ->
                        let label =
                            Path.GetFileName filePath |> Option.ofObj |> Option.defaultValue filePath

                        { Label = label
                          ApplyText = $"recent {filePath}"
                          Detail = filePath
                          Kind = PathItem })
                | "buffers" ->
                    context.Buffers
                    |> List.filter (fun (id, name, _) ->
                        name.StartsWith(argument, StringComparison.OrdinalIgnoreCase)
                        || (string id).StartsWith argument)
                    |> List.truncate context.CompletionLimit
                    |> List.map (fun (id, name, filePath) ->
                        let detail = filePath |> Option.defaultValue "scratch"

                        { Label = $"{id}  {name}"
                          ApplyText = $"buffers {id}"
                          Detail = detail
                          Kind = PathItem })
                | _ -> []

    let helpLines () =
        specs |> List.map (fun spec -> $"{spec.Usage}  {spec.Summary}")
