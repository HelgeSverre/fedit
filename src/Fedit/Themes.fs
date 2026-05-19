namespace Fedit

open System


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
    let cyan =
        { Name = "cyan"
          Description = "Default — cool blue accent"
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

    let green =
        { Name = "green"
          Description = "Forest green accent"
          Accent = 82
          StatusFg = 15
          StatusBg = 22
          SelectedBg = 28
          CurrentLine = 157 }

    let yellow =
        { Name = "yellow"
          Description = "Warm yellow accent (dark text)"
          Accent = 220
          StatusFg = 0
          StatusBg = 100
          SelectedBg = 178
          CurrentLine = 229 }

    let orange =
        { Name = "orange"
          Description = "Warm amber accent (dark text)"
          Accent = 215
          StatusFg = 0
          StatusBg = 130
          SelectedBg = 166
          CurrentLine = 222 }

    let red =
        { Name = "red"
          Description = "Crimson accent"
          Accent = 203
          StatusFg = 15
          StatusBg = 88
          SelectedBg = 124
          CurrentLine = 217 }

    let magenta =
        { Name = "magenta"
          Description = "Hot pink accent"
          Accent = 213
          StatusFg = 15
          StatusBg = 90
          SelectedBg = 127
          CurrentLine = 219 }

    let purple =
        { Name = "purple"
          Description = "Royal purple accent"
          Accent = 141
          StatusFg = 15
          StatusBg = 54
          SelectedBg = 92
          CurrentLine = 183 }

    let all = [ cyan; teal; green; yellow; orange; red; magenta; purple ]

    let defaultTheme = cyan

    let tryFind (name: string) =
        let needle = name.Trim().ToLowerInvariant()
        all |> List.tryFind (fun theme -> theme.Name = needle)

    /// Look up a theme across both the bundled list and a supplied set of
    /// user-defined themes. User themes win on name collision so a user can
    /// override `cyan` etc. by dropping a file with that name.
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
