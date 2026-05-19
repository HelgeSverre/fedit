namespace Fedit

open System

// Themes are pure accent palettes — the dock title, status bar, selection
// highlight, and current-line gutter all swap together while the grayscale
// chrome stays constant across themes.
//
// Canonical brand spec lives in brand/themes/*.json. This module is the
// implementation. Values use ANSI 256 color codes for compatibility with
// any terminal; true-color terminals upgrade transparently elsewhere.
type Theme =
    { Name: string
      Description: string
      Accent: int
      StatusFg: int
      StatusBg: int
      SelectedBg: int
      CurrentLine: int }

[<RequireQualifiedAccess>]
module Themes =
    // Brand default. Phosphor green #00B86B → ANSI 35 (#00AF5F).
    let green =
        { Name = "green"
          Description = "Phosphor green — brand default"
          Accent = 35
          StatusFg = 15
          StatusBg = 22
          SelectedBg = 28
          CurrentLine = 35 }

    // Electric blue #1F6FEB → ANSI 33 (#0087FF). GitHub-adjacent, not purple.
    let blue =
        { Name = "blue"
          Description = "Electric blue — high contrast"
          Accent = 33
          StatusFg = 15
          StatusBg = 17
          SelectedBg = 25
          CurrentLine = 33 }

    // Burnt orange #D2691E → ANSI 166. Warm, retro-terminal feel.
    let orange =
        { Name = "orange"
          Description = "Burnt orange — warm, retro"
          Accent = 166
          StatusFg = 15
          StatusBg = 94
          SelectedBg = 130
          CurrentLine = 173 }

    let cyan =
        { Name = "cyan"
          Description = "Cool cyan accent"
          Accent = 81
          StatusFg = 15
          StatusBg = 24
          SelectedBg = 31
          CurrentLine = 153 }

    let teal =
        { Name = "teal"
          Description = "Cyan-green hybrid"
          Accent = 80
          StatusFg = 15
          StatusBg = 23
          SelectedBg = 30
          CurrentLine = 159 }

    let yellow =
        { Name = "yellow"
          Description = "Warm yellow (dark text)"
          Accent = 220
          StatusFg = 0
          StatusBg = 100
          SelectedBg = 178
          CurrentLine = 229 }

    let red =
        { Name = "red"
          Description = "Crimson accent"
          Accent = 203
          StatusFg = 15
          StatusBg = 88
          SelectedBg = 124
          CurrentLine = 217 }

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
