namespace TodoList

open System.IO
open Fedit.PluginApi

/// Reports every `TODO:` site as `path:line  text` in an editable scratch
/// buffer named "todos".
///
/// Reference plugin #2 of the TODO trio. Demonstrates: deriving the
/// candidate files from `Workspace.Files` (the host's cached index — no
/// directory walk), reading file contents from disk, and `NewBuffer` for
/// output that outgrows a notification.
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

    /// Cap output so a workspace with thousands of TODOs stays readable.
    let private maxResults = 50

    let private scan (root: string) (files: string list) =
        let results = System.Collections.Generic.List<string>()
        let hitFiles = System.Collections.Generic.HashSet<string>()

        for rel in files do
            if results.Count < maxResults then
                let ext = (Path.GetExtension rel).ToLowerInvariant()

                if extensions.Contains ext then
                    try
                        let mutable lineNo = 0

                        for line in File.ReadLines(Path.Combine(root, rel)) do
                            lineNo <- lineNo + 1

                            if line.Contains "TODO:" && results.Count < maxResults then
                                results.Add $"{rel}:{lineNo}  {line.Trim()}"
                                hitFiles.Add rel |> ignore
                    with _ ->
                        ()

        List.ofSeq results, hitFiles.Count

    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "todolist"
              Usage = "todolist"
              Summary = $"List `TODO:` markers in the workspace (max {maxResults})."
              Run =
                fun ctx ->
                    let items, fileCount = scan ctx.Workspace.RootPath ctx.Workspace.Files

                    if items.IsEmpty then
                        [ Notify(Info, "No todos.") ]
                    else
                        [ NewBuffer("todos", String.concat "\n" items)
                          Notify(Info, $"{items.Length} todos in {fileCount} files") ] }
