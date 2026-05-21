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
      StatusFg: Color
      // Syntax palette — one Color per HighlightCapture case.
      // `Color.Default` means "no override; keep the surface foreground".
      SyntaxKeyword: Color
      SyntaxKeywordControl: Color
      SyntaxKeywordOperator: Color
      SyntaxString: Color
      SyntaxStringSpecial: Color
      SyntaxNumber: Color
      SyntaxComment: Color
      SyntaxFunction: Color
      SyntaxFunctionCall: Color
      SyntaxType: Color
      SyntaxConstructor: Color
      SyntaxVariable: Color
      SyntaxParameter: Color
      SyntaxOperator: Color
      SyntaxPunctuation: Color
      SyntaxAttribute: Color }

[<RequireQualifiedAccess>]
module Themes =
    // Syntax baseline — a dark-terminal-friendly palette that fits all
    // bundled accents. Picked from the ANSI 256 cube so colors render
    // consistently across truecolor + 256-color terminals without
    // quantization drift. Per-theme overrides go in each theme record
    // below (none today; revisit if a specific accent clashes with one
    // of these picks).
    let private defaultSyntax =
        {| SyntaxKeyword = Color.indexed 141 // soft purple
           SyntaxKeywordControl = Color.indexed 141
           SyntaxKeywordOperator = Color.indexed 141
           SyntaxString = Color.indexed 114 // muted green
           SyntaxStringSpecial = Color.indexed 180 // amber
           SyntaxNumber = Color.indexed 215 // light orange
           SyntaxComment = Color.indexed 244 // mid grey
           SyntaxFunction = Color.indexed 117 // light blue
           SyntaxFunctionCall = Color.indexed 117
           SyntaxType = Color.indexed 222 // soft yellow
           SyntaxConstructor = Color.indexed 175 // dusty pink
           SyntaxVariable = Color.Default // no override
           SyntaxParameter = Color.Default
           SyntaxOperator = Color.indexed 248 // light grey
           SyntaxPunctuation = Color.indexed 246 // dimmer grey
           SyntaxAttribute = Color.indexed 180 |}

    // Brand default. Phosphor green #00B86B → ANSI 35 (#00AF5F).
    let green =
        { Name = "green"
          Description = "Phosphor green — brand default"
          Accent = Color.phosphorGreen
          StatusBg = Color.forestGreen
          SelectedBg = Color.mossGreen
          CurrentLine = Color.phosphorGreen
          StatusFg = Color.brightWhite
          SyntaxKeyword = defaultSyntax.SyntaxKeyword
          SyntaxKeywordControl = defaultSyntax.SyntaxKeywordControl
          SyntaxKeywordOperator = defaultSyntax.SyntaxKeywordOperator
          SyntaxString = defaultSyntax.SyntaxString
          SyntaxStringSpecial = defaultSyntax.SyntaxStringSpecial
          SyntaxNumber = defaultSyntax.SyntaxNumber
          SyntaxComment = defaultSyntax.SyntaxComment
          SyntaxFunction = defaultSyntax.SyntaxFunction
          SyntaxFunctionCall = defaultSyntax.SyntaxFunctionCall
          SyntaxType = defaultSyntax.SyntaxType
          SyntaxConstructor = defaultSyntax.SyntaxConstructor
          SyntaxVariable = defaultSyntax.SyntaxVariable
          SyntaxParameter = defaultSyntax.SyntaxParameter
          SyntaxOperator = defaultSyntax.SyntaxOperator
          SyntaxPunctuation = defaultSyntax.SyntaxPunctuation
          SyntaxAttribute = defaultSyntax.SyntaxAttribute }

    // Electric blue #1F6FEB → ANSI 33 (#0087FF). GitHub-adjacent, not purple.
    let blue =
        { green with
            Name = "blue"
            Description = "Electric blue — high contrast"
            Accent = Color.electricBlue
            StatusBg = Color.midnightBlue
            SelectedBg = Color.steelBlue
            CurrentLine = Color.electricBlue }

    // Burnt orange #D2691E → ANSI 166. Warm, retro-terminal feel.
    let orange =
        { green with
            Name = "orange"
            Description = "Burnt orange — warm, retro"
            Accent = Color.burntOrange
            StatusBg = Color.saddleBrown
            SelectedBg = Color.copper
            CurrentLine = Color.peach }

    let cyan =
        { green with
            Name = "cyan"
            Description = "Cool cyan accent"
            Accent = Color.deepSkyBlue
            StatusBg = Color.oceanBlue
            SelectedBg = Color.azure
            CurrentLine = Color.paleSky }

    let teal =
        { green with
            Name = "teal"
            Description = "Cyan-green hybrid"
            Accent = Color.teal
            StatusBg = Color.darkTeal
            SelectedBg = Color.seafoam
            CurrentLine = Color.paleCyan }

    let yellow =
        { green with
            Name = "yellow"
            Description = "Warm yellow (dark text)"
            Accent = Color.amber
            StatusBg = Color.goldenrod
            SelectedBg = Color.mustard
            CurrentLine = Color.lemonChiffon
            StatusFg = Color.black }

    let red =
        { green with
            Name = "red"
            Description = "Crimson accent"
            Accent = Color.crimson
            StatusBg = Color.darkRed
            SelectedBg = Color.firebrick
            CurrentLine = Color.salmon }

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
