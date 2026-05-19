namespace Fedit

open System

[<RequireQualifiedAccess>]
module Layout =
    let private surface = Style.withColors (Indexed 252) Default
    let private chrome = Style.withColors (Indexed 244) Default
    let private commandBar = Style.withColors (Indexed 230) (Indexed 237)
    let private lineNumber = Style.withColors (Indexed 241) Default
    // Active editor line background — dim gray, fixed (intentionally not
    // theme-derived: the theme accent is too saturated to use behind text).
    let private currentLineBg = Style.withColors (Indexed 252) (Indexed 236)

    let private accentOf (theme: Theme) =
        Style.withColors (Indexed theme.Accent) Default

    let private statusOf (theme: Theme) =
        Style.withColors (Indexed theme.StatusFg) (Indexed theme.StatusBg)

    let private selectedOf (theme: Theme) =
        { commandBar with
            Background = Indexed theme.SelectedBg
            Bold = true }

    let private currentLineOf (theme: Theme) =
        Style.withColors (Indexed theme.CurrentLine) Default

    let private effectiveTheme model =
        model.Prompt.PreviewTheme |> Option.defaultValue model.Config.Theme

    let private promptModeLabel mode =
        match mode with
        | FilePicker -> "FILE"
        | Command -> "CMD"
        | Search -> "FIND"
        | Buffers -> "BUF"

    let statusLine model =
        let buffer = Editor.activeBufferState model

        let focusText =
            match model.Focus with
            | Sidebar ->
                if model.Workspace.SearchBuffer.Length > 0 then
                    $"TREE  find:{model.Workspace.SearchBuffer}"
                else
                    "TREE"
            | Editor -> "EDIT"
            | Prompt -> promptModeLabel model.Prompt.Mode

        let dirty = if buffer.Dirty then " [+]" else ""

        let note =
            model.Notification
            |> Option.map _.Message
            |> Option.defaultValue "Ctrl+P prompt"

        let pathText = buffer.FilePath |> Option.defaultValue "[scratch]"
        let newlineStyle = if buffer.Newline = "\r\n" then "CRLF" else "LF"
        let totalLines = Buffer.lineCount buffer
        let bufferCount = model.Editors.Buffers.Count

        $"{focusText}  {pathText}{dirty}  Ln {buffer.Cursor.Line + 1}/{totalLines}, Col {buffer.Cursor.Column + 1}  {newlineStyle}  buf {bufferCount}  {note}"

    let dockPanel model =
        let prompt = model.Prompt

        if prompt.Active then
            match prompt.Mode with
            | FilePicker when not prompt.Completions.IsEmpty ->
                DockCompletions("Files", prompt.Completions, prompt.SelectedCompletion)
            | FilePicker -> DockInfo("Files", [ "Type to filter recent + workspace files." ])
            | Command when not prompt.Completions.IsEmpty ->
                DockCompletions("Commands", prompt.Completions, prompt.SelectedCompletion)
            | Command ->
                let lines =
                    match prompt.Parsed with
                    | Ready(Command.Goto(line, None)) -> [ $"Press Enter to jump to line {line}." ]
                    | Ready(Command.Goto(line, Some col)) -> [ $"Press Enter to jump to line {line}, column {col}." ]
                    | Ready _ -> [ "Press Enter to run the command." ]
                    | Pending message -> [ message ]
                    | Invalid message -> [ message ]
                    | Empty -> Commands.helpLines () |> List.truncate (max 0 (model.Panels.DockHeight - 1))

                DockInfo("Commands", lines)
            | Buffers when not prompt.Completions.IsEmpty ->
                DockCompletions("Buffers", prompt.Completions, prompt.SelectedCompletion)
            | Buffers -> DockInfo("Buffers", [ "Type buffer id or name." ])
            | Search ->
                let line =
                    match prompt.SearchPreview with
                    | Some preview when preview.Matches.IsEmpty -> "no matches"
                    | Some preview -> $"match {preview.Current + 1}/{preview.Matches.Length}"
                    | None -> "Type to search the active buffer."

                DockInfo("Find", [ line ])
        else
            NoDock

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
        let selected = selectedOf (effectiveTheme model)
        Screen.fillRect 0 0 width height surface ' ' screen

        let entries = Workspace.visibleEntries model.Workspace

        let selectedIndex =
            entries |> List.tryFindIndex _.IsSelected |> Option.defaultValue 0

        let startIndex =
            max 0 (min (max 0 (entries.Length - height)) (selectedIndex - (height / 2)))

        entries
        |> List.skip startIndex
        |> List.truncate height
        |> List.iteri (fun row entry ->
            let marker =
                if entry.IsDirectory then
                    if entry.IsExpanded then "[-] " else "[+] "
                else
                    "    "

            let indentation = String.replicate (entry.Depth * model.Config.SidebarIndent) " "

            let text = $"{indentation}{marker}{entry.Name}"
            Screen.writeText 0 row (if entry.IsSelected then selected else surface) width (pad width text) screen)

    let private renderEditor x width height screen model =
        let buffer = Editor.activeBufferState model
        let theme = effectiveTheme model
        let selected = selectedOf theme
        let currentLineNumber = currentLineOf theme
        let gutterWidth = Buffer.gutterWidth buffer
        let digits = gutterWidth - 2
        let rows = Buffer.lines buffer |> List.toArray
        let contentWidth = max 1 (width - gutterWidth)

        Screen.fillRect x 0 width height surface ' ' screen

        let lineStarts = Array.zeroCreate rows.Length
        let mutable accum = 0

        for i in 0 .. rows.Length - 1 do
            lineStarts[i] <- accum
            accum <- accum + rows[i].Length + 1

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

                match Buffer.selectionRange buffer with
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
            let cursorX = x + gutterWidth + (buffer.Cursor.Column - buffer.ViewportLeft)
            let cursorY = buffer.Cursor.Line - buffer.ViewportTop

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

    let render model =
        let theme = effectiveTheme model
        let accent = accentOf theme
        let status = statusOf theme
        let selected = selectedOf theme
        let width = max 1 model.Terminal.Width
        let height = max 1 model.Terminal.Height

        let panel = dockPanel model

        let dockHeight =
            match panel with
            | NoDock -> 0
            | _ -> min model.Panels.DockHeight (max 3 (height / 3))

        let statusY = max 0 (height - dockHeight - 2)
        let dockY = max 0 (height - dockHeight - 1)
        let commandY = height - 1
        let mainHeight = max 1 statusY

        let sidebarWidth =
            if model.Panels.SidebarVisible && width >= 40 then
                min model.Panels.SidebarWidth (max 18 (width / 3))
            else
                0

        let editorX = if sidebarWidth > 0 then sidebarWidth + 1 else 0
        let editorWidth = max 1 (width - editorX)
        let screen = Screen.create width height
        let mutable current = screen

        if sidebarWidth > 0 then
            renderSidebar sidebarWidth mainHeight current model
            Screen.drawVerticalLine sidebarWidth 0 mainHeight chrome '|' current

        current <- renderEditor editorX editorWidth mainHeight current model
        Screen.fillRect 0 statusY width 1 status ' ' current
        let statusInner = max 0 (width - 2)
        Screen.writeText 1 statusY status statusInner (pad statusInner (statusLine model)) current

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

        Screen.fillRect 0 commandY width 1 commandBar ' ' current

        let prompt = model.Prompt

        let lineText =
            if prompt.Active then
                let suffix =
                    match prompt.Mode, prompt.SearchPreview with
                    | Search, Some preview when not preview.Matches.IsEmpty ->
                        $"  ({preview.Current + 1}/{preview.Matches.Length})"
                    | _ -> ""

                prompt.Text + suffix
            else
                ""

        Screen.writeText 0 commandY commandBar width (pad width lineText) current

        if prompt.Active then
            current <-
                Screen.withCursor
                    { Left = min (width - 1) prompt.Cursor
                      Top = commandY
                      Visible = true }
                    current

        current
