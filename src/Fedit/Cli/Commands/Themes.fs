/// `fedit themes [--json]` — dump the bundled themes.
///
/// `--json` emits a JSON array the website's theme previews consume; bare
/// prints a short aligned table. Serializes `Themes.all` — the same records
/// the editor renders — with every chrome surface resolved to a hex string
/// via `Color.toHex`, or `null` when the surface keeps the terminal default
/// (the dark themes leave editor/gutter/sidebar backgrounds untouched).
module Fedit.Cli.Commands.Themes

open System.Text
open Fedit
open Fedit.Cli

let private jsonEscape (s: string) : string =
    let sb = StringBuilder()

    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | other -> sb.Append other |> ignore

    sb.ToString()

let private quote (s: string) = "\"" + jsonEscape s + "\""

/// A surface as a hex literal, or JSON `null` when it keeps the terminal
/// default (so the web preview can fall back to a neutral canvas).
let private colorVal (c: Color) : string =
    match Color.toHex c with
    | Some hex -> quote hex
    | None -> "null"

/// "light" / "dark" inferred from the editor background's luma — dark themes
/// have a `Default` background and read as "dark".
let private appearance (t: Theme) : string =
    match Color.toRgb t.SurfaceBg with
    | Some(r, g, b) ->
        let luma = (0.299 * float r + 0.587 * float g + 0.114 * float b) / 255.0
        if luma >= 0.5 then "light" else "dark"
    | None -> "dark"

let private field (name: string) (value: string) = quote name + ": " + value

let private themeJson (t: Theme) : string =
    let syntax =
        [ field "keyword" (colorVal t.SyntaxKeyword)
          field "string" (colorVal t.SyntaxString)
          field "comment" (colorVal t.SyntaxComment)
          field "function" (colorVal t.SyntaxFunction)
          field "type" (colorVal t.SyntaxType) ]
        |> String.concat ", "

    let pairs =
        [ field "name" (quote t.Name)
          field "description" (quote t.Description)
          field
              "isDefault"
              (if t.Name = Themes.defaultTheme.Name then
                   "true"
               else
                   "false")
          field "appearance" (quote (appearance t))
          field "accent" (colorVal t.Accent)
          field "surfaceFg" (colorVal t.SurfaceFg)
          field "surfaceBg" (colorVal t.SurfaceBg)
          field "chromeFg" (colorVal t.ChromeFg)
          field "chromeBg" (colorVal t.ChromeBg)
          field "promptFg" (colorVal t.PromptFg)
          field "promptBg" (colorVal t.PromptBg)
          field "lineNumberFg" (colorVal t.LineNumberFg)
          field "lineNumberBg" (colorVal t.LineNumberBg)
          field "activeLineFg" (colorVal t.ActiveLineFg)
          field "activeLineBg" (colorVal t.ActiveLineBg)
          field "currentLine" (colorVal t.CurrentLine)
          field "currentLineBg" (colorVal t.CurrentLineBg)
          field "statusFg" (colorVal t.StatusFg)
          field "statusBg" (colorVal t.StatusBg)
          field "selectionFg" (colorVal t.SelectionFg)
          field "selectedBg" (colorVal t.SelectedBg)
          field "syntax" ("{ " + syntax + " }") ]

    "  { " + String.concat ", " pairs + " }"

/// Serialize `Themes.all` to a JSON array, newest field order matching the
/// website's `Theme` interface. Ends with a newline.
let toJson (themes: Theme list) : string =
    "[\n" + (themes |> List.map themeJson |> String.concat ",\n") + "\n]\n"

/// Human-readable aligned table: name, appearance, accent hex.
let private renderTable (themes: Theme list) : string =
    let nameWidth =
        themes
        |> List.map (fun t -> t.Name.Length)
        |> (fun ls -> if List.isEmpty ls then 0 else List.max ls)

    let sb = StringBuilder()

    for t in themes do
        let accent = Color.toHex t.Accent |> Option.defaultValue "-"

        sb.AppendLine(sprintf "%-*s  %-5s  %s" nameWidth t.Name (appearance t) accent)
        |> ignore

    sb.ToString()

type private ThemesOpt =
    | ThemesHelp
    | ThemesJson

let private themesApp: CliApp<ThemesOpt> =
    { Name = "fedit themes"
      Summary = "Print the bundled themes"
      Positionals = []
      Options =
        [ { Short = Some 'h'
            Long = "help"
            Value = NoValue
            Description = "Show this help and exit"
            Option = ThemesHelp
            Completion = NoHint }
          { Short = None
            Long = "json"
            Value = NoValue
            Description = "Emit the themes as a JSON array"
            Option = ThemesJson
            Completion = NoHint } ]
      Subcommands = [] }

/// Descriptor for the `themes` subcommand. Exported so the top-level
/// descriptor in `Program.fs` can nest it.
let descriptor: CliCommandDescriptor =
    { Name = "themes"
      Aliases = []
      HiddenAliases = []
      Summary = themesApp.Summary
      Positionals = themesApp.Positionals
      Options = themesApp.Options |> List.map Parser.toOptionDescriptor
      Subcommands = [] }

let private wantsHelp items =
    items
    |> List.exists (function
        | Option(ThemesHelp, _) -> true
        | _ -> false)

let private wantsJson items =
    items
    |> List.exists (function
        | Option(ThemesJson, _) -> true
        | _ -> false)

let run (argv: string[]) : int =
    match Parser.parse themesApp.Options argv with
    | Result.Error errors ->
        eprintfn "%s" (Parser.formatErrors themesApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        printfn "%s" (Parser.formatHelp themesApp)
        0
    | Result.Ok items ->
        if wantsJson items then
            printf "%s" (toJson Themes.all)
        else
            printf "%s" (renderTable Themes.all)

        0
