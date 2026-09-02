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

/// sampleTree plus a deeper branch: /root/sub/deep/d.fs sits two collapsed
/// directories below the auto-expanded root.
let private deepTree () =
    dir
        "/root"
        "root"
        [ leaf "/root/a.fs" "a.fs"
          dir
              "/root/sub"
              "sub"
              [ leaf "/root/sub/b.fs" "b.fs"
                dir "/root/sub/deep" "deep" [ leaf "/root/sub/deep/d.fs" "d.fs" ] ] ]

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
let ``setTree caches the flat relative file list in tree order`` () =
    // preSort puts directories first, so the cached list follows the
    // sorted tree exactly as the old per-keystroke flatten did.
    let ws = Workspace.create "/root" |> Workspace.setTree (sampleTree ())
    ws.Files |> should equal [ "sub/b.fs"; "sub/c.fs"; "a.fs" ]

[<Fact>]
let ``create starts with an empty file list`` () =
    (Workspace.create "/root").Files |> should be Empty

[<Fact>]
let ``visibleEntries lists the auto-expanded root and its collapsed children`` () =
    // setTree expands and selects the root, so its direct children are
    // visible (directories sorted first); `sub` stays collapsed, hiding
    // b.fs and c.fs.
    let ws = Workspace.create "/root" |> Workspace.setTree (sampleTree ())

    Workspace.visibleEntries ws
    |> should
        equal
        [ { Path = "/root"
            Name = "root"
            Depth = 0
            IsDirectory = true
            IsExpanded = true
            IsSelected = true }
          { Path = "/root/sub"
            Name = "sub"
            Depth = 1
            IsDirectory = true
            IsExpanded = false
            IsSelected = false }
          { Path = "/root/a.fs"
            Name = "a.fs"
            Depth = 1
            IsDirectory = false
            IsExpanded = false
            IsSelected = false } ]

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
let ``revealPath expands collapsed ancestors and selects the file`` () =
    let ws =
        Workspace.create "/root"
        |> Workspace.setTree (deepTree ())
        |> Workspace.revealPath "/root/sub/deep/d.fs"

    Set.isSubset (Set.ofList [ "/root"; "/root/sub"; "/root/sub/deep" ]) ws.Expanded
    |> should equal true

    ws.SelectedPath |> should equal (Some "/root/sub/deep/d.fs")

    Workspace.visibleEntries ws
    |> List.exists (fun entry -> entry.Path = "/root/sub/deep/d.fs")
    |> should equal true

[<Fact>]
let ``revealPath outside the root is a no-op`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (deepTree ())
    Workspace.revealPath "/elsewhere/x.fs" ws |> should equal ws

[<Fact>]
let ``revealPath of the root selects it without new expansions`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (deepTree ())
    let revealed = ws |> Workspace.moveSelection 1 |> Workspace.revealPath "/root"
    revealed.SelectedPath |> should equal (Some "/root")
    revealed.Expanded |> should equal ws.Expanded

[<Fact>]
let ``revealPath of a top-level file needs no expansion`` () =
    let ws = Workspace.create "/root" |> Workspace.setTree (deepTree ())
    let revealed = Workspace.revealPath "/root/a.fs" ws
    revealed.SelectedPath |> should equal (Some "/root/a.fs")
    revealed.Expanded |> should equal ws.Expanded

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
    // Selection is intentionally preserved when the buffer clears (cursor stays at last match).
    ws.SelectedPath |> should equal (Some "/root/a.fs")

[<Fact>]
let ``clearSearch resets the buffer`` () =
    let ws =
        Workspace.create "/root"
        |> Workspace.setTree (sampleTree ())
        |> Workspace.appendSearch 'a'
        |> Workspace.clearSearch

    ws.SearchBuffer |> should equal ""
    // Selection is intentionally preserved on clear (cursor stays at last match).
    ws.SelectedPath |> should equal (Some "/root/a.fs")

// -- Ignore rules -------------------------------------------------------------

let private gitignore (text: string) = [ Ignore.parseGitignore "/root" text ]

[<Theory>]
[<InlineData("bin/", "/root/bin", true, true)>]
[<InlineData("bin/", "/root/bin", false, false)>]
[<InlineData("bin/", "/root/src/bin", true, true)>]
[<InlineData("*.log", "/root/a/b/c.log", false, true)>]
[<InlineData("*.log", "/root/a/b/c.txt", false, false)>]
[<InlineData("/build", "/root/build", true, true)>]
[<InlineData("/build", "/root/src/build", true, false)>]
[<InlineData("docs/**", "/root/docs/a/b.md", false, true)>]
[<InlineData("**/temp", "/root/x/y/temp", true, true)>]
[<InlineData("a/*.js", "/root/a/x.js", false, true)>]
[<InlineData("a/*.js", "/root/a/b/x.js", false, false)>]
[<InlineData("?.txt", "/root/a.txt", false, true)>]
[<InlineData("[ab].txt", "/root/b.txt", false, true)>]
[<InlineData("# comment\n\n*.tmp", "/root/x.tmp", false, true)>]
[<InlineData("*.log\n!keep.log", "/root/keep.log", false, false)>]
let ``gitignore patterns match like git`` (pattern: string) (path: string) (isDir: bool) (expected: bool) =
    Ignore.matchesGitignore (gitignore pattern) path isDir |> should equal expected

[<Fact>]
let ``nested gitignore overrides its ancestors`` () =
    let files =
        [ Ignore.parseGitignore "/root" "*.log"
          Ignore.parseGitignore "/root/keep" "!*.log" ]

    Ignore.matchesGitignore files "/root/keep/a.log" false |> should equal false
    Ignore.matchesGitignore files "/root/other/a.log" false |> should equal true

[<Fact>]
let ``ignored names apply at any depth and gitignore can be switched off`` () =
    let rules =
        { Names = [ "node_modules" ]
          UseGitignore = false }

    let files = gitignore "*.log"
    Ignore.isIgnored rules files "/root/a/node_modules" true |> should equal true
    Ignore.isIgnored rules files "/root/a/x.log" false |> should equal false

    Ignore.isIgnored { rules with UseGitignore = true } files "/root/a/x.log" false
    |> should equal true
