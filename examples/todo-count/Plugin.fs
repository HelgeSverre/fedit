namespace TodoCount

open System.IO
open Fedit.PluginApi

/// Walks the workspace tree, counts lines containing `TODO:` in
/// recognised source/text files.
///
/// Reference plugin #1 of the TODO trio. Demonstrates: workspace traversal
/// from `WorkspaceView.RootPath`; aggregate result returned as one Notify.
module Plugin =
    let private extensions =
        Set.ofList
            [ ".fs"
              ".fsi"
              ".fsproj"
              ".fsx"
              ".cs"
              ".md"
              ".txt"
              ".json"
              ".yaml"
              ".yml"
              ".toml"
              ".sh"
              ".py"
              ".rb"
              ".ts"
              ".tsx"
              ".js"
              ".mjs"
              ".astro"
              ".css"
              ".html" ]

    let private skipDirs =
        Set.ofList
            [ "bin"
              "obj"
              "node_modules"
              ".git"
              ".dotnet"
              ".astro"
              "dist"
              ".vercel"
              ".cache" ]

    /// Count `TODO:` occurrences. Returns the total plus a per-file
    /// breakdown (root-relative path, count), most first.
    let private scan (root: string) =
        let mutable count = 0
        let perFile = ResizeArray<string * int>()
        let queue = System.Collections.Generic.Queue<string>()
        queue.Enqueue root

        while queue.Count > 0 do
            let dir = queue.Dequeue()

            try
                for sub in Directory.EnumerateDirectories dir do
                    let name = Path.GetFileName sub

                    if not (skipDirs.Contains name) then
                        queue.Enqueue sub

                for file in Directory.EnumerateFiles dir do
                    let ext = (Path.GetExtension file).ToLowerInvariant()

                    if extensions.Contains ext then
                        try
                            let mutable inFile = 0

                            for line in File.ReadLines file do
                                if line.Contains "TODO:" then
                                    inFile <- inFile + 1

                            if inFile > 0 then
                                count <- count + inFile
                                perFile.Add(Path.GetRelativePath(root, file), inFile)
                        with _ ->
                            ()
            with _ ->
                ()

        count, (perFile |> Seq.sortByDescending snd |> List.ofSeq)

    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "todocount"
              Usage = "todocount"
              Summary = "Count `TODO:` markers across the workspace."
              Run =
                fun ctx ->
                    let count, perFile = scan ctx.Workspace.RootPath

                    let message =
                        match count, perFile.Length with
                        | 0, _ -> "No TODOs found"
                        | 1, _ -> "1 TODO"
                        | n, 1 -> $"{n} TODOs in 1 file"
                        | n, f -> $"{n} TODOs across {f} files"

                    // The panel breaks the count down per file; the status
                    // item keeps the total visible after the panel closes.
                    let lines =
                        [ for path, n in perFile ->
                              [ { Text = path
                                  Style = TextStyle.Accent }
                                { Text = $"  {n}"
                                  Style = TextStyle.Muted } ] ]

                    [ Notify(Info, message)
                      ShowPanel("TODOs", lines)
                      SetStatusItem(if count = 0 then None else Some $"TODO {count}") ] }
