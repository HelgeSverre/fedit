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

    let private options =
        [ { Names = [ "--log" ]
            Value = RequiredValue "path"
            Option = Log }
          { Names = [ "--help"; "-h" ]
            Value = NoValue
            Option = Help }
          { Names = [ "--version"; "-V" ]
            Value = NoValue
            Option = Version } ]

    let private parseArgs (argv: string[]) =
        let apply parsed item =
            match item with
            | CliParsed.Argument path when parsed.Workspace.IsNone -> { parsed with Workspace = Some path }
            | CliParsed.Argument _ -> parsed
            | CliParsed.Option(Log, Some path) -> { parsed with LogPath = Some path }
            | CliParsed.Option(Log, None) -> parsed
            | CliParsed.Option(Help, _) -> { parsed with HelpRequested = true }
            | CliParsed.Option(Version, _) -> { parsed with VersionRequested = true }

        argv
        |> Cli.parse options
        |> List.fold
            apply
            { Workspace = None
              LogPath = None
              HelpRequested = false
              VersionRequested = false }

    let private printHelp () =
        printfn "fedit — a small terminal text editor written in F#"
        printfn ""
        printfn "Usage: fedit [<path>] [options]"
        printfn ""
        printfn "Arguments:"
        printfn "  <path>             Workspace directory or file to open (default: cwd)"
        printfn ""
        printfn "Options:"
        printfn "  -h, --help         Show this help and exit"
        printfn "  -V, --version      Print version and exit"
        printfn "      --log <path>   Append Msg/Effect trace to <path> for debugging"

    let private versionString () =
        let asm = System.Reflection.Assembly.GetExecutingAssembly()

        match asm.GetName().Version with
        | null -> "fedit unknown"
        | version -> $"fedit {version.Major}.{version.Minor}.{version.Build}"

    [<EntryPoint>]
    let main argv =
        let parsed = parseArgs argv

        if parsed.HelpRequested then
            printHelp ()
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
