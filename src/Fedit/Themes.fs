namespace Fedit

open System

// Themes are pure accent palettes — the dock title, status bar, selection
// highlight, and current-line gutter all swap together while the grayscale
// chrome stays constant across themes.
//
// Canonical brand spec lives in brand/themes/*.json. This module is the
// implementation. Values use the unified `Color` type from Color.fs:
// bundled themes mix named statics (`Color.deepSkyBlue`) where they exist
// and `Color.ofHex` for cube picks without a curated name. Truecolor
// (`Rgb`) values are accepted; the renderer emits them directly and
// `Color.toIndexed` quantizes if a future capability profile demands it.
type Theme =
    { Name: string
      Description: string
      // Hue family — four shades of the theme's primary color, from
      // brightest (Accent) to softest (CurrentLine).
      Accent: Color
      StatusBg: Color
      SelectedBg: Color
      CurrentLine: Color
      // Foreground policy — text on StatusBg.
      StatusFg: Color }

[<RequireQualifiedAccess>]
module Themes =
    // Brand default. Phosphor green #00B86B → ANSI 35 (#00AF5F).
    let green =
        { Name = "green"
          Description = "Phosphor green — brand default"
          Accent = Color.phosphorGreen
          StatusBg = Color.forestGreen
          SelectedBg = Color.mossGreen
          CurrentLine = Color.phosphorGreen
          StatusFg = Color.brightWhite }

    // Electric blue #1F6FEB → ANSI 33 (#0087FF). GitHub-adjacent, not purple.
    let blue =
        { Name = "blue"
          Description = "Electric blue — high contrast"
          Accent = Color.electricBlue
          StatusBg = Color.midnightBlue
          SelectedBg = Color.steelBlue
          CurrentLine = Color.electricBlue
          StatusFg = Color.brightWhite }

    // Burnt orange #D2691E → ANSI 166. Warm, retro-terminal feel.
    let orange =
        { Name = "orange"
          Description = "Burnt orange — warm, retro"
          Accent = Color.burntOrange
          StatusBg = Color.saddleBrown
          SelectedBg = Color.copper
          CurrentLine = Color.peach
          StatusFg = Color.brightWhite }

    let cyan =
        { Name = "cyan"
          Description = "Cool cyan accent"
          Accent = Color.deepSkyBlue
          StatusBg = Color.oceanBlue
          SelectedBg = Color.azure
          CurrentLine = Color.paleSky
          StatusFg = Color.brightWhite }

    let teal =
        { Name = "teal"
          Description = "Cyan-green hybrid"
          Accent = Color.teal
          StatusBg = Color.darkTeal
          SelectedBg = Color.seafoam
          CurrentLine = Color.paleCyan
          StatusFg = Color.brightWhite }

    let yellow =
        { Name = "yellow"
          Description = "Warm yellow (dark text)"
          Accent = Color.amber
          StatusBg = Color.goldenrod
          SelectedBg = Color.mustard
          CurrentLine = Color.lemonChiffon
          StatusFg = Color.black }

    let red =
        { Name = "red"
          Description = "Crimson accent"
          Accent = Color.crimson
          StatusBg = Color.darkRed
          SelectedBg = Color.firebrick
          CurrentLine = Color.salmon
          StatusFg = Color.brightWhite }

    let all = [ green; blue; orange; cyan; teal; yellow; red ]

    let defaultTheme = green

    let tryFind (name: string) =
        let needle = name.Trim().ToLowerInvariant()
        all |> List.tryFind (fun theme -> theme.Name = needle)

    /// Look up a theme across both the bundled list and a supplied set of
    /// user-defined themes. User themes win on name collision so a user can
    /// override `green` etc. by dropping a file with that name.
    let tryFindIn (userThemes: Theme list) (name: string) =
        let needle = name.Trim().ToLowerInvariant()
        let user = userThemes |> List.tryFind (fun theme -> theme.Name = needle)

        match user with
        | Some _ -> user
        | None -> tryFind needle

    /// Merge user themes with the bundled list. User themes override bundled
    /// entries on name collision (last-write semantics).
    let merge (userThemes: Theme list) =
        let userNames = userThemes |> List.map (fun t -> t.Name) |> Set.ofList
        let bundled = all |> List.filter (fun t -> not (Set.contains t.Name userNames))
        bundled @ userThemes
