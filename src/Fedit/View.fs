namespace Fedit

open System

[<RequireQualifiedAccess>]
module Layout =
    let private surface = Style.withColors (Indexed 252) Default
    let private chrome = Style.withColors (Indexed 244) Default
    let private commandBar = Style.withColors (Indexed 230) (Indexed 237)
    let private lineNumber = Style.withColors (Indexed 241) Default

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
        model.CommandBar.PreviewTheme |> Option.defaultValue model.Theme

    let private workspaceMetadataLines workspace =
        match Workspace.metadata workspace with
        | Some meta ->
            let typeLine = if meta.IsDirectory then "Type: Directory" else "Type: File"

            let countLine =
                match meta.ChildCount with
                | Some n -> $"Children: {n}"
                | None -> "Enter to open"

            [ $"Path: {meta.Path}"; typeLine; countLine; "Ctrl+B tree"; "Ctrl+E editor" ]
        | None -> [ "No file selected." ]

    let statusLine model =
        let buffer = Editor.activeBufferState model

        let focusText =
            match model.Focus with
            | Sidebar -> "TREE"
            | Editor -> "EDIT"
            | CommandBar -> "CMD"
            | Search -> "FIND"

        let dirty = if buffer.Dirty then " [+]" else ""

        let note =
            model.Notification
            |> Option.map _.Message
            |> Option.defaultValue "Ctrl+P commands"

        let pathText = buffer.FilePath |> Option.defaultValue "[scratch]"
        let newlineStyle = if buffer.Newline = "\r\n" then "CRLF" else "LF"
        let totalLines = Buffer.lineCount buffer
        let bufferCount = model.Editors.Buffers.Count

        $"{focusText}  {pathText}{dirty}  Ln {buffer.Cursor.Line + 1}/{totalLines}, Col {buffer.Cursor.Column + 1}  {newlineStyle}  buf {bufferCount}  {note}"

    let dockPanel model =
        if model.CommandBar.Active && not model.CommandBar.Completions.IsEmpty then
            DockCompletions("Completions", model.CommandBar.Completions, model.CommandBar.SelectedCompletion)
        elif model.CommandBar.Active then
            let lines =
                match model.CommandBar.Parsed with
                | Empty -> Commands.helpLines () |> List.truncate 4
                | Pending message -> [ message ]
                | Invalid message -> [ message ]
                | Ready _ -> [ "Press Enter to run the command." ]

            DockInfo("Command", lines)
        elif model.Focus = Sidebar then
            DockInfo("File Tree", workspaceMetadataLines model.Workspace)
        else
            let buffer = Editor.activeBufferState model

            DockInfo(
                "Editor",
                [ $"Buffer: {buffer.Name}"
                  $"Open buffers: {model.Editors.Buffers.Count}"
                  "Ctrl+B tree, Ctrl+E editor"
                  "Ctrl+P commands, Ctrl+S save"
                  "Tab indent, Shift+Tab unindent" ]
            )

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

            let indentation = String.replicate entry.Depth "  "

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
            match model.Search with
            | Some s when s.Query.Length > 0 -> Some(s.Query.Length, s.Matches)
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
                        selected
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
        let dockHeight = min model.Panels.DockHeight (max 3 (height / 3))
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
        Screen.writeText 0 statusY status width (pad width (statusLine model)) current
        Screen.fillRect 0 dockY width dockHeight chrome ' ' current

        match dockPanel model with
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
            Screen.writeText 0 dockY accent width (pad width $" {title} ") current

            items
            |> List.truncate (max 0 (dockHeight - 1))
            |> List.iteri (fun index item ->
                let style = if index = selectedIndex then selected else chrome
                let prefix = if index = selectedIndex then "> " else "  "

                Screen.writeText
                    1
                    (dockY + index + 1)
                    style
                    (max 0 (width - 2))
                    (pad (max 0 (width - 2)) $"{prefix}{item.Label}  {item.Detail}")
                    current)

        Screen.fillRect 0 commandY width 1 commandBar ' ' current

        let lineText =
            match model.Search with
            | Some search ->
                let count = search.Matches.Length

                let position =
                    if count = 0 then
                        ""
                    else
                        $"  ({search.Current + 1}/{count})"

                $"/{search.Query}{position}"
            | None ->
                if model.CommandBar.Active then
                    ":" + model.CommandBar.Text
                else
                    " Ctrl+P commands  Ctrl+B tree  Ctrl+F find  Ctrl+S save  Ctrl+Q quit "

        Screen.writeText 0 commandY commandBar width (pad width lineText) current

        if model.CommandBar.Active then
            current <-
                Screen.withCursor
                    { Left = min (width - 1) (1 + model.CommandBar.Cursor)
                      Top = commandY
                      Visible = true }
                    current
        elif model.Search.IsSome then
            let queryLength =
                match model.Search with
                | Some s -> s.Query.Length
                | None -> 0

            current <-
                Screen.withCursor
                    { Left = min (width - 1) (1 + queryLength)
                      Top = commandY
                      Visible = true }
                    current

        current
