module Fedit.Tests.SnapshotTests

open Fedit
open Xunit

// Tier 2 render checks, two kinds in one file. Every scenario builds a
// model, renders it, and projects via `Snapshot.render`:
//
// - Render smokes: assert that key markers (mode labels, prompt prefixes,
//   the dirty marker) appear somewhere in the projected frame. Most tests
//   are smokes because their frames carry incidental content we don't
//   want to lock down.
// - Full-frame goldens: `assertSnapshot` compares the entire projected
//   frame against a checked-in literal for small deterministic scenarios.
//   A failing test prints actual vs expected — review the diff like a
//   schema change before "accepting" by editing the literal.
//
// Goldens are intentionally inline rather than .verified.txt files: the
// terminal sizes used here are small (30x6 typical) so the snapshot
// content fits next to the assertion. Easier to review in PRs.

let private size width height = { Width = width; Height = height }

let private baseModel terminal =
    let model, _ = Editor.init "/root" terminal (Config.defaults Themes.defaultTheme) []

    model

let private renderOf model = Layout.render model |> Snapshot.render

/// Replace the literal with the actual when intentionally accepting a
/// snapshot drift. The whole point is that diffs are visible.
let private assertSnapshot (expected: string) (actual: string) =
    // Normalize newlines: the renderer emits LF, but a golden string literal
    // checked out with CRLF (Windows) would otherwise differ. `.gitattributes`
    // enforces LF too; this is belt-and-suspenders.
    let normalize (s: string) = s.Replace("\r\n", "\n").Trim()

    if normalize expected <> normalize actual then
        let header = "--- expected ---\n"
        let mid = "\n--- actual ---\n"
        Assert.Fail(header + expected + mid + actual)

// ── Full-frame goldens ──────────────────────────────────────────────────
// At 30x6 the sidebar is hidden (width < 40) and the dock is empty, so the
// frame is: editor rows (gutter + content, active line styled), status bar
// (truncated at the inner width), and the empty command bar.

[<Fact>]
let ``golden frame: empty scratch buffer at 30x6`` () =
    let expected =
        """
=== 30x6 cursor=0,5 visible ===
[35/_ ..]  1 [252/_ ..] [252/236 ..]
[241/_ ..]~    [252/_ ..]
[241/_ ..]~    [252/_ ..]
[241/_ ..]~    [252/_ ..]
[15/22 ..] EDIT  [scratch]  Ctrl+P prom
[230/237 ..]
"""

    assertSnapshot expected (renderOf (baseModel (size 30 6)))

[<Fact>]
let ``golden frame: two-line buffer at 30x6`` () =
    let model = baseModel (size 30 6)
    let buf = Buffer.fromText 1 None "scratch" "ab\ncd" "\n"

    let withLines =
        { model with
            Editors =
                { model.Editors with
                    Buffers = Map.ofList [ 1, buf ] } }

    let expected =
        """
=== 30x6 cursor=0,5 visible ===
[35/_ ..]  1 [252/_ ..] [252/236 ..]ab
[241/_ ..]  2 [252/_ ..] cd
[241/_ ..]~    [252/_ ..]
[241/_ ..]~    [252/_ ..]
[15/22 ..] EDIT  [scratch]  Ctrl+P prom
[230/237 ..]
"""

    assertSnapshot expected (renderOf withLines)

// ── Render smokes ───────────────────────────────────────────────────────

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
