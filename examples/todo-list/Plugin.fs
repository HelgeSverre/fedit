namespace TodoList

open System.IO
open Fedit.PluginApi

/// Reports every `TODO:` site as `relative/path.fs:line: text` in the dock.
///
/// Reference plugin #2 of the TODO trio. Demonstrates: multiline Notify
/// (joined with newlines — the dock renders them as separate rows).
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

    /// Cap output so a workspace with thousands of TODOs doesn't drown the
    /// dock. The terminal can scroll a notification but very large strings
    /// stress the renderer.
    let private maxResults = 50

    let private scan (root: string) =
        let results = System.Collections.Generic.List<string>()
        let queue = System.Collections.Generic.Queue<string>()
        queue.Enqueue root

        while queue.Count > 0 && results.Count < maxResults do
            let dir = queue.Dequeue()

            try
                for sub in Directory.EnumerateDirectories dir do
                    if not (skipDirs.Contains(Path.GetFileName sub)) then
                        queue.Enqueue sub

                for file in Directory.EnumerateFiles dir do
                    if results.Count >= maxResults then
                        ()
                    else
                        let ext = (Path.GetExtension file).ToLowerInvariant()

                        if extensions.Contains ext then
                            try
                                let mutable lineNo = 0

                                for line in File.ReadLines file do
                                    lineNo <- lineNo + 1

                                    if line.Contains "TODO:" && results.Count < maxResults then
                                        let rel = Path.GetRelativePath(root, file)
                                        results.Add $"{rel}:{lineNo}: {line.Trim()}"
                            with _ ->
                                ()
            with _ ->
                ()

        List.ofSeq results

    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "todolist"
              Usage = "todolist"
              Summary = $"List `TODO:` markers in the workspace (max {maxResults})."
              Run =
                fun ctx ->
                    let items = scan ctx.Workspace.RootPath

                    let body =
                        if items.IsEmpty then
                            "(no TODOs found)"
                        else
                            System.String.Join("\n", items)

                    [ Notify(Info, body) ] }
