namespace Fedit

open System
open System.IO
open System.Text

/// Config file location + load / save. Carved out of Runtime.fs (Phase 10.3).
/// Owns nothing about the runtime loop; just JSON in/out for the `Config`
/// record. Named `ConfigIO` so it doesn't collide with `Model.Config`
/// (which carries the type + defaults).
[<RequireQualifiedAccess>]
module ConfigIO =
    let private utf8WithoutBom = UTF8Encoding false

    let directory () =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "fedit")

    let path () =
        Path.Combine(directory (), "config.json")

    let themesDirectory () = Path.Combine(directory (), "themes")

    let private optStr (s: string | null) =
        match s with
        | null -> None
        | value -> Some value

    let private getStringProp (root: System.Text.Json.JsonElement) (name: string) =
        match root.TryGetProperty(name: string) with
        | true, elem when elem.ValueKind = System.Text.Json.JsonValueKind.String -> optStr (elem.GetString())
        | _ -> None

    let private getIntProp (root: System.Text.Json.JsonElement) (name: string) =
        match root.TryGetProperty(name: string) with
        | true, elem when elem.ValueKind = System.Text.Json.JsonValueKind.Number -> Some(elem.GetInt32())
        | _ -> None

    let private clampInt low high value = max low (min high value)

    /// Returns the loaded config and an optional error message. On parse
    /// failure we still return defaults (so the editor boots) but surface
    /// the error in the startup notification.
    let load (userThemes: Theme list) : Config * string option =
        let defaults = Config.defaults Themes.defaultTheme

        try
            let p = path ()

            if File.Exists p then
                let json = File.ReadAllText p
                use doc = System.Text.Json.JsonDocument.Parse json
                let root = doc.RootElement

                let theme =
                    getStringProp root "theme"
                    |> Option.bind (fun name -> Themes.tryFindIn userThemes name)
                    |> Option.defaultValue defaults.Theme

                let recent =
                    match root.TryGetProperty "recent" with
                    | true, elem when elem.ValueKind = System.Text.Json.JsonValueKind.Array ->
                        elem.EnumerateArray()
                        |> Seq.choose (fun item ->
                            if item.ValueKind = System.Text.Json.JsonValueKind.String then
                                optStr (item.GetString())
                            else
                                None)
                        |> Seq.toList
                    | _ -> defaults.Recent

                let completionLimit =
                    getIntProp root "completionLimit"
                    |> Option.defaultValue defaults.CompletionLimit
                    |> clampInt 1 64

                let sidebarIndent =
                    getIntProp root "sidebarIndent"
                    |> Option.defaultValue defaults.SidebarIndent
                    |> clampInt 0 16

                let sidebarWidth =
                    getIntProp root "sidebarWidth"
                    |> Option.defaultValue defaults.SidebarWidth
                    |> clampInt 10 200

                let dockHeight =
                    getIntProp root "dockHeight"
                    |> Option.defaultValue defaults.DockHeight
                    |> clampInt 1 40

                let wordMotion =
                    match getStringProp root "wordMotion" with
                    | Some "nextWordStart" -> NextWordStart
                    | Some "wordEnd" -> WordEnd
                    | _ -> defaults.WordMotion

                let pageOverlap =
                    getIntProp root "pageOverlap"
                    |> Option.defaultValue defaults.PageOverlap
                    |> clampInt 0 32

                let treePageJump =
                    getIntProp root "treePageJump"
                    |> Option.defaultValue defaults.TreePageJump
                    |> clampInt 1 500

                let tabWidth =
                    getIntProp root "tabWidth"
                    |> Option.defaultValue defaults.TabWidth
                    |> clampInt 1 16

                let icons =
                    match getStringProp root "icons" with
                    | Some "nerd" -> IconsNerd
                    | Some "off" -> IconsOff
                    | _ -> defaults.Icons

                let statusFormat =
                    getStringProp root "statusFormat" |> Option.defaultValue defaults.StatusFormat

                let syntaxHighlightingEnabled =
                    match root.TryGetProperty "syntaxHighlighting" with
                    | true, e when e.ValueKind = System.Text.Json.JsonValueKind.False -> false
                    | true, e when e.ValueKind = System.Text.Json.JsonValueKind.True -> true
                    | _ -> defaults.SyntaxHighlightingEnabled

                let scrollMode =
                    match getStringProp root "scrollMode" with
                    | Some "line" -> ScrollLine
                    | Some "viewport" -> ScrollViewport
                    | _ -> defaults.ScrollMode

                let scrollOff =
                    getIntProp root "scrollOff"
                    |> Option.defaultValue defaults.ScrollOff
                    |> clampInt 0 50

                let mouseScrollLines =
                    getIntProp root "mouseScrollLines"
                    |> Option.defaultValue defaults.MouseScrollLines
                    |> clampInt 1 20

                let config =
                    { Theme = theme
                      Recent = recent
                      CompletionLimit = completionLimit
                      SidebarIndent = sidebarIndent
                      SidebarWidth = sidebarWidth
                      DockHeight = dockHeight
                      WordMotion = wordMotion
                      PageOverlap = pageOverlap
                      TreePageJump = treePageJump
                      TabWidth = tabWidth
                      Icons = icons
                      StatusFormat = statusFormat
                      SyntaxHighlightingEnabled = syntaxHighlightingEnabled
                      ScrollMode = scrollMode
                      ScrollOff = scrollOff
                      MouseScrollLines = mouseScrollLines }

                config, None
            else
                defaults, None
        with ex ->
            defaults, Some $"config.json: {ex.Message}"

    /// Read a color field as a hex string ("#RRGGBB" / "#RGB") or a
    /// named color ("deepSkyBlue"). Integer values are rejected — the
    /// schema is hex-or-name only after the Color unification.
    let private getColorProp (root: System.Text.Json.JsonElement) (name: string) =
        getStringProp root name |> Option.bind Color.tryParse

    /// Returns the loaded themes and a list of per-file error messages.
    let loadUserThemes () : Theme list * string list =
        try
            let dir = themesDirectory ()

            if not (Directory.Exists dir) then
                [], []
            else
                let mutable errors: string list = []
                let mutable themes: Theme list = []

                for file in Directory.EnumerateFiles(dir, "*.json") do
                    try
                        let json = File.ReadAllText file
                        use doc = System.Text.Json.JsonDocument.Parse json
                        let root = doc.RootElement

                        let fallbackName =
                            Path.GetFileNameWithoutExtension file
                            |> optStr
                            |> Option.defaultValue "user-theme"

                        let name =
                            getStringProp root "name"
                            |> Option.defaultValue fallbackName
                            |> fun s -> s.ToLowerInvariant()

                        let description =
                            getStringProp root "description" |> Option.defaultValue $"User theme '{name}'"

                        // Optional `syntax` block — each field falls back to the
                        // bundled-default theme's value so a user can override
                        // only the chrome colors without redefining all 16.
                        let syntaxRoot =
                            match root.TryGetProperty "syntax" with
                            | true, e when e.ValueKind = System.Text.Json.JsonValueKind.Object -> Some e
                            | _ -> None

                        let pickSyntax (field: string) (fallback: Color) =
                            match syntaxRoot with
                            | Some o ->
                                match getColorProp o field with
                                | Some c -> c
                                | None -> fallback
                            | None -> fallback

                        let d = Themes.defaultTheme

                        // Optional chrome override. An absent key falls back to
                        // the bundled value so existing user themes keep today's
                        // chrome; the literal "default" maps to Color.Default so
                        // a light theme can override a foreground while leaving a
                        // background as the terminal default.
                        let pickColor (field: string) (fallback: Color) =
                            match getStringProp root field with
                            | Some s when s.Trim().Equals("default", System.StringComparison.OrdinalIgnoreCase) ->
                                Color.Default
                            | Some s -> Color.tryParse s |> Option.defaultValue fallback
                            | None -> fallback

                        match
                            getColorProp root "accent",
                            getColorProp root "statusFg",
                            getColorProp root "statusBg",
                            getColorProp root "selectedBg",
                            getColorProp root "currentLine"
                        with
                        | Some a, Some sf, Some sb, Some seb, Some cl ->
                            themes <-
                                { Name = name
                                  Description = description
                                  Accent = a
                                  StatusFg = sf
                                  StatusBg = sb
                                  SelectedBg = seb
                                  CurrentLine = cl
                                  SelectionFg = pickColor "selectionFg" d.SelectionFg
                                  CurrentLineBg = pickColor "currentLineBg" d.CurrentLineBg
                                  SurfaceFg = pickColor "surfaceFg" d.SurfaceFg
                                  SurfaceBg = pickColor "surfaceBg" d.SurfaceBg
                                  ChromeFg = pickColor "chromeFg" d.ChromeFg
                                  ChromeBg = pickColor "chromeBg" d.ChromeBg
                                  PromptFg = pickColor "promptFg" d.PromptFg
                                  PromptBg = pickColor "promptBg" d.PromptBg
                                  LineNumberFg = pickColor "lineNumberFg" d.LineNumberFg
                                  LineNumberBg = pickColor "lineNumberBg" d.LineNumberBg
                                  ActiveLineFg = pickColor "activeLineFg" d.ActiveLineFg
                                  ActiveLineBg = pickColor "activeLineBg" d.ActiveLineBg
                                  SyntaxKeyword = pickSyntax "keyword" d.SyntaxKeyword
                                  SyntaxKeywordControl = pickSyntax "keywordControl" d.SyntaxKeywordControl
                                  SyntaxKeywordOperator = pickSyntax "keywordOperator" d.SyntaxKeywordOperator
                                  SyntaxString = pickSyntax "string" d.SyntaxString
                                  SyntaxStringSpecial = pickSyntax "stringSpecial" d.SyntaxStringSpecial
                                  SyntaxNumber = pickSyntax "number" d.SyntaxNumber
                                  SyntaxComment = pickSyntax "comment" d.SyntaxComment
                                  SyntaxFunction = pickSyntax "function" d.SyntaxFunction
                                  SyntaxFunctionCall = pickSyntax "functionCall" d.SyntaxFunctionCall
                                  SyntaxType = pickSyntax "type" d.SyntaxType
                                  SyntaxConstructor = pickSyntax "constructor" d.SyntaxConstructor
                                  SyntaxVariable = pickSyntax "variable" d.SyntaxVariable
                                  SyntaxParameter = pickSyntax "parameter" d.SyntaxParameter
                                  SyntaxOperator = pickSyntax "operator" d.SyntaxOperator
                                  SyntaxPunctuation = pickSyntax "punctuation" d.SyntaxPunctuation
                                  SyntaxAttribute = pickSyntax "attribute" d.SyntaxAttribute }
                                :: themes
                        | _ ->
                            errors <-
                                $"theme '{Path.GetFileName file}': missing or malformed color fields (need #RRGGBB or named color)"
                                :: errors
                    with ex ->
                        errors <- $"theme '{Path.GetFileName file}': {ex.Message}" :: errors

                List.rev themes, List.rev errors
        with ex ->
            [], [ $"themes dir: {ex.Message}" ]

    let save (config: Config) =
        Directory.CreateDirectory(directory ()) |> ignore
        let p = path ()

        let root =
            if File.Exists p then
                try
                    let existing = File.ReadAllText p

                    match System.Text.Json.Nodes.JsonNode.Parse existing with
                    | :? System.Text.Json.Nodes.JsonObject as obj -> obj
                    | _ -> System.Text.Json.Nodes.JsonObject()
                with _ ->
                    System.Text.Json.Nodes.JsonObject()
            else
                System.Text.Json.Nodes.JsonObject()

        let recentArray = System.Text.Json.Nodes.JsonArray()

        for item in config.Recent do
            recentArray.Add(System.Text.Json.Nodes.JsonValue.Create item)

        let wordMotionStr =
            match config.WordMotion with
            | WordEnd -> "wordEnd"
            | NextWordStart -> "nextWordStart"

        let iconsStr =
            match config.Icons with
            | IconsOff -> "off"
            | IconsNerd -> "nerd"

        let scrollModeStr =
            match config.ScrollMode with
            | ScrollLine -> "line"
            | ScrollViewport -> "viewport"

        root["theme"] <- System.Text.Json.Nodes.JsonValue.Create config.Theme.Name
        root["recent"] <- recentArray
        root["completionLimit"] <- System.Text.Json.Nodes.JsonValue.Create config.CompletionLimit
        root["sidebarIndent"] <- System.Text.Json.Nodes.JsonValue.Create config.SidebarIndent
        root["sidebarWidth"] <- System.Text.Json.Nodes.JsonValue.Create config.SidebarWidth
        root["dockHeight"] <- System.Text.Json.Nodes.JsonValue.Create config.DockHeight
        root["wordMotion"] <- System.Text.Json.Nodes.JsonValue.Create wordMotionStr
        root["pageOverlap"] <- System.Text.Json.Nodes.JsonValue.Create config.PageOverlap
        root["treePageJump"] <- System.Text.Json.Nodes.JsonValue.Create config.TreePageJump
        root["tabWidth"] <- System.Text.Json.Nodes.JsonValue.Create config.TabWidth
        root["icons"] <- System.Text.Json.Nodes.JsonValue.Create iconsStr
        root["statusFormat"] <- System.Text.Json.Nodes.JsonValue.Create config.StatusFormat
        root["syntaxHighlighting"] <- System.Text.Json.Nodes.JsonValue.Create config.SyntaxHighlightingEnabled
        root["scrollMode"] <- System.Text.Json.Nodes.JsonValue.Create scrollModeStr
        root["scrollOff"] <- System.Text.Json.Nodes.JsonValue.Create config.ScrollOff
        root["mouseScrollLines"] <- System.Text.Json.Nodes.JsonValue.Create config.MouseScrollLines

        let options = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
        let json = root.ToJsonString options
        File.WriteAllText(p, json + "\n", utf8WithoutBom)
