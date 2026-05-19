namespace Fedit

open System
open System.IO

module Program =
    let private parseArgs (argv: string[]) =
        let mutable workspace = None
        let mutable logPath = None
        let mutable i = 0

        while i < argv.Length do
            if argv[i] = "--log" && i + 1 < argv.Length then
                logPath <- Some argv[i + 1]
                i <- i + 2
            elif argv[i] = "--help" || argv[i] = "-h" then
                i <- i + 1
            else
                if workspace.IsNone then
                    workspace <- Some argv[i]

                i <- i + 1

        workspace, logPath

    [<EntryPoint>]
    let main argv =
        let workspace, logPath = parseArgs argv

        let rootPath =
            match workspace with
            | Some path -> Path.GetFullPath path
            | None -> Directory.GetCurrentDirectory()

        let absLogPath = logPath |> Option.map Path.GetFullPath

        try
            Runtime.run rootPath absLogPath
            0
        with ex ->
            eprintfn "fedit: unrecoverable error"
            eprintfn "  %s" ex.Message

            if not (String.IsNullOrEmpty ex.StackTrace) then
                eprintfn "%s" ex.StackTrace

            1
