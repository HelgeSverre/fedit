namespace TodoList

open System.IO
open System.Text.RegularExpressions
open Fedit.PluginApi

/// Lists every `TODO:` site in a clickable scratch buffer and navigates to
/// the source on Enter / left-click.
///
/// Two commands:
///   todolist  — scan workspace, open a "todos" buffer, register click handler
///   todo-jump — parse the clicked line, open the file at the exact position
///
/// Reference plugin #2 of the TODO trio. Demonstrates: deriving candidate
/// files from `Workspace.Files` (host cache), `NewBuffer` for output that
/// outgrows a notification, `SetBufferActivation` for clickable lines, and
/// `OpenFileAt` for precise navigation.
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

    /// Regex for the header line emitted before the listing.
    let private headerRegex =
        Regex("^\\d+ todos? in \\d+ files?$", RegexOptions.Compiled)

    /// Parse a listing line of the form `path:line:col  text`.
    /// Returns (relativePath, line, column) or None for header/blank lines.
    let private parseTodoLine (line: string) =
        let m = Regex.Match(line, "^(.+?):(\\d+):(\\d+)\\s")

        if m.Success then
            Some(m.Groups[1].Value, int m.Groups[2].Value, int m.Groups[3].Value)
        else
            None

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
                                let trimmed = line.Trim()
                                results.Add $"{rel}:{lineNo}:1  {trimmed}"
                                hitFiles.Add rel |> ignore
                    with _ ->
                        ()

        List.ofSeq results, hitFiles.Count

    let register (host: IPluginHost) =
        // Command 1: scan and open the listing buffer.
        host.RegisterCommand
            { Name = "todolist"
              Usage = "todolist"
              Summary = $"List `TODO:` markers in the workspace (max {maxResults}). Click a line to jump to source."
              Run =
                fun ctx ->
                    let items, fileCount = scan ctx.Workspace.RootPath ctx.Workspace.Files

                    if items.IsEmpty then
                        [ Notify(Info, "No todos.") ]
                    else
                        let header = $"{items.Length} todos in {fileCount} files"
                        let body = header + "\n" + (String.concat "\n" items)

                        [ NewBuffer("todos", body)
                          SetBufferActivation "todo-jump"
                          Notify(Info, $"{items.Length} todos in {fileCount} files — Enter to jump") ] }

        // Command 2: handle click/Enter on a listing line.
        host.RegisterCommand
            { Name = "todo-jump"
              Usage = "todo-jump"
              Summary = "Jump to the TODO source location (internal — used by todolist activation)."
              Run =
                fun ctx ->
                    let lines = ctx.ActiveBuffer.Text.Split('\n')

                    let lineIdx = ctx.ActiveBuffer.Cursor.Line - 1

                    if lineIdx >= 0 && lineIdx < lines.Length then
                        match parseTodoLine lines[lineIdx] with
                        | Some(path, line, col) -> [ OpenFileAt(path, { Line = line; Column = col }, false) ]
                        | None -> []
                    else
                        [] }
