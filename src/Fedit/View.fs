namespace Fedit

open System
open Fedit.PickerTypes
open Fedit.PromptTypes

[<RequireQualifiedAccess>]
module Layout =
    // Chrome styles are now theme-owned: each pulls an explicit fg+bg from the
    // theme so a palette controls the whole surface. The bundled dark themes
    // carry `Default` backgrounds (no SGR), so the rendered output is identical
    // to the previously-hardcoded constants.
    let private surfaceOf (theme: Theme) =
        Style.withColors theme.SurfaceFg theme.SurfaceBg

    let private chromeOf (theme: Theme) =
        Style.withColors theme.ChromeFg theme.ChromeBg

    let private promptOf (theme: Theme) =
        Style.withColors theme.PromptFg theme.PromptBg

    let private lineNumberOf (theme: Theme) =
        Style.withColors theme.LineNumberFg theme.LineNumberBg

    let private activeLineOf (theme: Theme) =
        Style.withColors theme.ActiveLineFg theme.ActiveLineBg

    // Dock title sits on the dock panel, so its background follows ChromeBg.
    let private accentOf (theme: Theme) =
        Style.withColors theme.Accent theme.ChromeBg

    let private statusOf (theme: Theme) =
        Style.withColors theme.StatusFg theme.StatusBg

    let private selectedOf (theme: Theme) =
        { Style.withColors theme.SelectionFg theme.SelectedBg with
            Bold = true }

    let private currentLineOf (theme: Theme) =
        Style.withColors theme.CurrentLine theme.CurrentLineBg

    /// Nerd Font glyph for a file name (by extension). Returns a 2-char
    /// "<glyph><space>" so the column count matches the ASCII `[+] ` marker.
    let private nerdIconFor (fileName: string) =
        let ext =
            match System.IO.Path.GetExtension fileName with
            | null -> ""
            | s -> s.TrimStart('.').ToLowerInvariant()

        let glyph =
            match ext with
            | "fs"
            | "fsi"
            | "fsx" -> ""
            | "md" -> ""
            | "json" -> ""
            | "toml" -> ""
            | "yaml"
            | "yml" -> ""
            | "sh" -> ""
            | "txt" -> ""
            | _ -> ""

        $"{glyph} "

    let private themeFromApplyText (userThemes: Theme list) (applyText: string) =
        if applyText.StartsWith("theme ", System.StringComparison.OrdinalIgnoreCase) then
            Themes.tryFindIn userThemes (applyText.Substring 6)
        else
            None

    /// Derive the theme being previewed (if any) from the current prompt
    /// state. Stays Pure — no mutation of `Prompt` required.
    let private previewTheme model =
        let prompt = model.Prompt

        if not prompt.Active || prompt.Mode <> Command then
            None
        else
            let fromCompletion =
                prompt.Completions
                |> List.tryItem prompt.SelectedCompletion
                |> Option.bind (fun item -> themeFromApplyText model.UserThemes item.ApplyText)

            match fromCompletion, prompt.Parsed with
            | Some _, _ -> fromCompletion
            | None, Ready(Theme name) -> Themes.tryFindIn model.UserThemes name
            | _ -> None

    let private effectiveTheme model =
        previewTheme model |> Option.defaultValue model.Config.Theme

    let private promptDisplayPrefix =
        function
        | PromptSessionKind.PluginsSession -> "Plugins: "
        | PromptSessionKind.MacrosSession -> "Macros: "
        | PromptSessionKind.KeybindingsSession -> "Keybindings: "
        | _ -> ""

    let private pad width (text: string) =
        if width <= 0 then ""
        elif text.Length <= width then text.PadRight width
        else text[.. width - 1]

    let private crop start width (text: string) =
        if width <= 0 || start >= text.Length then
            ""
        else
            text.Substring(start, min width (text.Length - start))

    let private renderSidebar width height screen model =
        let theme = effectiveTheme model
        let selected = selectedOf theme
        let surface = surfaceOf theme
        Screen.fillRect 0 0 width height surface ' ' screen

        // Scroll origin comes from the same pass the mouse hit-testing in
        // `Editor.sidebarEntryAt` uses, so paint and input can't drift.
        let entries, startIndex = Dock.sidebarRows model height

        let icons = model.Config.Icons

        let markerFor (entry: WorkspaceEntry) =
            match icons with
            | IconsOff ->
                if entry.IsDirectory then
                    if entry.IsExpanded then "[-] " else "[+] "
                else
                    "    "
            | IconsNerd ->
                if entry.IsDirectory then
                    if entry.IsExpanded then " " else " "
                else
                    nerdIconFor entry.Name

        entries
        |> List.skip startIndex
        |> List.truncate height
        |> List.iteri (fun row entry ->
            let marker = markerFor entry
            let indentation = String.replicate (entry.Depth * model.Config.SidebarIndent) " "

            let text = $"{indentation}{marker}{entry.Name}"
            Screen.writeText 0 row (if entry.IsSelected then selected else surface) width (pad width text) screen)

    let private renderEditor x width height screen model =
        let buffer = Editor.activeBufferState model
        let theme = effectiveTheme model
        let selected = selectedOf theme
        let currentLineNumber = currentLineOf theme
        let surface = surfaceOf theme
        let lineNumber = lineNumberOf theme
        let currentLineBg = activeLineOf theme
        let gutterWidth = Buffer.gutterWidth buffer
        let digits = gutterWidth - 2
        let rows = Buffer.lines buffer
        let contentWidth = max 1 (width - gutterWidth)

        Screen.fillRect x 0 width height surface ' ' screen

        // Absolute char offset of each line start, computed only as far as
        // the viewport needs — not an O(file) walk of the tail every frame.
        let lastVisible = min (rows.Length - 1) (buffer.ViewportTop + height - 1)
        let lineStarts = Array.zeroCreate (lastVisible + 1)
        let mutable accum = 0

        for i in 0..lastVisible do
            lineStarts[i] <- accum
            accum <- accum + rows[i].Length + 1

        // Hoisted out of the row loop: selectionRange runs positionToIndex,
        // which walks the line array up to the cursor — once per frame is
        // plenty.
        let selection = Buffer.selectionRange buffer

        let searchInfo =
            match model.Prompt.SearchPreview with
            | Some preview when preview.Matches.Length > 0 ->
                let query = Prompt.argumentOf model.Prompt.Text
                Some(query.Length, preview.Matches)
            | _ -> None

        let highlightStyle =
            { Style.defaultStyle with
                Inverted = true }

        for row in 0 .. height - 1 do
            let lineIndex = buffer.ViewportTop + row

            if lineIndex < rows.Length then
                let activeLine = lineIndex = buffer.Cursor.Line

                let textStyle =
                    if activeLine && model.Focus = Editor then
                        currentLineBg
                    else
                        surface

                let lineNumberText = $"{lineIndex + 1}".PadLeft(digits) + " "

                Screen.writeText
                    x
                    row
                    (if activeLine then currentLineNumber else lineNumber)
                    gutterWidth
                    lineNumberText
                    screen

                Screen.writeText
                    (x + gutterWidth)
                    row
                    textStyle
                    contentWidth
                    (pad contentWidth (crop buffer.ViewportLeft contentWidth rows[lineIndex]))
                    screen

                // Syntax overlay — replace each cell's foreground with the
                // span's themed color where one exists. Runs before
                // selection / search so those overlays continue to win,
                // matching the design spec.
                if model.Config.SyntaxHighlightingEnabled then
                    match Map.tryFind buffer.Id model.HighlightStates with
                    | Some spans when spans.Length > 0 ->
                        let lineStart = lineStarts[lineIndex]
                        let lineLen = rows[lineIndex].Length
                        let visibleStart = buffer.ViewportLeft
                        let visibleEnd = min lineLen (buffer.ViewportLeft + contentWidth)

                        for col in visibleStart .. visibleEnd - 1 do
                            match Highlight.spanAt spans (lineStart + col) with
                            | Some span ->
                                let fg = Highlight.colorFor theme span.Capture

                                if fg <> Color.Default then
                                    let displayCol = col - buffer.ViewportLeft
                                    let cellX = x + gutterWidth + displayCol

                                    if cellX < x + width then
                                        let existing = screen.Cells[row, cellX]

                                        Screen.setCell
                                            cellX
                                            row
                                            { existing.Style with Foreground = fg }
                                            existing.Glyph
                                            screen
                            | None -> ()
                    | _ -> ()

                match selection with
                | Some(selStart, selEnd) when selEnd > selStart ->
                    let lineStart = lineStarts[lineIndex]
                    let lineEnd = lineStart + rows[lineIndex].Length

                    if selStart < lineEnd && selEnd > lineStart then
                        let colStart = max 0 (selStart - lineStart)
                        let colEnd = min rows[lineIndex].Length (selEnd - lineStart)

                        for col in colStart .. colEnd - 1 do
                            let displayCol = col - buffer.ViewportLeft

                            if displayCol >= 0 && displayCol < contentWidth then
                                Screen.setCell
                                    (x + gutterWidth + displayCol)
                                    row
                                    selected
                                    (rows.[lineIndex].[col])
                                    screen
                | _ -> ()

                match searchInfo with
                | Some(qLen, matches) ->
                    let lineStart = lineStarts[lineIndex]
                    let lineEnd = lineStart + rows[lineIndex].Length

                    for matchStart in matches do
                        let matchEnd = matchStart + qLen

                        if matchStart < lineEnd && matchEnd > lineStart then
                            let colStart = max 0 (matchStart - lineStart)
                            let colEnd = min rows[lineIndex].Length (matchEnd - lineStart)

                            for col in colStart .. colEnd - 1 do
                                let displayCol = col - buffer.ViewportLeft

                                if displayCol >= 0 && displayCol < contentWidth then
                                    Screen.setCell
                                        (x + gutterWidth + displayCol)
                                        row
                                        highlightStyle
                                        (rows.[lineIndex].[col])
                                        screen
                | None -> ()
            else
                Screen.writeText x row lineNumber gutterWidth (pad gutterWidth "~") screen

        if model.Focus = Editor then
            // Inclusive block cursor: on a forward selection the caret is at
            // the trailing boundary (one past the last selected glyph); sit
            // the block on that glyph instead so the selected char reads as
            // covered, not the empty cell after it. Backward selections
            // already put the caret on the first selected glyph.
            let blockPos =
                match selection with
                | Some(selStart, selEnd) when selEnd > selStart && Buffer.positionToIndex buffer.Cursor buffer = selEnd ->
                    Buffer.indexToPosition (selEnd - 1) buffer
                | _ -> buffer.Cursor

            let cursorX = x + gutterWidth + (blockPos.Column - buffer.ViewportLeft)
            let cursorY = blockPos.Line - buffer.ViewportTop

            if
                cursorX >= x + gutterWidth
                && cursorX < x + width
                && cursorY >= 0
                && cursorY < height
            then
                Screen.withCursor
                    { Left = cursorX
                      Top = cursorY
                      Visible = true }
                    screen
            else
                screen
        else
            screen

    // ── Picker dock ─────────────────────────────────────────────────────
    // Renders a `PickerView` — semantic, layout-agnostic data — into the dock.
    // These functions consume only the view + theme + geometry, never the
    // Model, so row and layout generation stay testable apart from rendering.

    /// Semantic badge role → terminal color. Success reuses the theme accent;
    /// warning/danger fall back to ANSI yellow/red because the theme schema has
    /// no notification slots yet (see the picker design doc).
    let private badgeStyleOf (theme: Theme) (role: PickerBadgeRole) =
        let fg =
            match role with
            | Success -> theme.Accent
            | Warning -> Color.yellow
            | Danger -> Color.red
            | Neutral
            | Muted -> theme.ChromeFg

        match role with
        | Muted -> Style.withColors fg theme.ChromeBg
        | _ ->
            { Style.withColors fg theme.ChromeBg with
                Bold = true }

    /// Action-key chip style. Disabled actions render as plain chrome (no
    /// emphasis); enabled actions are bold and colored by role.
    let private actionKeyStyleOf (theme: Theme) enabled (role: PickerActionRole) =
        if not enabled then
            Style.withColors theme.ChromeFg theme.ChromeBg
        else
            let fg =
                match role with
                | Primary -> theme.Accent
                | Secondary -> theme.ChromeFg
                | Destructive -> Color.red

            { Style.withColors fg theme.ChromeBg with
                Bold = true }

    /// Write `text` at (x, y) clipped to `maxX`, returning the next free x.
    /// Lays out multi-style segments (labels, colored badges, key chips) on a row.
    let private writeSeg x y style maxX (text: string) screen =
        if x >= maxX || String.IsNullOrEmpty text then
            x
        else
            let shown =
                let avail = maxX - x
                if text.Length > avail then text[.. avail - 1] else text

            Screen.writeText x y style shown.Length shown screen
            x + shown.Length

    let private accessoryText =
        function
        | TextAccessory s -> s
        | CountAccessory(label, value) -> $"{value} {label}"
        | ShortcutAccessory chord -> Chord.render chord

    let private keyChip (chord: Chord) = $"[{Chord.render chord}]"

    let private renderPickerFooter (theme: Theme) footerY width (footer: PickerFooter) screen =
        let chrome = chromeOf theme
        let maxX = max 0 (width - 1)

        match footer with
        | ConfirmationFooter(label, key) ->
            let warn = badgeStyleOf theme Warning
            let mutable x = writeSeg 1 footerY warn maxX "confirm: press " screen
            x <- writeSeg x footerY (actionKeyStyleOf theme true Destructive) maxX (keyChip key) screen
            writeSeg x footerY warn maxX $" again to {label}" screen |> ignore
        | ActionFooter actions ->
            let mutable x = 1

            for action in actions do
                let enabled =
                    match action.State with
                    | Enabled -> true
                    | Disabled _ -> false

                x <- writeSeg x footerY (actionKeyStyleOf theme enabled action.Role) maxX (keyChip action.Key) screen
                x <- writeSeg x footerY chrome maxX $" {action.Label}   " screen

    let private renderInspector (theme: Theme) x bodyTop bodyHeight maxX (inspector: PickerInspector) screen =
        let accent = accentOf theme
        let chrome = chromeOf theme
        let width = max 0 (maxX - x)

        let lineFor =
            function
            | TextLine s -> chrome, s
            | PathLine s -> chrome, s
            | ErrorLine s -> Style.withColors Color.red theme.ChromeBg, s
            | ShortcutSequenceLine chords -> chrome, Chord.renderStroke chords

        let rows =
            [ yield accent, inspector.Title
              match inspector.Subtitle with
              | Some s -> yield chrome, s
              | None -> ()
              yield! inspector.Lines |> List.map lineFor ]

        rows
        |> List.truncate bodyHeight
        |> List.iteri (fun i (style, text) -> Screen.writeText x (bodyTop + i) style width (pad width text) screen)

    let private renderPicker (theme: Theme) dockY width dockHeight (view: PickerView) screen =
        let accent = accentOf theme
        let chrome = chromeOf theme
        let selected = selectedOf theme

        let filterSuffix =
            if String.IsNullOrWhiteSpace view.Filter then
                ""
            else
                $"  filter: {view.Filter}"

        Screen.writeText 0 dockY accent width (pad width $" {view.Title} ({view.Items.Length}){filterSuffix} ") screen

        let bodyTop = dockY + 1
        let footerY = dockY + dockHeight - 1
        let bodyHeight = max 0 (footerY - bodyTop)

        if bodyHeight > 0 then
            if view.Items.IsEmpty then
                let w = max 0 (width - 2)
                Screen.writeText 1 bodyTop chrome w (pad w ("  " + view.EmptyText)) screen
            else
                let selectedIndex = max 0 (min (view.Items.Length - 1) view.SelectedIndex)

                let viewOffset =
                    if selectedIndex < bodyHeight then
                        0
                    else
                        selectedIndex - bodyHeight + 1
                    |> min (max 0 (view.Items.Length - bodyHeight))

                let visible = view.Items |> List.skip viewOffset |> List.truncate bodyHeight

                match view.Layout with
                | ListWithInspector ->
                    let leftWidth = max 16 (min 40 (width / 2))
                    Screen.drawVerticalLine leftWidth bodyTop bodyHeight chrome '│' screen

                    visible
                    |> List.iteri (fun i item ->
                        let isSelected = viewOffset + i = selectedIndex
                        let rowY = bodyTop + i
                        let rowStyle = if isSelected then selected else chrome
                        let maxX = leftWidth - 1
                        let prefix = if isSelected then "> " else "  "
                        let mutable x = writeSeg 1 rowY rowStyle maxX (prefix + item.Title) screen

                        match item.Badge with
                        | Some badge ->
                            x <- writeSeg (x + 1) rowY (badgeStyleOf theme badge.Role) maxX badge.Label screen
                        | None -> ()

                        if isSelected then
                            for fillX in x .. maxX - 1 do
                                Screen.setCell fillX rowY selected ' ' screen)

                    match
                        view.Items
                        |> List.tryItem selectedIndex
                        |> Option.bind (fun item -> item.Inspector)
                    with
                    | Some inspector ->
                        renderInspector theme (leftWidth + 2) bodyTop bodyHeight (max 0 (width - 1)) inspector screen
                    | None -> ()
                | SearchResults ->
                    let maxX = max 0 (width - 1)

                    visible
                    |> List.iteri (fun i item ->
                        let isSelected = viewOffset + i = selectedIndex
                        let rowY = bodyTop + i
                        let rowStyle = if isSelected then selected else chrome
                        let prefix = if isSelected then "> " else "  "
                        let mutable x = writeSeg 1 rowY rowStyle maxX prefix screen
                        x <- writeSeg x rowY (actionKeyStyleOf theme true Primary) maxX $"[{item.Title}]" screen

                        match item.Subtitle with
                        | Some sub -> x <- writeSeg (x + 1) rowY rowStyle maxX sub screen
                        | None -> ()

                        for acc in item.Accessories do
                            x <- writeSeg (x + 2) rowY chrome maxX (accessoryText acc) screen

                        match item.Badge with
                        | Some badge ->
                            writeSeg (x + 2) rowY (badgeStyleOf theme badge.Role) maxX badge.Label screen
                            |> ignore
                        | None -> ())

        if dockHeight > 1 then
            renderPickerFooter theme footerY width view.Footer screen

    let render model =
        let theme = effectiveTheme model
        let accent = accentOf theme
        let status = statusOf theme
        let selected = selectedOf theme
        let chrome = chromeOf theme
        let commandBar = promptOf theme
        let width = max 1 model.Terminal.Width
        let height = max 1 model.Terminal.Height

        // All dock/editor geometry comes from the same pass that the mouse
        // hit-testing in `Editor` uses, so paint and input can't drift.
        let metrics = Dock.metrics model
        let pickerView = metrics.PickerView
        let panel = metrics.Panel
        let dockHeight = metrics.DockHeight
        let statusY = metrics.StatusY
        let dockY = metrics.DockY
        let commandY = metrics.CommandY
        let mainHeight = metrics.MainHeight
        let sidebarWidth = metrics.SidebarWidth
        let editorX = metrics.EditorX
        let editorWidth = metrics.EditorWidth
        let screen = Screen.create width height
        let mutable current = screen

        if sidebarWidth > 0 then
            renderSidebar sidebarWidth mainHeight current model
            Screen.drawVerticalLine sidebarWidth 0 mainHeight chrome '│' current

        current <- renderEditor editorX editorWidth mainHeight current model
        Screen.fillRect 0 statusY width 1 status ' ' current
        let statusInner = max 0 (width - 2)
        // Status.render lays the format itself, including `<EXPAND>`
        // spacing, so we don't pad after the fact.
        Screen.writeText 1 statusY status statusInner (Status.render statusInner model) current

        if dockHeight > 0 then
            Screen.fillRect 0 dockY width dockHeight chrome ' ' current

        match panel with
        | NoDock -> ()
        | DockInfo(title, lines) ->
            Screen.writeText 0 dockY accent width (pad width $" {title} ") current

            lines
            |> List.truncate (max 0 (dockHeight - 1))
            |> List.iteri (fun index lineText ->
                Screen.writeText
                    1
                    (dockY + index + 1)
                    chrome
                    (max 0 (width - 2))
                    (pad (max 0 (width - 2)) lineText)
                    current)
        | DockCompletions(title, items, selectedIndex) ->
            let visibleHeight = max 0 (dockHeight - 1)
            let totalCount = items.Length
            let titleWithCount = $" {title} ({selectedIndex + 1}/{totalCount}) "
            Screen.writeText 0 dockY accent width (pad width titleWithCount) current

            if visibleHeight > 0 then
                let viewOffset =
                    if selectedIndex < visibleHeight then
                        0
                    else
                        selectedIndex - visibleHeight + 1
                    |> min (max 0 (totalCount - visibleHeight))

                items
                |> List.skip viewOffset
                |> List.truncate visibleHeight
                |> List.iteri (fun i item ->
                    let actualIndex = viewOffset + i
                    let isSelected = actualIndex = selectedIndex
                    let rowY = dockY + i + 1
                    let rowWidth = max 0 (width - 2)
                    let style = if isSelected then selected else chrome
                    let prefix = if isSelected then "> " else "  "

                    // Render prefix and label in the main style
                    let labelPart = $"{prefix}{item.Label}"
                    Screen.writeText 1 rowY style rowWidth (pad rowWidth labelPart) current

                    // Render detail in a dimmed style if not selected, or just append if selected
                    if not (String.IsNullOrEmpty item.Detail) then
                        let detailX = 1 + labelPart.Length + 2
                        let detailWidth = max 0 (rowWidth - labelPart.Length - 2)

                        if detailWidth > 0 then
                            let detailStyle = if isSelected then { style with Bold = false } else chrome
                            Screen.writeText detailX rowY detailStyle detailWidth (pad detailWidth item.Detail) current)

        match pickerView with
        | Some view -> renderPicker theme dockY width dockHeight view current
        | None -> ()

        Screen.fillRect 0 commandY width 1 commandBar ' ' current

        let prompt = model.Prompt

        let lineText =
            if prompt.Active then
                let displayPrefix = promptDisplayPrefix prompt.Session

                let suffix =
                    match prompt.Mode, prompt.SearchPreview with
                    | Search, Some preview when not preview.Matches.IsEmpty ->
                        $"  ({preview.Current + 1}/{preview.Matches.Length})"
                    | _ -> ""

                displayPrefix + prompt.Text + suffix
            else
                ""

        Screen.writeText 0 commandY commandBar width (pad width lineText) current

        if prompt.Active then
            let displayPrefix = promptDisplayPrefix prompt.Session

            current <-
                Screen.withCursor
                    { Left = min (width - 1) (displayPrefix.Length + prompt.Cursor)
                      Top = commandY
                      Visible = true }
                    current

        current
