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
    { RootPath: string
      Tree: FileNode option
      Expanded: Set<string>
      SelectedPath: string option }

type SidebarAction =
    | SidebarNoOp
    | SidebarOpenFile of string

[<RequireQualifiedAccess>]
module Workspace =
    let excludedNames = set [ ".DS_Store"; ".git"; ".dotnet"; "bin"; "obj" ]

    let create rootPath =
        { RootPath = rootPath
          Tree = None
          Expanded = Set.singleton rootPath
          SelectedPath = None }

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

    let setTree (tree: FileNode) workspace =
        { workspace with
            Tree = Some tree
            Expanded =
                if tree.IsDirectory then
                    Set.add tree.Path workspace.Expanded
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

    let private findNodeByPath path workspace =
        let rec loop (node: FileNode) =
            if node.Path = path then
                Some node
            else
                node.Children |> List.tryPick loop

        workspace.Tree |> Option.bind loop

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

    let metadata workspace =
        workspace.SelectedPath
        |> Option.bind (fun path -> findNodeByPath path workspace)
        |> Option.map (fun node ->
            let relativePath = Path.GetRelativePath(workspace.RootPath, node.Path)
            let label = if relativePath = "." then node.Path else relativePath

            {| Path = label
               IsDirectory = node.IsDirectory
               ChildCount = if node.IsDirectory then Some node.Children.Length else None |})
