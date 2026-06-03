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
      // Hue family — the theme's primary color and its companions.
      Accent: Color
      StatusBg: Color
      StatusFg: Color
      SelectedBg: Color
      SelectionFg: Color
      // Active line number (gutter cell) — foreground + background.
      // `CurrentLine` is the fg; `ActiveLine*` below is the editor line itself.
      CurrentLine: Color
      CurrentLineBg: Color
      // Chrome surfaces — every painted region carries an explicit fg+bg so a
      // theme owns the whole screen. A `Color.Default` background means "keep
      // the terminal's default background" (emits no SGR), which is what the
      // bundled dark themes use; a light theme sets real backgrounds instead.
      SurfaceFg: Color
      SurfaceBg: Color
      ChromeFg: Color
      ChromeBg: Color
      PromptFg: Color
      PromptBg: Color
      LineNumberFg: Color
      LineNumberBg: Color
      ActiveLineFg: Color
      ActiveLineBg: Color
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

    // GitHub Light Default (Primer) syntax palette. Truecolor hex tuned for a
    // white surface — the dark `defaultSyntax` picks are too pale to read here.
    // Operators/punctuation/variables stay `Default` so they inherit the dark
    // editor foreground, matching how GitHub leaves them uncolored.
    let private githubLightSyntax =
        {| SyntaxKeyword = Color.ofHex "#CF222E" // red
           SyntaxKeywordControl = Color.ofHex "#CF222E"
           SyntaxKeywordOperator = Color.ofHex "#CF222E"
           SyntaxString = Color.ofHex "#0A3069" // deep blue
           SyntaxStringSpecial = Color.ofHex "#0A3069"
           SyntaxNumber = Color.ofHex "#0550AE" // blue constant
           SyntaxComment = Color.ofHex "#6E7781" // grey
           SyntaxFunction = Color.ofHex "#8250DF" // purple entity
           SyntaxFunctionCall = Color.ofHex "#8250DF"
           SyntaxType = Color.ofHex "#953800" // orange-brown
           SyntaxConstructor = Color.ofHex "#953800"
           SyntaxVariable = Color.Default
           SyntaxParameter = Color.Default
           SyntaxOperator = Color.Default
           SyntaxPunctuation = Color.Default
           SyntaxAttribute = Color.ofHex "#0550AE" |}

    // GitHub Dark Default (Primer) syntax palette. Truecolor hex on a near-black
    // surface. Operators/punctuation/variables stay `Default` so they inherit
    // the light editor foreground (#E6EDF3), as GitHub leaves them uncolored.
    let private githubDarkSyntax =
        {| SyntaxKeyword = Color.ofHex "#FF7B72" // coral red
           SyntaxKeywordControl = Color.ofHex "#FF7B72"
           SyntaxKeywordOperator = Color.ofHex "#FF7B72"
           SyntaxString = Color.ofHex "#A5D6FF" // light blue
           SyntaxStringSpecial = Color.ofHex "#A5D6FF"
           SyntaxNumber = Color.ofHex "#79C0FF" // blue constant
           SyntaxComment = Color.ofHex "#8B949E" // grey
           SyntaxFunction = Color.ofHex "#D2A8FF" // light purple entity
           SyntaxFunctionCall = Color.ofHex "#D2A8FF"
           SyntaxType = Color.ofHex "#79C0FF" // support type blue
           SyntaxConstructor = Color.ofHex "#79C0FF"
           SyntaxVariable = Color.Default
           SyntaxParameter = Color.Default
           SyntaxOperator = Color.Default
           SyntaxPunctuation = Color.Default
           SyntaxAttribute = Color.ofHex "#79C0FF" |}

    // Brand default. Phosphor green #00B86B → ANSI 35 (#00AF5F).
    let green =
        { Name = "green"
          Description = "Phosphor green — brand default"
          Accent = Color.phosphorGreen
          StatusBg = Color.forestGreen
          StatusFg = Color.brightWhite
          SelectedBg = Color.mossGreen
          SelectionFg = Color.indexed 230
          CurrentLine = Color.phosphorGreen
          CurrentLineBg = Color.Default
          // Chrome defaults reproduce the previously-hardcoded View constants,
          // so every bundled theme keeps its exact current look.
          SurfaceFg = Color.indexed 252
          SurfaceBg = Color.Default
          ChromeFg = Color.indexed 244
          ChromeBg = Color.Default
          PromptFg = Color.indexed 230
          PromptBg = Color.indexed 237
          LineNumberFg = Color.indexed 241
          LineNumberBg = Color.Default
          ActiveLineFg = Color.indexed 252
          ActiveLineBg = Color.indexed 236
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

    let graphite =
        { green with
            Name = "graphite"
            Description = "Blue-grey high readability"
            Accent = Color.ofHex "#8CB4FF"
            StatusBg = Color.ofHex "#1F2937"
            SelectedBg = Color.ofHex "#374151"
            CurrentLine = Color.ofHex "#4B5563" }

    let evergreen =
        { green with
            Name = "evergreen"
            Description = "Soft forest green"
            Accent = Color.ofHex "#A7C080"
            StatusBg = Color.ofHex "#3A4A3F"
            SelectedBg = Color.ofHex "#4F5F4A"
            CurrentLine = Color.ofHex "#5F6F55" }

    let monoAmber =
        { green with
            Name = "mono-amber"
            Description = "Deep amber phosphor"
            Accent = Color.ofHex "#FFAF00"
            StatusBg = Color.ofHex "#5F3B00"
            SelectedBg = Color.ofHex "#875F00"
            CurrentLine = Color.ofHex "#AF8700"
            StatusFg = Color.black }

    // First bundled light theme. Every surface carries an explicit light
    // background — a dark terminal no longer bleeds through — and the syntax
    // palette swaps to GitHub's light-readable colors.
    let githubLight =
        { green with
            Name = "github-light"
            Description = "GitHub Light Default (Primer)"
            Accent = Color.ofHex "#0969DA"
            StatusBg = Color.ofHex "#0969DA"
            StatusFg = Color.ofHex "#FFFFFF"
            SelectedBg = Color.ofHex "#DDF4FF"
            SelectionFg = Color.ofHex "#1F2328"
            CurrentLine = Color.ofHex "#1F2328"
            CurrentLineBg = Color.ofHex "#F6F8FA"
            SurfaceFg = Color.ofHex "#1F2328"
            SurfaceBg = Color.ofHex "#FFFFFF"
            ChromeFg = Color.ofHex "#6E7781"
            ChromeBg = Color.ofHex "#F6F8FA"
            PromptFg = Color.ofHex "#1F2328"
            PromptBg = Color.ofHex "#EAEEF2"
            LineNumberFg = Color.ofHex "#8C959F"
            LineNumberBg = Color.ofHex "#FFFFFF"
            ActiveLineFg = Color.ofHex "#1F2328"
            ActiveLineBg = Color.ofHex "#F6F8FA"
            SyntaxKeyword = githubLightSyntax.SyntaxKeyword
            SyntaxKeywordControl = githubLightSyntax.SyntaxKeywordControl
            SyntaxKeywordOperator = githubLightSyntax.SyntaxKeywordOperator
            SyntaxString = githubLightSyntax.SyntaxString
            SyntaxStringSpecial = githubLightSyntax.SyntaxStringSpecial
            SyntaxNumber = githubLightSyntax.SyntaxNumber
            SyntaxComment = githubLightSyntax.SyntaxComment
            SyntaxFunction = githubLightSyntax.SyntaxFunction
            SyntaxFunctionCall = githubLightSyntax.SyntaxFunctionCall
            SyntaxType = githubLightSyntax.SyntaxType
            SyntaxConstructor = githubLightSyntax.SyntaxConstructor
            SyntaxVariable = githubLightSyntax.SyntaxVariable
            SyntaxParameter = githubLightSyntax.SyntaxParameter
            SyntaxOperator = githubLightSyntax.SyntaxOperator
            SyntaxPunctuation = githubLightSyntax.SyntaxPunctuation
            SyntaxAttribute = githubLightSyntax.SyntaxAttribute }

    // GitHub Dark Default (Primer). A full-surface dark theme: unlike the
    // accent-only bundled themes it sets explicit near-black backgrounds rather
    // than inheriting green's `Default` chrome, so the palette matches GitHub
    // exactly instead of the terminal default.
    let githubDark =
        { green with
            Name = "github-dark"
            Description = "GitHub Dark Default (Primer)"
            Accent = Color.ofHex "#2F81F7"
            StatusBg = Color.ofHex "#1F6FEB"
            StatusFg = Color.ofHex "#FFFFFF"
            SelectedBg = Color.ofHex "#1E4273"
            SelectionFg = Color.ofHex "#E6EDF3"
            CurrentLine = Color.ofHex "#E6EDF3"
            CurrentLineBg = Color.ofHex "#161B22"
            SurfaceFg = Color.ofHex "#E6EDF3"
            SurfaceBg = Color.ofHex "#0D1117"
            ChromeFg = Color.ofHex "#8B949E"
            ChromeBg = Color.ofHex "#161B22"
            PromptFg = Color.ofHex "#E6EDF3"
            PromptBg = Color.ofHex "#161B22"
            LineNumberFg = Color.ofHex "#6E7681"
            LineNumberBg = Color.ofHex "#0D1117"
            ActiveLineFg = Color.ofHex "#E6EDF3"
            ActiveLineBg = Color.ofHex "#161B22"
            SyntaxKeyword = githubDarkSyntax.SyntaxKeyword
            SyntaxKeywordControl = githubDarkSyntax.SyntaxKeywordControl
            SyntaxKeywordOperator = githubDarkSyntax.SyntaxKeywordOperator
            SyntaxString = githubDarkSyntax.SyntaxString
            SyntaxStringSpecial = githubDarkSyntax.SyntaxStringSpecial
            SyntaxNumber = githubDarkSyntax.SyntaxNumber
            SyntaxComment = githubDarkSyntax.SyntaxComment
            SyntaxFunction = githubDarkSyntax.SyntaxFunction
            SyntaxFunctionCall = githubDarkSyntax.SyntaxFunctionCall
            SyntaxType = githubDarkSyntax.SyntaxType
            SyntaxConstructor = githubDarkSyntax.SyntaxConstructor
            SyntaxVariable = githubDarkSyntax.SyntaxVariable
            SyntaxParameter = githubDarkSyntax.SyntaxParameter
            SyntaxOperator = githubDarkSyntax.SyntaxOperator
            SyntaxPunctuation = githubDarkSyntax.SyntaxPunctuation
            SyntaxAttribute = githubDarkSyntax.SyntaxAttribute }

    let all =
        [ green
          blue
          orange
          cyan
          teal
          yellow
          red
          graphite
          evergreen
          monoAmber
          githubLight
          githubDark ]

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
