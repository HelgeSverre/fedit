namespace Fedit

open System
open System.IO

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

    let private app: CliApp<ProgramOption> =
        { Name = "fedit"
          Summary = "a small terminal text editor written in F#"
          Positionals =
            [ { Name = "path"
                Description = "Workspace directory or file to open (default: cwd)" } ]
          Options =
            [ { Short = Some 'h'
                Long = "help"
                Value = NoValue
                Description = "Show this help and exit"
                Option = Help }
              { Short = Some 'V'
                Long = "version"
                Value = NoValue
                Description = "Print version and exit"
                Option = Version }
              { Short = None
                Long = "log"
                Value = RequiredValue "path"
                Description = "Append Msg/Effect trace to <path> for debugging"
                Option = Log } ] }

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

    [<EntryPoint>]
    let main argv =
        match Cli.parse app.Options argv with
        | Result.Error errors ->
            eprintfn "%s" (Cli.formatErrors app errors)
            2

        | Result.Ok items ->
            let parsed = foldParsed items

            if parsed.HelpRequested then
                printfn "%s" (Cli.formatHelp app)
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
