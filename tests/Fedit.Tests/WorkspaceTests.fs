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
let ``appendSearch jumps to first prefix match`` () =
    let ws =
        Workspace.create "/root"
        |> Workspace.setTree (sampleTree ())
        |> Workspace.appendSearch 'a'

    ws.SearchBuffer |> should equal "a"
    ws.SelectedPath |> should equal (Some "/root/a.fs")

[<Fact>]
let ``appendSearch with no match clears buffer and keeps selection`` () =
    let baseline = Workspace.create "/root" |> Workspace.setTree (sampleTree ())
    let beforePath = baseline.SelectedPath
    let after = Workspace.appendSearch 'z' baseline
    after.SearchBuffer |> should equal ""
    after.SelectedPath |> should equal beforePath

[<Fact>]
let ``appendSearch falls back to single char on extended mismatch`` () =
    let ws =
        Workspace.create "/root"
        |> Workspace.setTree (sampleTree ())
        |> Workspace.appendSearch 'a' // buffer = "a", selects /root/a.fs
        |> Workspace.appendSearch 's' // "as" no match, fallback "s" matches "sub"

    ws.SearchBuffer |> should equal "s"
    ws.SelectedPath |> should equal (Some "/root/sub")

[<Fact>]
let ``backspaceSearch shortens the buffer`` () =
    let ws =
        Workspace.create "/root"
        |> Workspace.setTree (sampleTree ())
        |> Workspace.appendSearch 'a'
        |> Workspace.backspaceSearch

    ws.SearchBuffer |> should equal ""

[<Fact>]
let ``clearSearch resets the buffer`` () =
    let ws =
        Workspace.create "/root"
        |> Workspace.setTree (sampleTree ())
        |> Workspace.appendSearch 'a'
        |> Workspace.clearSearch

    ws.SearchBuffer |> should equal ""
