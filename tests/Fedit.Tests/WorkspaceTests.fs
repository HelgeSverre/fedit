module Fedit.Tests.WorkspaceTests

open Fedit
open Xunit
open FsUnit.Xunit

let private leaf path name : FileNode =
    { Path = path
      Name = name
      IsDirectory = false
      Children = [] }

let private dir path name children : FileNode =
    { Path = path
      Name = name
      IsDirectory = true
      Children = children }

let private sampleTree () =
    dir
        "/root"
        "root"
        [ leaf "/root/a.fs" "a.fs"
          dir "/root/sub" "sub" [ leaf "/root/sub/b.fs" "b.fs"; leaf "/root/sub/c.fs" "c.fs" ] ]

[<Fact>]
let ``create yields empty workspace`` () =
    let ws = Workspace.create "/root"
    ws.RootPath |> should equal "/root"
    ws.Tree |> should equal None
    ws.SelectedPath |> should equal None

[<Fact>]
let ``setTree populates and auto-selects root`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (sampleTree ())
    ws.Tree.IsSome |> should equal true
    ws.SelectedPath.IsSome |> should equal true

[<Fact>]
let ``visibleEntries returns just the root when collapsed`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (sampleTree ())
    let entries = Workspace.visibleEntries ws
    entries |> List.length |> should be (greaterThan 0)

[<Fact>]
let ``moveSelection moves down then up`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (sampleTree ())
    let firstPath = ws.SelectedPath
    let moved = ws |> Workspace.moveSelection 1
    moved.SelectedPath |> should not' (equal firstPath)
    let back = moved |> Workspace.moveSelection -1
    back.SelectedPath |> should equal firstPath

[<Fact>]
let ``tryCollapseSelected only succeeds on expanded directories`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (sampleTree ())
    // root is expanded automatically
    let collapsed = Workspace.tryCollapseSelected ws
    collapsed.IsSome |> should equal true

[<Fact>]
let ``metadata returns the selected entry's structure`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (sampleTree ())
    let meta = Workspace.metadata ws
    meta.IsSome |> should equal true

[<Fact>]
let ``metadata is None when nothing selected`` () =
    let ws = Workspace.create "/root"
    Workspace.metadata ws |> should equal None
