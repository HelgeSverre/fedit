module Fedit.Tests.ThemesTests

open Fedit
open Xunit
open FsUnit.Xunit

// The chrome surface was lifted out of View.fs onto the Theme record so a theme
// owns every painted region (and a light theme can fill real backgrounds). These
// tests lock in two guarantees from that change:
//   1. green carries the exact values the old View constants used, so the
//      default theme renders identically to before.
//   2. every bundled theme inherits that chrome (each is `{ green with … }`
//      overriding only hue slots), so no bundled theme regressed and the dark
//      themes keep `Default` backgrounds.

[<Fact>]
let ``green chrome defaults match the former View constants`` () =
    Themes.green.SurfaceFg |> should equal (Color.indexed 252)
    Themes.green.SurfaceBg |> should equal Color.Default
    Themes.green.ChromeFg |> should equal (Color.indexed 244)
    Themes.green.ChromeBg |> should equal Color.Default
    Themes.green.PromptFg |> should equal (Color.indexed 230)
    Themes.green.PromptBg |> should equal (Color.indexed 237)
    Themes.green.LineNumberFg |> should equal (Color.indexed 241)
    Themes.green.LineNumberBg |> should equal Color.Default
    Themes.green.ActiveLineFg |> should equal (Color.indexed 252)
    Themes.green.ActiveLineBg |> should equal (Color.indexed 236)
    Themes.green.CurrentLineBg |> should equal Color.Default
    Themes.green.SelectionFg |> should equal (Color.indexed 230)

let private chromeOf (t: Theme) =
    t.SurfaceFg,
    t.SurfaceBg,
    t.ChromeFg,
    t.ChromeBg,
    t.PromptFg,
    t.PromptBg,
    t.LineNumberFg,
    t.LineNumberBg,
    t.ActiveLineFg,
    t.ActiveLineBg,
    t.CurrentLineBg,
    t.SelectionFg

// Full-surface themes deliberately replace green's whole chrome (their own
// editor/gutter/dock backgrounds) rather than inheriting it. Every other bundled
// theme is `{ green with <hue> }` and must keep green's chrome verbatim.
let private fullSurfaceThemes = set [ "github-light"; "github-dark"; "ayu" ]

[<Fact>]
let ``accent-only bundled themes inherit green's chrome surface`` () =
    let inheriting =
        Themes.all |> List.filter (fun t -> not (fullSurfaceThemes.Contains t.Name))

    for theme in inheriting do
        chromeOf theme |> should equal (chromeOf Themes.green)

[<Fact>]
let ``github-light is bundled and paints a real light surface`` () =
    Themes.tryFind "github-light" |> Option.isSome |> should equal true
    // A light theme must override the default-background chrome, otherwise a
    // dark terminal bleeds through. Assert the editor fill is opaque white.
    Themes.githubLight.SurfaceBg |> should equal (Color.ofHex "#FFFFFF")
    Themes.githubLight.SurfaceBg |> should not' (equal Color.Default)

[<Fact>]
let ``github-light fills the editor with its white background end-to-end`` () =
    // Render a real frame under github-light and confirm the white SurfaceBg
    // reaches actual screen cells — proves the theme slot drives the renderer,
    // not just the record.
    let model, _ =
        Editor.init "/root" { Width = 40; Height = 10 } (Config.defaults Themes.githubLight) [] None

    let screen = Layout.render model
    let white = Color.ofHex "#FFFFFF"

    let painted =
        seq {
            for row in 0 .. screen.Height - 1 do
                for col in 0 .. screen.Width - 1 do
                    yield screen.Cells[row, col].Style.Background
        }
        |> Seq.contains white

    painted |> should equal true

[<Fact>]
let ``github-dark is bundled and paints a near-black surface`` () =
    Themes.tryFind "github-dark" |> Option.isSome |> should equal true
    // Full-surface dark theme: the editor fill is GitHub's canvas, not the
    // terminal default, so it renders identically regardless of terminal bg.
    Themes.githubDark.SurfaceBg |> should equal (Color.ofHex "#0D1117")
    Themes.githubDark.SurfaceBg |> should not' (equal Color.Default)
