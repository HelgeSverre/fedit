namespace Fedit

open System
open System.IO


type FileNode =
    { Path: string
      Name: string
      IsDirectory: bool
      Children: FileNode list }

type WorkspaceEntry =
    { Path: string
      Name: string
      Depth: int
      IsDirectory: bool
      IsExpanded: bool
      IsSelected: bool }

type WorkspaceState =
    {
        RootPath: string
        Tree: FileNode option
        /// Flat path → node lookup, populated once in `setTree`. Replaces
        /// the recursive `tryPick` walk in `findNodeByPath`, which used to
        /// run several times per sidebar keypress.
        ByPath: Map<string, FileNode>
        Expanded: Set<string>
        SelectedPath: string option
        /// Type-ahead search query (Finder / VS Code Explorer style).
        /// Empty when no search is in progress.
        SearchBuffer: string
    }

type SidebarAction =
    | SidebarNoOp
    | SidebarOpenFile of string

[<RequireQualifiedAccess>]
module Workspace =
    let excludedNames = set [ ".DS_Store"; ".git"; ".dotnet"; "bin"; "obj" ]

    let create rootPath =
        { RootPath = rootPath
          Tree = None
          ByPath = Map.empty
          Expanded = Set.singleton rootPath
          SelectedPath = None
          SearchBuffer = "" }

    let private sortChildren (nodes: FileNode list) =
        nodes
        |> List.sortBy (fun node -> (not node.IsDirectory, node.Name.ToLowerInvariant()))

    let rec private flatten selected expanded depth (node: FileNode) =
        let entry =
            { Path = node.Path
              Name = node.Name
              Depth = depth
              IsDirectory = node.IsDirectory
              IsExpanded = node.IsDirectory && Set.contains node.Path expanded
              IsSelected = Some node.Path = selected }

        if node.IsDirectory && Set.contains node.Path expanded then
            entry
            :: (node.Children
                |> sortChildren
                |> List.collect (flatten selected expanded (depth + 1)))
        else
            [ entry ]

    let visibleEntries workspace =
        match workspace.Tree with
        | Some tree -> flatten workspace.SelectedPath workspace.Expanded 0 tree
        | None -> []

    let private ensureSelected workspace =
        let visible = visibleEntries workspace

        match workspace.SelectedPath, visible with
        | Some selectedPath, _ when visible |> List.exists (fun entry -> entry.Path = selectedPath) -> workspace
        | _, first :: _ ->
            { workspace with
                SelectedPath = Some first.Path }
        | _ -> workspace

    /// Recursively sort a tree's children so `visibleEntries` doesn't have
    /// to sort on every keypress.
    let rec private preSort (node: FileNode) : FileNode =
        if node.IsDirectory then
            { node with
                Children = node.Children |> List.map preSort |> sortChildren }
        else
            node

    let rec private collectByPath (acc: Map<string, FileNode>) (node: FileNode) =
        let acc = Map.add node.Path node acc

        if node.IsDirectory then
            node.Children |> List.fold collectByPath acc
        else
            acc

    let setTree (tree: FileNode) workspace =
        let sorted = preSort tree

        { workspace with
            Tree = Some sorted
            ByPath = collectByPath Map.empty sorted
            Expanded =
                if sorted.IsDirectory then
                    Set.add sorted.Path workspace.Expanded
                else
                    workspace.Expanded }
        |> ensureSelected

    let selectPath path workspace =
        { workspace with
            SelectedPath = Some path }
        |> ensureSelected

    let moveSelection delta workspace =
        let visible = visibleEntries workspace

        match visible with
        | [] -> workspace
        | _ ->
            let currentIndex =
                workspace.SelectedPath
                |> Option.bind (fun path -> visible |> List.tryFindIndex (fun entry -> entry.Path = path))
                |> Option.defaultValue 0

            { workspace with
                SelectedPath = Some visible[max 0 (min (visible.Length - 1) (currentIndex + delta))].Path }

    let moveHome workspace =
        match visibleEntries workspace with
        | first :: _ ->
            { workspace with
                SelectedPath = Some first.Path }
        | [] -> workspace

    let moveEnd workspace =
        match visibleEntries workspace |> List.tryLast with
        | Some last ->
            { workspace with
                SelectedPath = Some last.Path }
        | None -> workspace

    let private findNodeByPath path workspace = Map.tryFind path workspace.ByPath

    let expandSelected workspace =
        match
            workspace.SelectedPath
            |> Option.bind (fun path -> findNodeByPath path workspace)
        with
        | Some node when node.IsDirectory ->
            { workspace with
                Expanded = Set.add node.Path workspace.Expanded }
        | _ -> workspace

    let tryCollapseSelected workspace =
        match
            visibleEntries workspace
            |> List.tryFind (fun entry -> Some entry.Path = workspace.SelectedPath)
        with
        | Some entry when entry.IsDirectory && entry.IsExpanded ->
            Some
                { workspace with
                    Expanded = Set.remove entry.Path workspace.Expanded }
        | _ -> None

    let selectParent workspace =
        match
            visibleEntries workspace
            |> List.tryFind (fun entry -> Some entry.Path = workspace.SelectedPath)
        with
        | Some entry ->
            match Path.GetDirectoryName entry.Path |> Option.ofObj with
            | Some parent when not (String.IsNullOrEmpty parent) -> selectPath parent workspace
            | _ -> workspace
        | None -> workspace

    let activateSelected workspace =
        match
            workspace.SelectedPath
            |> Option.bind (fun path -> findNodeByPath path workspace)
        with
        | Some node when node.IsDirectory ->
            let expanded =
                if Set.contains node.Path workspace.Expanded then
                    Set.remove node.Path workspace.Expanded
                else
                    Set.add node.Path workspace.Expanded

            { workspace with Expanded = expanded }, SidebarNoOp
        | Some node -> workspace, SidebarOpenFile node.Path
        | None -> workspace, SidebarNoOp

    let clearSearch workspace =
        if workspace.SearchBuffer = "" then
            workspace
        else
            { workspace with SearchBuffer = "" }

    let private matchesIn (entries: WorkspaceEntry list) (needle: string) =
        if String.IsNullOrEmpty needle then
            []
        else
            entries
            |> List.filter (fun entry -> entry.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))

    /// VS Code / Finder-style type-ahead: extend the buffer if the extended
    /// query still matches anything; otherwise restart with just the new char;
    /// otherwise drop the buffer entirely (next press starts fresh).
    /// If the same query matches multiple entries and the current selection is
    /// already one of them, advance to the next match.
    let appendSearch (c: char) workspace =
        let entries = visibleEntries workspace
        let extended = workspace.SearchBuffer + string c
        let single = string c

        let newBuffer, matched =
            match matchesIn entries extended with
            | _ :: _ as ms -> extended, ms
            | [] ->
                match matchesIn entries single with
                | _ :: _ as ms -> single, ms
                | [] -> "", []

        if matched.IsEmpty then
            { workspace with SearchBuffer = "" }
        else
            let currentIsMatch =
                workspace.SelectedPath
                |> Option.exists (fun path -> matched |> List.exists (fun m -> m.Path = path))

            let target =
                if currentIsMatch && workspace.SearchBuffer = newBuffer then
                    // Same query re-typed — cycle to next matching entry.
                    let currentIdx =
                        matched |> List.findIndex (fun m -> Some m.Path = workspace.SelectedPath)

                    matched[(currentIdx + 1) % matched.Length]
                else
                    List.head matched

            { workspace with
                SearchBuffer = newBuffer
                SelectedPath = Some target.Path }

    /// Drop the last character from the search buffer and re-select the first
    /// match of the shortened query. If the buffer was empty or becomes empty,
    /// just clears it.
    let backspaceSearch workspace =
        if workspace.SearchBuffer.Length = 0 then
            workspace
        else
            let shorter = workspace.SearchBuffer.Substring(0, workspace.SearchBuffer.Length - 1)

            if shorter.Length = 0 then
                { workspace with SearchBuffer = "" }
            else
                let entries = visibleEntries workspace
                let matched = matchesIn entries shorter

                match matched with
                | first :: _ ->
                    { workspace with
                        SearchBuffer = shorter
                        SelectedPath = Some first.Path }
                | [] ->
                    { workspace with
                        SearchBuffer = shorter }
