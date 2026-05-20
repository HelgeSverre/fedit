namespace Fedit

open System
open System.IO
open Fedit.Cli
open Fedit.Cli.Commands

module Program =
    type private ProgramOption =
        | Log
        | Help
        | Version

    type private ParsedArgs =
        { Workspace: string option
          LogPath: string option
          HelpRequested: bool
          VersionRequested: bool }

    let private subcommands: CliSubcommandSpec list =
        [ { Name = "plugins"
            Aliases = []
            // `plugin` (singular) is a convenience alias — kept undocumented
            // so the canonical `plugins` form is what people learn.
            HiddenAliases = [ "plugin" ]
            Summary = "Manage installed plugins" }
          { Name = "completions"
            Aliases = []
            HiddenAliases = []
            Summary = "Generate shell completion scripts" } ]

    let private app: CliApp<ProgramOption> =
        { Name = "fedit"
          Summary = "a small terminal text editor written in F#"
          Positionals =
            [ { Name = "path"
                Description = "Workspace directory or file to open (default: cwd)"
                Completion = FilePath } ]
          Options =
            [ { Short = Some 'h'
                Long = "help"
                Value = NoValue
                Description = "Show this help and exit"
                Option = Help
                Completion = NoHint }
              { Short = Some 'V'
                Long = "version"
                Value = NoValue
                Description = "Print version and exit"
                Option = Version
                Completion = NoHint }
              { Short = None
                Long = "log"
                Value = RequiredValue "path"
                Description = "Append Msg/Effect trace to <path> for debugging"
                Option = Log
                Completion = FilePath } ]
          Subcommands = subcommands }

    let private applyParsed parsed item =
        match item with
        | CliParsed.Argument path when parsed.Workspace.IsNone -> { parsed with Workspace = Some path }
        | CliParsed.Argument _ -> parsed
        | CliParsed.Option(Log, Some path) -> { parsed with LogPath = Some path }
        | CliParsed.Option(Log, None) -> parsed
        | CliParsed.Option(Help, _) -> { parsed with HelpRequested = true }
        | CliParsed.Option(Version, _) -> { parsed with VersionRequested = true }

    let private foldParsed items =
        items
        |> List.fold
            applyParsed
            { Workspace = None
              LogPath = None
              HelpRequested = false
              VersionRequested = false }

    let private versionString () =
        let asm = System.Reflection.Assembly.GetExecutingAssembly()

        match asm.GetName().Version with
        | null -> "fedit unknown"
        | version -> $"fedit {version.Major}.{version.Minor}.{version.Build}"

    /// The full fedit command tree as a single descriptor — used by
    /// `fedit completions <shell>` to emit accurate shell scripts.
    let private rootDescriptor: CliCommandDescriptor =
        { Name = app.Name
          Aliases = []
          HiddenAliases = []
          Summary = app.Summary
          Positionals = app.Positionals
          Options = app.Options |> List.map Parser.toOptionDescriptor
          Subcommands = [ Plugins.descriptor; Completions.descriptor ] }

    [<EntryPoint>]
    let main argv =
        match Parser.route subcommands argv with
        | Some("plugins", rest) -> Plugins.run rest
        | Some("completions", rest) -> Completions.run rootDescriptor rest
        | _ ->

            match Parser.parse app.Options argv with
            | Result.Error errors ->
                eprintfn "%s" (Parser.formatErrors app errors)
                2

            | Result.Ok items ->
                let parsed = foldParsed items

                if parsed.HelpRequested then
                    printfn "%s" (Parser.formatHelp app)
                    0
                elif parsed.VersionRequested then
                    printfn "%s" (versionString ())
                    0
                else
                    let rootPath =
                        match parsed.Workspace with
                        | Some path -> Path.GetFullPath path
                        | None -> Directory.GetCurrentDirectory()

                    let absLogPath = parsed.LogPath |> Option.map Path.GetFullPath

                    try
                        Runtime.run rootPath absLogPath
                        0
                    with ex ->
                        eprintfn "fedit: unrecoverable error"
                        eprintfn "  %s" ex.Message

                        if not (String.IsNullOrEmpty ex.StackTrace) then
                            eprintfn "%s" ex.StackTrace

                        1
