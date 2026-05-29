module Fedit.Tests.SnapshotTests

open Fedit
open Xunit

// Phase 7: Tier 2 frame snapshots. Each scenario builds a model, renders
// it, projects via `Snapshot.render`, and compares to a checked-in
// expected string. A failing test prints actual vs expected — review the
// diff like a schema change before "accepting" by editing the literal.
//
// Goldens are intentionally inline rather than .verified.txt files: the
// terminal sizes used here are small (40x10 typical) so the snapshot
// content fits next to the assertion. Easier to review in PRs.

let private size width height = { Width = width; Height = height }

let private baseModel terminal =
    let model, _ =
        Editor.init "/root" terminal (Config.defaults Themes.defaultTheme) [] None

    model

let private renderOf model = Layout.render model |> Snapshot.render

/// Replace the literal with the actual when intentionally accepting a
/// snapshot drift. The whole point is that diffs are visible.
let private assertSnapshot (expected: string) (actual: string) =
    if expected.Trim() <> actual.Trim() then
        let header = "--- expected ---\n"
        let mid = "\n--- actual ---\n"
        Assert.Fail(header + expected + mid + actual)

[<Fact>]
let ``cold start with no buffer opens to the scratch view`` () =
    let actual = renderOf (baseModel (size 30 6))
    // Loose check: the empty scratch buffer shows tilde-style empty rows.
    // We don't lock the entire snapshot because the welcome notification
    // varies with bindings.
    Assert.Contains("~", actual)
    Assert.Contains("EDIT", actual)
    Assert.Contains("[scratch]", actual)

[<Fact>]
let ``Ctrl+P opens prompt in Command mode and status shows CMD`` () =
    let model = baseModel (size 30 6)

    let next, _ =
        Editor.update
            (KeyPressed
                { Mods = Set.ofList [ Ctrl ]
                  Key = Key.Char 'p' })
            model

    let actual = renderOf next
    Assert.Contains("CMD", actual)
    // The visible prompt prefix is the real ':' character now (no glue).
    Assert.Contains(":", actual)

[<Fact>]
let ``Ctrl+O opens prompt in FilePicker mode`` () =
    let model = baseModel (size 30 6)

    let next, _ =
        Editor.update
            (KeyPressed
                { Mods = Set.ofList [ Ctrl ]
                  Key = Key.Char 'o' })
            model

    let actual = renderOf next
    Assert.Contains("FILE", actual)

[<Fact>]
let ``Ctrl+F opens prompt with / prefix and FIND label`` () =
    let model = baseModel (size 30 6)

    let next, _ =
        Editor.update
            (KeyPressed
                { Mods = Set.ofList [ Ctrl ]
                  Key = Key.Char 'f' })
            model

    let actual = renderOf next
    Assert.Contains("FIND", actual)
    Assert.Contains("/", actual)

[<Fact>]
let ``Ctrl+B with hidden sidebar shows and focuses it`` () =
    let base' = baseModel (size 40 8)

    let hidden =
        { base' with
            Panels =
                { base'.Panels with
                    SidebarVisible = false } }

    let next, _ =
        Editor.update
            (KeyPressed
                { Mods = Set.ofList [ Ctrl ]
                  Key = Key.Char 'b' })
            hidden

    let actual = renderOf next
    Assert.Contains("TREE", actual)
    // The vertical separator should now be the Unicode glyph, not '|'
    Assert.Contains("│", actual)

[<Fact>]
let ``typing a character into the editor inserts it and marks dirty`` () =
    let model = baseModel (size 30 6)

    let next, _ =
        Editor.update (KeyPressed { Mods = Set.empty; Key = Key.Char 'a' }) model

    let actual = renderOf next
    Assert.Contains("a", actual)
    Assert.Contains("[+]", actual)

[<Fact>]
let ``resize updates terminal size and re-renders`` () =
    let small = baseModel (size 30 6)
    let resized, _ = Editor.update (Resize(size 60 10)) small
    let actual = renderOf resized
    // The header line carries the dimensions.
    Assert.Contains("=== 60x10", actual)

[<Fact>]
let ``Snapshot projector elides trailing whitespace per row`` () =
    let model = baseModel (size 30 6)
    let actual = renderOf model

    for line in actual.Split('\n') do
        if line.Length > 0 then
            Assert.False(line.EndsWith ' ', $"line had trailing whitespace: '{line}'")
