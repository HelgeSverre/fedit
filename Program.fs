open System
open System.IO
open System.Text
open System.Threading

type Size = { Width: int; Height: int }

type Position = { Line: int; Column: int }

type Severity =
    | Info
    | Warning
    | Error

type Notification = { Severity: Severity; Message: string }

type FocusTarget =
    | Sidebar
    | Editor
    | CommandBar

type KeyInput =
    | Character of char
    | Enter
    | Escape
    | Backspace
    | Delete
    | Tab
    | ShiftTab
    | Left
    | Right
    | Up
    | Down
    | Home
    | End
    | PageUp
    | PageDown
    | Ctrl of char

type CompletionKind =
    | Command
    | PathItem

type CompletionItem =
    { Label: string
      ApplyText: string
      Detail: string
      Kind: CompletionKind }

type DockPanel =
    | DockInfo of title: string * lines: string list
    | DockCompletions of title: string * items: CompletionItem list * selectedIndex: int

module Position =
    let zero = { Line = 0; Column = 0 }

module Notification =
    let info message = { Severity = Info; Message = message }

    let warning message =
        { Severity = Warning
          Message = message }

    let error message = { Severity = Error; Message = message }

type PieceSource =
    | Original
    | Added

type Piece =
    { Source: PieceSource
      Start: int
      Length: int }

type PieceTable =
    { Original: string
      Added: string
      Pieces: Piece list }

[<RequireQualifiedAccess>]
module PieceTable =
    let empty =
        { Original = ""
          Added = ""
          Pieces = [] }

    let ofString (text: string) =
        if String.IsNullOrEmpty text then
            empty
        else
            { Original = text
              Added = ""
              Pieces =
                [ { Source = Original
                    Start = 0
                    Length = text.Length } ] }

    let private slice table piece =
        let source =
            match piece.Source with
            | Original -> table.Original
            | Added -> table.Added

        source.Substring(piece.Start, piece.Length)

    let toString table =
        table.Pieces |> List.map (slice table) |> String.concat ""

    let length table = table.Pieces |> List.sumBy _.Length

    let private trim piece startOffset endOffset =
        let nextLength = piece.Length - startOffset - endOffset

        if nextLength <= 0 then
            None
        else
            Some
                { piece with
                    Start = piece.Start + startOffset
                    Length = nextLength }

    let insert index (text: string) table =
        if String.IsNullOrEmpty text then
            table
        else
            let index = max 0 (min index (length table))

            let insertedPiece =
                { Source = Added
                  Start = table.Added.Length
                  Length = text.Length }

            let rec loop remaining acc pieces =
                match pieces with
                | [] -> List.rev acc @ [ insertedPiece ]
                | piece :: rest when remaining = 0 -> List.rev acc @ (insertedPiece :: piece :: rest)
                | piece :: rest when remaining < piece.Length ->
                    let left = trim piece 0 (piece.Length - remaining)
                    let right = trim piece remaining 0

                    List.rev acc
                    @ (left |> Option.toList)
                    @ [ insertedPiece ]
                    @ (right |> Option.toList)
                    @ rest
                | piece :: rest -> loop (remaining - piece.Length) (piece :: acc) rest

            { table with
                Added = table.Added + text
                Pieces = loop index [] table.Pieces }

    let deleteRange index count table =
        if count <= 0 then
            table
        else
            let startIndex = max 0 index
            let endIndex = min (length table) (startIndex + count)

            if endIndex <= startIndex then
                table
            else
                let rec loop position pieces =
                    match pieces with
                    | [] -> []
                    | piece :: rest ->
                        let pieceStart = position
                        let pieceEnd = position + piece.Length
                        let tail = loop pieceEnd rest

                        if pieceEnd <= startIndex || pieceStart >= endIndex then
                            piece :: tail
                        else
                            let left =
                                let keep = max 0 (startIndex - pieceStart)

                                if keep > 0 then
                                    trim piece 0 (piece.Length - keep) |> Option.toList
                                else
                                    []

                            let right =
                                let keep = max 0 (pieceEnd - endIndex)

                                if keep > 0 then
                                    trim piece (piece.Length - keep) 0 |> Option.toList
                                else
                                    []

                            left @ right @ tail

                { table with
                    Pieces = loop 0 table.Pieces }

type BufferRevision =
    { Document: PieceTable
      Cursor: Position
      PreferredColumn: int option
      Dirty: bool }

type BufferState =
    { Id: int
      FilePath: string option
      Name: string
      Document: PieceTable
      Cursor: Position
      PreferredColumn: int option
      ViewportTop: int
      ViewportLeft: int
      Dirty: bool
      Newline: string
      Undo: BufferRevision list
      Redo: BufferRevision list }

[<RequireQualifiedAccess>]
module Buffer =
    let private tabText = "    "

    let private snapshot buffer =
        { Document = buffer.Document
          Cursor = buffer.Cursor
          PreferredColumn = buffer.PreferredColumn
          Dirty = buffer.Dirty }

    let private pushUndo buffer =
        { buffer with
            Undo = snapshot buffer :: buffer.Undo
            Redo = [] }

    let createEmpty id =
        { Id = id
          FilePath = None
          Name = "scratch"
          Document = PieceTable.empty
          Cursor = Position.zero
          PreferredColumn = None
          ViewportTop = 0
          ViewportLeft = 0
          Dirty = false
          Newline = "\n"
          Undo = []
          Redo = [] }

    let fromText id filePath name text newline =
        { Id = id
          FilePath = filePath
          Name = name
          Document = PieceTable.ofString text
          Cursor = Position.zero
          PreferredColumn = None
          ViewportTop = 0
          ViewportLeft = 0
          Dirty = false
          Newline = newline
          Undo = []
          Redo = [] }

    let text buffer = PieceTable.toString buffer.Document

    let private rawLines buffer =
        let contents = text buffer

        if String.IsNullOrEmpty contents then
            [| "" |]
        else
            contents.Split('\n')

    let lines buffer = rawLines buffer |> Array.toList

    let line lineIndex buffer =
        rawLines buffer |> Array.tryItem lineIndex |> Option.defaultValue ""

    let lineCount buffer = rawLines buffer |> Array.length

    let gutterWidth buffer =
        let digits = max 3 (string (lineCount buffer)).Length
        digits + 2

    let private clamp position buffer =
        let rows = rawLines buffer
        let lineIndex = max 0 (min position.Line (rows.Length - 1))

        { Line = lineIndex
          Column = max 0 (min position.Column rows[lineIndex].Length) }

    let positionToIndex position buffer =
        let rows = rawLines buffer
        let safe = clamp position buffer

        (rows |> Seq.take safe.Line |> Seq.sumBy (fun lineText -> lineText.Length + 1))
        + safe.Column

    let indexToPosition index buffer =
        let bounded = max 0 (min index (text buffer).Length)
        let rows = rawLines buffer

        let rec loop remaining lineIndex =
            let lineLength = rows[lineIndex].Length

            if lineIndex = rows.Length - 1 || remaining <= lineLength then
                { Line = lineIndex; Column = remaining }
            else
                loop (remaining - lineLength - 1) (lineIndex + 1)

        loop bounded 0

    let private moveToIndex index (buffer: BufferState) =
        { buffer with
            Cursor = indexToPosition index buffer
            PreferredColumn = None }

    let private changeDocument newDocument newCursor (buffer: BufferState) =
        { (pushUndo buffer) with
            Document = newDocument
            Cursor = newCursor
            PreferredColumn = None
            Dirty = true }

    let private replaceRange startIndex count replacement buffer =
        let deleted = PieceTable.deleteRange startIndex count buffer.Document
        let inserted = PieceTable.insert startIndex replacement deleted

        let nextCursor =
            indexToPosition (startIndex + replacement.Length) { buffer with Document = inserted }

        changeDocument inserted nextCursor buffer

    let insertText value buffer =
        replaceRange (positionToIndex buffer.Cursor buffer) 0 value buffer

    let insertNewline buffer = insertText "\n" buffer

    let backspace buffer =
        let index = positionToIndex buffer.Cursor buffer

        if index = 0 then
            buffer
        else
            let nextDocument = PieceTable.deleteRange (index - 1) 1 buffer.Document
            let nextCursor = indexToPosition (index - 1) { buffer with Document = nextDocument }
            changeDocument nextDocument nextCursor buffer

    let deleteForward buffer =
        let index = positionToIndex buffer.Cursor buffer

        if index >= PieceTable.length buffer.Document then
            buffer
        else
            let nextDocument = PieceTable.deleteRange index 1 buffer.Document
            let nextCursor = indexToPosition index { buffer with Document = nextDocument }
            changeDocument nextDocument nextCursor buffer

    let private withCursor position (buffer: BufferState) =
        { buffer with
            Cursor = clamp position buffer }

    let private withPreferredColumn column (buffer: BufferState) =
        { buffer with PreferredColumn = column }

    let moveLeft buffer =
        moveToIndex (max 0 (positionToIndex buffer.Cursor buffer - 1)) buffer

    let moveRight buffer =
        moveToIndex (positionToIndex buffer.Cursor buffer + 1) buffer

    let moveUp buffer =
        let targetColumn =
            buffer.PreferredColumn |> Option.defaultValue buffer.Cursor.Column

        buffer
        |> withCursor
            { Line = max 0 (buffer.Cursor.Line - 1)
              Column = targetColumn }
        |> withPreferredColumn (Some targetColumn)

    let moveDown buffer =
        let targetColumn =
            buffer.PreferredColumn |> Option.defaultValue buffer.Cursor.Column

        buffer
        |> withCursor
            { Line = min (lineCount buffer - 1) (buffer.Cursor.Line + 1)
              Column = targetColumn }
        |> withPreferredColumn (Some targetColumn)

    let moveHome (buffer: BufferState) =
        { buffer with
            Cursor = { buffer.Cursor with Column = 0 }
            PreferredColumn = None }

    let moveEnd (buffer: BufferState) =
        { buffer with
            Cursor =
                { buffer.Cursor with
                    Column = (line buffer.Cursor.Line buffer).Length }
            PreferredColumn = None }

    let movePageUp amount buffer =
        let targetColumn = buffer.Cursor.Column

        buffer
        |> withCursor
            { Line = max 0 (buffer.Cursor.Line - max 1 amount)
              Column = targetColumn }
        |> withPreferredColumn (Some targetColumn)

    let movePageDown amount buffer =
        let targetColumn = buffer.Cursor.Column

        buffer
        |> withCursor
            { Line = min (lineCount buffer - 1) (buffer.Cursor.Line + max 1 amount)
              Column = targetColumn }
        |> withPreferredColumn (Some targetColumn)

    let indent buffer = insertText tabText buffer

    let unindent buffer =
        let currentLine = line buffer.Cursor.Line buffer

        let removable =
            currentLine |> Seq.takeWhile ((=) ' ') |> Seq.length |> min tabText.Length

        if removable = 0 then
            buffer
        else
            let lineStart =
                positionToIndex
                    { Line = buffer.Cursor.Line
                      Column = 0 }
                    buffer

            let nextDocument = PieceTable.deleteRange lineStart removable buffer.Document

            let nextCursor =
                { Line = buffer.Cursor.Line
                  Column =
                    if buffer.Cursor.Column <= removable then
                        0
                    else
                        buffer.Cursor.Column - removable }

            changeDocument nextDocument nextCursor buffer

    let ensureViewport viewportHeight viewportWidth buffer =
        let safeHeight = max 1 viewportHeight
        let safeWidth = max 1 viewportWidth
        let maxTop = max 0 (lineCount buffer - safeHeight)
        let maxLeft = max 0 ((line buffer.Cursor.Line buffer).Length - safeWidth)

        let nextTop =
            if buffer.Cursor.Line < buffer.ViewportTop then
                buffer.Cursor.Line
            elif buffer.Cursor.Line >= buffer.ViewportTop + safeHeight then
                buffer.Cursor.Line - safeHeight + 1
            else
                buffer.ViewportTop
            |> max 0
            |> min maxTop

        let nextLeft =
            if buffer.Cursor.Column < buffer.ViewportLeft then
                buffer.Cursor.Column
            elif buffer.Cursor.Column >= buffer.ViewportLeft + safeWidth then
                buffer.Cursor.Column - safeWidth + 1
            else
                buffer.ViewportLeft
            |> max 0
            |> min maxLeft

        { buffer with
            ViewportTop = nextTop
            ViewportLeft = nextLeft }

    let markSaved filePath (buffer: BufferState) =
        { buffer with
            FilePath = Some filePath
            Name = Path.GetFileName filePath
            Dirty = false
            Undo = []
            Redo = [] }

    let undo buffer =
        match buffer.Undo with
        | previous :: rest ->
            let current = snapshot buffer

            { buffer with
                Document = previous.Document
                Cursor = previous.Cursor
                PreferredColumn = previous.PreferredColumn
                Dirty = previous.Dirty
                Undo = rest
                Redo = current :: buffer.Redo }
        | [] -> buffer

    let redo buffer =
        match buffer.Redo with
        | next :: rest ->
            let current = snapshot buffer

            { buffer with
                Document = next.Document
                Cursor = next.Cursor
                PreferredColumn = next.PreferredColumn
                Dirty = next.Dirty
                Undo = current :: buffer.Undo
                Redo = rest }
        | [] -> buffer

type FileNode =
    { Path: string
      Name: string
      IsDirectory: bool
      Children: FileNode list }

type WorkspaceEntry =
    { Path: string
      Name: string
      Depth: int
      IsDirectory: bool
      IsExpanded: bool
      IsSelected: bool }

type WorkspaceState =
    { RootPath: string
      Tree: FileNode option
      Expanded: Set<string>
      SelectedPath: string option }

type SidebarAction =
    | SidebarNoOp
    | SidebarOpenFile of string

[<RequireQualifiedAccess>]
module Workspace =
    let excludedNames = set [ ".DS_Store"; ".git"; ".dotnet"; "bin"; "obj" ]

    let create rootPath =
        { RootPath = rootPath
          Tree = None
          Expanded = Set.singleton rootPath
          SelectedPath = None }

    let private sortChildren (nodes: FileNode list) =
        nodes
        |> List.sortBy (fun node -> (not node.IsDirectory, node.Name.ToLowerInvariant()))

    let rec private flatten selected expanded depth (node: FileNode) =
        let entry =
            { Path = node.Path
              Name = node.Name
              Depth = depth
              IsDirectory = node.IsDirectory
              IsExpanded = node.IsDirectory && Set.contains node.Path expanded
              IsSelected = Some node.Path = selected }

        if node.IsDirectory && Set.contains node.Path expanded then
            entry
            :: (node.Children
                |> sortChildren
                |> List.collect (flatten selected expanded (depth + 1)))
        else
            [ entry ]

    let visibleEntries workspace =
        match workspace.Tree with
        | Some tree -> flatten workspace.SelectedPath workspace.Expanded 0 tree
        | None -> []

    let private ensureSelected workspace =
        let visible = visibleEntries workspace

        match workspace.SelectedPath, visible with
        | Some selectedPath, _ when visible |> List.exists (fun entry -> entry.Path = selectedPath) -> workspace
        | _, first :: _ ->
            { workspace with
                SelectedPath = Some first.Path }
        | _ -> workspace

    let setTree (tree: FileNode) workspace =
        { workspace with
            Tree = Some tree
            Expanded =
                if tree.IsDirectory then
                    Set.add tree.Path workspace.Expanded
                else
                    workspace.Expanded }
        |> ensureSelected

    let selectPath path workspace =
        { workspace with
            SelectedPath = Some path }
        |> ensureSelected

    let moveSelection delta workspace =
        let visible = visibleEntries workspace

        match visible with
        | [] -> workspace
        | _ ->
            let currentIndex =
                workspace.SelectedPath
                |> Option.bind (fun path -> visible |> List.tryFindIndex (fun entry -> entry.Path = path))
                |> Option.defaultValue 0

            { workspace with
                SelectedPath = Some visible[max 0 (min (visible.Length - 1) (currentIndex + delta))].Path }

    let moveHome workspace =
        match visibleEntries workspace with
        | first :: _ ->
            { workspace with
                SelectedPath = Some first.Path }
        | [] -> workspace

    let moveEnd workspace =
        match visibleEntries workspace |> List.tryLast with
        | Some last ->
            { workspace with
                SelectedPath = Some last.Path }
        | None -> workspace

    let private findNodeByPath path workspace =
        let rec loop (node: FileNode) =
            if node.Path = path then
                Some node
            else
                node.Children |> List.tryPick loop

        workspace.Tree |> Option.bind loop

    let expandSelected workspace =
        match
            workspace.SelectedPath
            |> Option.bind (fun path -> findNodeByPath path workspace)
        with
        | Some node when node.IsDirectory ->
            { workspace with
                Expanded = Set.add node.Path workspace.Expanded }
        | _ -> workspace

    let collapseSelected workspace =
        match
            visibleEntries workspace
            |> List.tryFind (fun entry -> Some entry.Path = workspace.SelectedPath)
        with
        | Some entry when entry.IsDirectory && entry.IsExpanded ->
            { workspace with
                Expanded = Set.remove entry.Path workspace.Expanded }
        | Some entry ->
            let parent = Path.GetDirectoryName entry.Path

            if String.IsNullOrEmpty parent then
                workspace
            else
                selectPath parent workspace
        | None -> workspace

    let activateSelected workspace =
        match
            workspace.SelectedPath
            |> Option.bind (fun path -> findNodeByPath path workspace)
        with
        | Some node when node.IsDirectory ->
            let expanded =
                if Set.contains node.Path workspace.Expanded then
                    Set.remove node.Path workspace.Expanded
                else
                    Set.add node.Path workspace.Expanded

            { workspace with Expanded = expanded }, SidebarNoOp
        | Some node -> workspace, SidebarOpenFile node.Path
        | None -> workspace, SidebarNoOp

    let metadata workspace =
        match
            workspace.SelectedPath
            |> Option.bind (fun path -> findNodeByPath path workspace)
        with
        | Some node ->
            let relativePath = Path.GetRelativePath(workspace.RootPath, node.Path)
            let label = if relativePath = "." then node.Path else relativePath
            let nodeType = if node.IsDirectory then "Directory" else "File"

            [ $"Path: {label}"
              $"Type: {nodeType}"
              (if node.IsDirectory then
                   $"Children: {node.Children.Length}"
               else
                   "Enter to open")
              "Ctrl+B tree"
              "Ctrl+E editor" ]
        | None -> [ "No file selected." ]

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

type Command =
    | Open of string
    | Write
    | WriteAs of string
    | Quit
    | ToggleSidebar
    | FocusTree
    | FocusEditor
    | ReloadWorkspace
    | NextBuffer
    | PreviousBuffer
    | Help
    | Theme of string

type ParsedCommand =
    | Empty
    | Ready of Command
    | Pending of string
    | Invalid of string

type CommandContext =
    { RootPath: string; Files: string list }

[<RequireQualifiedAccess>]
module Commands =
    type Spec =
        { Name: string
          Usage: string
          Summary: string
          Constructor: string -> ParsedCommand }

    let private simple command argument =
        if String.IsNullOrWhiteSpace argument then
            Ready command
        else
            Invalid "This command does not take arguments."

    let specs =
        [ { Name = "open"
            Usage = "open <path>"
            Summary = "Open a file from the workspace."
            Constructor =
              fun argument ->
                  if String.IsNullOrWhiteSpace argument then
                      Pending "Path is required."
                  else
                      Ready(Open(argument.Trim())) }
          { Name = "write"
            Usage = "write"
            Summary = "Save the active buffer."
            Constructor = simple Write }
          { Name = "writeas"
            Usage = "writeas <path>"
            Summary = "Save the active buffer to a new path."
            Constructor =
              fun argument ->
                  if String.IsNullOrWhiteSpace argument then
                      Pending "Target path is required."
                  else
                      Ready(WriteAs(argument.Trim())) }
          { Name = "quit"
            Usage = "quit"
            Summary = "Exit fedit."
            Constructor = simple Quit }
          { Name = "sidebar"
            Usage = "sidebar"
            Summary = "Toggle the sidebar."
            Constructor = simple ToggleSidebar }
          { Name = "tree"
            Usage = "tree"
            Summary = "Focus the file tree."
            Constructor = simple FocusTree }
          { Name = "editor"
            Usage = "editor"
            Summary = "Focus the editor."
            Constructor = simple FocusEditor }
          { Name = "reload"
            Usage = "reload"
            Summary = "Reload the workspace tree."
            Constructor = simple ReloadWorkspace }
          { Name = "next"
            Usage = "next"
            Summary = "Activate the next open buffer."
            Constructor = simple NextBuffer }
          { Name = "prev"
            Usage = "prev"
            Summary = "Activate the previous open buffer."
            Constructor = simple PreviousBuffer }
          { Name = "help"
            Usage = "help"
            Summary = "Show command help in the dock panel."
            Constructor = simple Help }
          { Name = "theme"
            Usage = "theme <name>"
            Summary = "Switch accent color (cyan, teal, green, yellow, orange, red, magenta, purple)."
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Theme name required."
                  else
                      match Themes.tryFind trimmed with
                      | Some _ -> Ready(Theme trimmed)
                      | None -> Invalid $"Unknown theme '{trimmed}'." } ]

    let parse (input: string) =
        let trimmed = input.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            Empty
        else
            let firstSpace = trimmed.IndexOf ' '

            let name, argument =
                if firstSpace < 0 then
                    trimmed, ""
                else
                    trimmed[.. firstSpace - 1], trimmed[firstSpace + 1 ..]

            match specs |> List.tryFind (fun spec -> spec.Name = name.ToLowerInvariant()) with
            | Some spec -> spec.Constructor argument
            | None when
                specs
                |> List.exists (fun spec -> spec.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                ->
                Pending "Command is incomplete."
            | None -> Invalid $"Unknown command '{name}'."

    let completions context (input: string) =
        let trimmed = input.TrimStart()

        if String.IsNullOrWhiteSpace trimmed then
            specs
            |> List.map (fun spec ->
                { Label = spec.Name
                  ApplyText = spec.Name
                  Detail = spec.Summary
                  Kind = Command })
        else
            let firstSpace = trimmed.IndexOf ' '

            if firstSpace < 0 then
                specs
                |> List.filter (fun spec -> spec.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                |> List.map (fun spec ->
                    { Label = spec.Name
                      ApplyText = spec.Name
                      Detail = spec.Summary
                      Kind = Command })
            else
                let commandName = trimmed[.. firstSpace - 1].ToLowerInvariant()
                let argument = trimmed[firstSpace + 1 ..].TrimStart()

                match commandName with
                | "open"
                | "writeas" ->
                    context.Files
                    |> List.filter (fun filePath -> filePath.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.truncate 8
                    |> List.map (fun filePath ->
                        { Label = filePath
                          ApplyText = $"{commandName} {filePath}"
                          Detail = "workspace file"
                          Kind = PathItem })
                | "theme" ->
                    Themes.all
                    |> List.filter (fun theme -> theme.Name.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun theme ->
                        { Label = theme.Name
                          ApplyText = $"theme {theme.Name}"
                          Detail = theme.Description
                          Kind = PathItem })
                | _ -> []

    let helpLines () =
        specs |> List.map (fun spec -> $"{spec.Usage}  {spec.Summary}")

type EditorsState =
    { Buffers: Map<int, BufferState>
      ActiveBufferId: int
      NextBufferId: int }

type CommandBarState =
    { Active: bool
      Text: string
      Cursor: int
      Parsed: ParsedCommand
      Completions: CompletionItem list
      SelectedCompletion: int
      History: string list
      HistoryIndex: int option
      PreviewTheme: Theme option }

type PanelsState =
    { SidebarVisible: bool
      SidebarWidth: int
      DockHeight: int }

type Model =
    { Workspace: WorkspaceState
      Editors: EditorsState
      CommandBar: CommandBarState
      Panels: PanelsState
      Focus: FocusTarget
      Terminal: Size
      Notification: Notification option
      Theme: Theme
      ShouldQuit: bool }

type Msg =
    | KeyPressed of KeyInput
    | Resize of Size
    | WorkspaceLoaded of Result<FileNode, string>
    | FileOpened of path: string * Result<string, string>
    | BufferSaved of bufferId: int * path: string * Result<unit, string>
    | ConfigSaved of Result<unit, string>

type Effect =
    | ScanWorkspace of string
    | LoadFile of string
    | SaveBuffer of bufferId: int * path: string * contents: string
    | SaveConfig of themeName: string

[<RequireQualifiedAccess>]
module Editor =
    let private emptyCommandBar =
        { Active = false
          Text = ""
          Cursor = 0
          Parsed = Empty
          Completions = []
          SelectedCompletion = 0
          History = []
          HistoryIndex = None
          PreviewTheme = None }

    let private initialPanels =
        { SidebarVisible = true
          SidebarWidth = 30
          DockHeight = 5 }

    let private initialEditors =
        let scratch = Buffer.createEmpty 1

        { Buffers = Map.ofList [ 1, scratch ]
          ActiveBufferId = 1
          NextBufferId = 2 }

    let private notify notification model =
        { model with
            Notification = notification }

    let activeBufferState model =
        model.Editors.Buffers[model.Editors.ActiveBufferId]

    let private updateActiveBuffer transform model =
        let transformed = activeBufferState model |> transform

        let sidebarOffset =
            if model.Panels.SidebarVisible then
                model.Panels.SidebarWidth + 1
            else
                0

        let viewportWidth =
            max 1 (model.Terminal.Width - sidebarOffset - Buffer.gutterWidth transformed)

        let viewportHeight = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
        let updated = transformed |> Buffer.ensureViewport viewportHeight viewportWidth

        { model with
            Editors =
                { model.Editors with
                    Buffers = model.Editors.Buffers |> Map.add updated.Id updated } }

    let private workspaceFiles (workspace: WorkspaceState) =
        let rec collect (node: FileNode) =
            if node.IsDirectory then
                node.Children |> List.collect collect
            else
                [ Path.GetRelativePath(workspace.RootPath, node.Path) ]

        workspace.Tree |> Option.map collect |> Option.defaultValue []

    let private themeFromApplyText (applyText: string) =
        if applyText.StartsWith("theme ", StringComparison.OrdinalIgnoreCase) then
            Themes.tryFind (applyText.Substring 6)
        else
            None

    let private updatePreview model =
        if not model.CommandBar.Active then
            { model with
                CommandBar =
                    { model.CommandBar with
                        PreviewTheme = None } }
        else
            let fromCompletion =
                model.CommandBar.Completions
                |> List.tryItem model.CommandBar.SelectedCompletion
                |> Option.bind (fun item -> themeFromApplyText item.ApplyText)

            let preview =
                match fromCompletion, model.CommandBar.Parsed with
                | Some _, _ -> fromCompletion
                | None, Ready(Theme name) -> Themes.tryFind name
                | _ -> None

            { model with
                CommandBar =
                    { model.CommandBar with
                        PreviewTheme = preview } }

    let private refreshCommandBar model =
        let completions =
            Commands.completions
                { RootPath = model.Workspace.RootPath
                  Files = workspaceFiles model.Workspace }
                model.CommandBar.Text

        let selectedIndex =
            if completions.IsEmpty then
                0
            else
                min model.CommandBar.SelectedCompletion (completions.Length - 1)

        { model with
            CommandBar =
                { model.CommandBar with
                    Parsed = Commands.parse model.CommandBar.Text
                    Completions = completions
                    SelectedCompletion = selectedIndex } }
        |> updatePreview

    let private openCommandBar initialText model =
        { model with
            Focus = CommandBar
            CommandBar =
                { model.CommandBar with
                    Active = true
                    Text = initialText
                    Cursor = initialText.Length
                    HistoryIndex = None
                    SelectedCompletion = 0 } }
        |> refreshCommandBar

    let private closeCommandBar model =
        { model with
            Focus = Editor
            CommandBar =
                { model.CommandBar with
                    Active = false
                    Text = ""
                    Cursor = 0
                    Parsed = Empty
                    Completions = []
                    SelectedCompletion = 0
                    HistoryIndex = None
                    PreviewTheme = None } }

    let private resolvePath (rootPath: string) (path: string) =
        if Path.IsPathRooted path then
            path
        else
            Path.GetFullPath(Path.Combine(rootPath, path))

    let private insertCommandText value model =
        let cursor = max 0 (min model.CommandBar.Cursor model.CommandBar.Text.Length)
        let nextText = model.CommandBar.Text.Insert(cursor, value)

        { model with
            CommandBar =
                { model.CommandBar with
                    Text = nextText
                    Cursor = cursor + value.Length
                    SelectedCompletion = 0 } }
        |> refreshCommandBar

    let private replaceCommandText value model =
        { model with
            CommandBar =
                { model.CommandBar with
                    Text = value
                    Cursor = value.Length
                    SelectedCompletion = 0 } }
        |> refreshCommandBar

    let private deleteCommandBackward model =
        if model.CommandBar.Cursor = 0 then
            model
        else
            { model with
                CommandBar =
                    { model.CommandBar with
                        Text = model.CommandBar.Text.Remove(model.CommandBar.Cursor - 1, 1)
                        Cursor = model.CommandBar.Cursor - 1
                        SelectedCompletion = 0 } }
            |> refreshCommandBar

    let private deleteCommandForward model =
        if model.CommandBar.Cursor >= model.CommandBar.Text.Length then
            model
        else
            { model with
                CommandBar =
                    { model.CommandBar with
                        Text = model.CommandBar.Text.Remove(model.CommandBar.Cursor, 1)
                        SelectedCompletion = 0 } }
            |> refreshCommandBar

    let private saveActiveBuffer customPath model =
        let buffer = activeBufferState model

        let targetPath =
            match customPath, buffer.FilePath with
            | Some path, _ -> Some(resolvePath model.Workspace.RootPath path)
            | None, Some existing -> Some existing
            | None, None -> None

        match targetPath with
        | Some path ->
            model,
            [ SaveBuffer(buffer.Id, path, Buffer.text buffer |> (fun text -> text.Replace("\n", buffer.Newline))) ]
        | None ->
            openCommandBar "writeas " model
            |> notify (Some(Notification.warning "Scratch buffers need a path.")),
            []

    let private pushHistory (text: string) model =
        let trimmed = text.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            model
        else
            { model with
                CommandBar =
                    { model.CommandBar with
                        History =
                            trimmed :: (model.CommandBar.History |> List.filter ((<>) trimmed))
                            |> List.truncate 20 } }

    let private switchBuffer offset model =
        let ids = model.Editors.Buffers |> Map.keys |> Seq.toList |> List.sort

        if ids.IsEmpty then
            model
        else
            let currentIndex =
                ids
                |> List.tryFindIndex ((=) model.Editors.ActiveBufferId)
                |> Option.defaultValue 0

            let nextIndex =
                if offset > 0 then
                    (currentIndex + 1) % ids.Length
                else
                    (currentIndex - 1 + ids.Length) % ids.Length

            { model with
                Editors =
                    { model.Editors with
                        ActiveBufferId = ids[nextIndex] } }
            |> notify (Some(Notification.info $"Buffer {nextIndex + 1}/{ids.Length}"))

    let private executeCommand command model =
        match command with
        | Open path ->
            let absolutePath = resolvePath model.Workspace.RootPath path

            match
                model.Editors.Buffers
                |> Map.toList
                |> List.tryFind (fun (_, buffer) -> buffer.FilePath = Some absolutePath)
            with
            | Some(bufferId, _) ->
                { model with
                    Editors =
                        { model.Editors with
                            ActiveBufferId = bufferId }
                    Workspace = Workspace.selectPath absolutePath model.Workspace
                    Focus = Editor }
                |> notify (Some(Notification.info $"Activated {Path.GetFileName absolutePath}")),
                []
            | None -> { model with Focus = Editor }, [ LoadFile absolutePath ]
        | Write -> saveActiveBuffer None model
        | WriteAs path -> saveActiveBuffer (Some path) model
        | Quit -> { model with ShouldQuit = true }, []
        | ToggleSidebar ->
            { model with
                Panels =
                    { model.Panels with
                        SidebarVisible = not model.Panels.SidebarVisible } },
            []
        | FocusTree -> { model with Focus = Sidebar }, []
        | FocusEditor -> { model with Focus = Editor }, []
        | ReloadWorkspace -> model, [ ScanWorkspace model.Workspace.RootPath ]
        | NextBuffer -> switchBuffer 1 model, []
        | PreviousBuffer -> switchBuffer -1 model, []
        | Help ->
            model
            |> notify (Some(Notification.info "Dock panel lists the current shortcuts and commands.")),
            []
        | Theme name ->
            match Themes.tryFind name with
            | Some theme ->
                { model with Theme = theme }
                |> notify (Some(Notification.info $"Theme: {theme.Name}")),
                [ SaveConfig theme.Name ]
            | None -> model |> notify (Some(Notification.error $"Unknown theme '{name}'.")), []

    let private runSidebar key model =
        match key with
        | Up ->
            { model with
                Workspace = Workspace.moveSelection -1 model.Workspace },
            []
        | Down ->
            { model with
                Workspace = Workspace.moveSelection 1 model.Workspace },
            []
        | PageUp ->
            { model with
                Workspace = Workspace.moveSelection -10 model.Workspace },
            []
        | PageDown ->
            { model with
                Workspace = Workspace.moveSelection 10 model.Workspace },
            []
        | Home ->
            { model with
                Workspace = Workspace.moveHome model.Workspace },
            []
        | End ->
            { model with
                Workspace = Workspace.moveEnd model.Workspace },
            []
        | Left ->
            { model with
                Workspace = Workspace.collapseSelected model.Workspace },
            []
        | Right ->
            { model with
                Workspace = Workspace.expandSelected model.Workspace },
            []
        | Enter ->
            let workspace, action = Workspace.activateSelected model.Workspace

            match action with
            | SidebarOpenFile path -> { model with Workspace = workspace }, [ LoadFile path ]
            | SidebarNoOp -> { model with Workspace = workspace }, []
        | Escape -> { model with Focus = Editor }, []
        | _ -> model, []

    let private runEditor key model =
        match key with
        | Character value -> updateActiveBuffer (Buffer.insertText (string value)) model, []
        | Enter -> updateActiveBuffer Buffer.insertNewline model, []
        | Backspace -> updateActiveBuffer Buffer.backspace model, []
        | Delete -> updateActiveBuffer Buffer.deleteForward model, []
        | Left -> updateActiveBuffer Buffer.moveLeft model, []
        | Right -> updateActiveBuffer Buffer.moveRight model, []
        | Up -> updateActiveBuffer Buffer.moveUp model, []
        | Down -> updateActiveBuffer Buffer.moveDown model, []
        | Home -> updateActiveBuffer Buffer.moveHome model, []
        | End -> updateActiveBuffer Buffer.moveEnd model, []
        | PageUp ->
            updateActiveBuffer (Buffer.movePageUp (max 1 (model.Terminal.Height - model.Panels.DockHeight - 2))) model,
            []
        | PageDown ->
            updateActiveBuffer (Buffer.movePageDown (max 1 (model.Terminal.Height - model.Panels.DockHeight - 2))) model,
            []
        | Tab -> updateActiveBuffer Buffer.indent model, []
        | ShiftTab -> updateActiveBuffer Buffer.unindent model, []
        | _ -> model, []

    let private runCommandBar key model =
        match key with
        | Escape -> closeCommandBar model, []
        | Left ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        Cursor = max 0 (model.CommandBar.Cursor - 1) } },
            []
        | Right ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        Cursor = min model.CommandBar.Text.Length (model.CommandBar.Cursor + 1) } },
            []
        | Home ->
            { model with
                CommandBar = { model.CommandBar with Cursor = 0 } },
            []
        | End ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        Cursor = model.CommandBar.Text.Length } },
            []
        | Backspace -> deleteCommandBackward model, []
        | Delete -> deleteCommandForward model, []
        | Character value -> insertCommandText (string value) model, []
        | Tab when not model.CommandBar.Completions.IsEmpty ->
            { model with
                CommandBar =
                    { model.CommandBar with
                        SelectedCompletion =
                            (model.CommandBar.SelectedCompletion + 1) % model.CommandBar.Completions.Length } }
            |> updatePreview,
            []
        | ShiftTab when not model.CommandBar.Completions.IsEmpty ->
            let count = model.CommandBar.Completions.Length

            { model with
                CommandBar =
                    { model.CommandBar with
                        SelectedCompletion = (model.CommandBar.SelectedCompletion + count - 1) % count } }
            |> updatePreview,
            []
        | Up when not model.CommandBar.History.IsEmpty ->
            let index =
                match model.CommandBar.HistoryIndex with
                | Some value -> max 0 (value - 1)
                | None -> model.CommandBar.History.Length - 1

            replaceCommandText
                model.CommandBar.History[index]
                { model with
                    CommandBar =
                        { model.CommandBar with
                            HistoryIndex = Some index } },
            []
        | Down when not model.CommandBar.History.IsEmpty ->
            let index =
                match model.CommandBar.HistoryIndex with
                | Some value -> min (model.CommandBar.History.Length - 1) (value + 1)
                | None -> 0

            replaceCommandText
                model.CommandBar.History[index]
                { model with
                    CommandBar =
                        { model.CommandBar with
                            HistoryIndex = Some index } },
            []
        | Enter ->
            match model.CommandBar.Parsed with
            | Ready command ->
                let remembered = pushHistory model.CommandBar.Text model
                let closed = closeCommandBar remembered
                executeCommand command closed
            | Pending _ when not model.CommandBar.Completions.IsEmpty ->
                match model.CommandBar.Completions |> List.tryItem model.CommandBar.SelectedCompletion with
                | Some item -> replaceCommandText item.ApplyText model, []
                | None -> model, []
            | Pending message -> notify (Some(Notification.warning message)) model, []
            | Invalid message -> notify (Some(Notification.error message)) model, []
            | Empty -> closeCommandBar model, []
        | _ -> model, []

    let init rootPath size theme =
        { Workspace = Workspace.create rootPath
          Editors = initialEditors
          CommandBar = emptyCommandBar
          Panels = initialPanels
          Focus = Editor
          Terminal = size
          Notification = Some(Notification.info "Ctrl+P commands  Ctrl+B tree  Ctrl+S save  Ctrl+Q quit")
          Theme = theme
          ShouldQuit = false },
        [ ScanWorkspace rootPath ]

    let private normalizeNewlines (text: string) =
        if text.Contains "\r\n" then
            text.Replace("\r\n", "\n"), "\r\n"
        else
            text, "\n"

    let update msg model =
        match msg with
        | Resize size -> { model with Terminal = size } |> updateActiveBuffer id, []
        | WorkspaceLoaded result ->
            match result with
            | Result.Ok tree ->
                { model with
                    Workspace = Workspace.setTree tree model.Workspace
                    Notification = Some(Notification.info $"Indexed {tree.Name}") },
                []
            | Result.Error message -> notify (Some(Notification.error message)) model, []
        | FileOpened(path, result) ->
            match result with
            | Result.Ok contents ->
                let normalized, newline = normalizeNewlines contents

                let buffer =
                    Buffer.fromText model.Editors.NextBufferId (Some path) (Path.GetFileName path) normalized newline

                { model with
                    Editors =
                        { model.Editors with
                            Buffers = model.Editors.Buffers |> Map.add buffer.Id buffer
                            ActiveBufferId = buffer.Id
                            NextBufferId = buffer.Id + 1 }
                    Workspace = Workspace.selectPath path model.Workspace
                    Focus = Editor
                    Notification = Some(Notification.info $"Opened {buffer.Name}") },
                []
            | Result.Error message -> notify (Some(Notification.error $"Failed to open {path}: {message}")) model, []
        | BufferSaved(bufferId, path, result) ->
            match result with
            | Result.Ok() ->
                { model with
                    Editors =
                        { model.Editors with
                            Buffers =
                                model.Editors.Buffers
                                |> Map.add bufferId (Buffer.markSaved path model.Editors.Buffers[bufferId]) }
                    Notification = Some(Notification.info $"Saved {Path.GetFileName path}") },
                []
            | Result.Error message -> notify (Some(Notification.error $"Failed to save {path}: {message}")) model, []
        | ConfigSaved result ->
            match result with
            | Result.Ok() -> model, []
            | Result.Error message ->
                notify (Some(Notification.warning $"Theme set, but config save failed: {message}")) model, []
        | KeyPressed key ->
            match key with
            | Ctrl 'q' ->
                { model with
                    ShouldQuit = true
                    Notification = None },
                []
            | Ctrl 'p' -> openCommandBar "" { model with Notification = None }, []
            | Ctrl 'b' ->
                { model with
                    Focus = Sidebar
                    Notification = None },
                []
            | Ctrl 'e' ->
                { model with
                    Focus = Editor
                    Notification = None },
                []
            | Ctrl 's' -> saveActiveBuffer None { model with Notification = None }
            | Ctrl 'r' -> { model with Notification = None }, [ ScanWorkspace model.Workspace.RootPath ]
            | Ctrl 'z' -> updateActiveBuffer Buffer.undo { model with Notification = None }, []
            | Ctrl 'y' -> updateActiveBuffer Buffer.redo { model with Notification = None }, []
            | _ ->
                match model.Focus with
                | Sidebar -> runSidebar key { model with Notification = None }
                | Editor -> runEditor key { model with Notification = None }
                | CommandBar -> runCommandBar key { model with Notification = None }

    let statusLine model =
        let buffer = activeBufferState model

        let focusText =
            match model.Focus with
            | Sidebar -> "TREE"
            | Editor -> "EDIT"
            | CommandBar -> "CMD"

        let dirty = if buffer.Dirty then " [+]" else ""

        let note =
            model.Notification
            |> Option.map _.Message
            |> Option.defaultValue "Ctrl+P commands"

        let pathText = buffer.FilePath |> Option.defaultValue "[scratch]"

        $"{focusText}  {pathText}{dirty}  Ln {buffer.Cursor.Line + 1}, Col {buffer.Cursor.Column + 1}  {note}"

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
            DockInfo("File Tree", Workspace.metadata model.Workspace)
        else
            let buffer = activeBufferState model

            DockInfo(
                "Editor",
                [ $"Buffer: {buffer.Name}"
                  $"Open buffers: {model.Editors.Buffers.Count}"
                  "Ctrl+B tree, Ctrl+E editor"
                  "Ctrl+P commands, Ctrl+S save"
                  "Tab indent, Shift+Tab unindent" ]
            )

type Color =
    | Default
    | Indexed of int

type Style =
    { Foreground: Color
      Background: Color
      Bold: bool
      Inverted: bool }

type Cell = { Glyph: char; Style: Style }

type Cursor = { Left: int; Top: int; Visible: bool }

type Screen =
    { Width: int
      Height: int
      Cells: Cell[,]
      Cursor: Cursor option }

type Renderer = { Writer: TextWriter }

[<RequireQualifiedAccess>]
module Style =
    let defaultStyle =
        { Foreground = Default
          Background = Default
          Bold = false
          Inverted = false }

    let withColors foreground background =
        { defaultStyle with
            Foreground = foreground
            Background = background }

[<RequireQualifiedAccess>]
module Screen =
    let private blank =
        { Glyph = ' '
          Style = Style.defaultStyle }

    let create width height =
        { Width = max 1 width
          Height = max 1 height
          Cells = Array2D.create (max 1 height) (max 1 width) blank
          Cursor = None }

    let private inBounds x y screen =
        x >= 0 && y >= 0 && x < screen.Width && y < screen.Height

    let setCell x y style glyph screen =
        if inBounds x y screen then
            screen.Cells[y, x] <- { Glyph = glyph; Style = style }

    let writeText x y style maxWidth (text: string) screen =
        if y >= 0 && y < screen.Height && maxWidth > 0 then
            text
            |> Seq.truncate maxWidth
            |> Seq.iteri (fun index glyph -> setCell (x + index) y style glyph screen)

    let fillRect x y width height style glyph screen =
        for row in y .. y + height - 1 do
            for col in x .. x + width - 1 do
                setCell col row style glyph screen

    let drawVerticalLine x y height style glyph screen =
        fillRect x y 1 height style glyph screen

    let withCursor cursor (screen: Screen) = { screen with Cursor = Some cursor }

[<RequireQualifiedAccess>]
module Renderer =
    let private sgrColor isForeground color =
        match color with
        | Default -> if isForeground then "39" else "49"
        | Indexed value -> if isForeground then $"38;5;{value}" else $"48;5;{value}"

    let private sgr style =
        let parts =
            [ "0"
              if style.Bold then
                  "1"
              if style.Inverted then
                  "7"
              sgrColor true style.Foreground
              sgrColor false style.Background ]

        let sequence = String.concat ";" parts

        $"\u001b[{sequence}m"

    let create writer = { Writer = writer }

    let enter renderer =
        renderer.Writer.Write("\u001b[?1049h\u001b[?25l\u001b[2J")

    let leave renderer =
        renderer.Writer.Write("\u001b[0m\u001b[?25h\u001b[?1049l")

    let render renderer screen =
        let builder = StringBuilder("\u001b[H")

        for row in 0 .. screen.Height - 1 do
            builder.Append($"\u001b[{row + 1};1H") |> ignore

            let mutable currentStyle: Style option = None

            for col in 0 .. screen.Width - 1 do
                let cell = screen.Cells[row, col]

                if currentStyle <> Some cell.Style then
                    builder.Append(sgr cell.Style) |> ignore
                    currentStyle <- Some cell.Style

                builder.Append(cell.Glyph) |> ignore

            builder.Append("\u001b[0m") |> ignore

        match screen.Cursor with
        | Some cursor when cursor.Visible ->
            builder.Append($"\u001b[?25h\u001b[{cursor.Top + 1};{cursor.Left + 1}H")
            |> ignore
        | _ -> builder.Append("\u001b[?25l") |> ignore

        renderer.Writer.Write(builder.ToString())
        renderer.Writer.Flush()

[<RequireQualifiedAccess>]
module Input =
    let private hasModifier modifier (keyInfo: ConsoleKeyInfo) = keyInfo.Modifiers.HasFlag modifier

    let tryMap (keyInfo: ConsoleKeyInfo) =
        if hasModifier ConsoleModifiers.Control keyInfo then
            match keyInfo.Key with
            | ConsoleKey.B -> Some(Ctrl 'b')
            | ConsoleKey.E -> Some(Ctrl 'e')
            | ConsoleKey.P -> Some(Ctrl 'p')
            | ConsoleKey.Q -> Some(Ctrl 'q')
            | ConsoleKey.R -> Some(Ctrl 'r')
            | ConsoleKey.S -> Some(Ctrl 's')
            | ConsoleKey.Y -> Some(Ctrl 'y')
            | ConsoleKey.Z -> Some(Ctrl 'z')
            | _ -> None
        else
            match keyInfo.Key with
            | ConsoleKey.Enter -> Some Enter
            | ConsoleKey.Escape -> Some Escape
            | ConsoleKey.Backspace -> Some Backspace
            | ConsoleKey.Delete -> Some Delete
            | ConsoleKey.Tab when hasModifier ConsoleModifiers.Shift keyInfo -> Some ShiftTab
            | ConsoleKey.Tab -> Some Tab
            | ConsoleKey.LeftArrow -> Some Left
            | ConsoleKey.RightArrow -> Some Right
            | ConsoleKey.UpArrow -> Some Up
            | ConsoleKey.DownArrow -> Some Down
            | ConsoleKey.Home -> Some Home
            | ConsoleKey.End -> Some End
            | ConsoleKey.PageUp -> Some PageUp
            | ConsoleKey.PageDown -> Some PageDown
            | _ when Char.IsControl keyInfo.KeyChar -> None
            | _ -> Some(Character keyInfo.KeyChar)

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
        Screen.writeText 0 statusY status width (pad width (Editor.statusLine model)) current
        Screen.fillRect 0 dockY width dockHeight chrome ' ' current

        match Editor.dockPanel model with
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
            if model.CommandBar.Active then
                ":" + model.CommandBar.Text
            else
                " Ctrl+P commands  Ctrl+B tree  Ctrl+S save  Ctrl+Q quit "

        Screen.writeText 0 commandY commandBar width (pad width lineText) current

        if model.CommandBar.Active then
            current <-
                Screen.withCursor
                    { Left = min (width - 1) (1 + model.CommandBar.Cursor)
                      Top = commandY
                      Visible = true }
                    current

        current

[<RequireQualifiedAccess>]
module Runtime =
    let private utf8WithoutBom = UTF8Encoding false

    let private configDirectory () =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "fedit")

    let private configPath () =
        Path.Combine(configDirectory (), "config.json")

    let private loadConfig () =
        try
            let path = configPath ()

            if File.Exists path then
                let json = File.ReadAllText path
                use doc = System.Text.Json.JsonDocument.Parse json

                match doc.RootElement.TryGetProperty "theme" with
                | true, elem when elem.ValueKind = System.Text.Json.JsonValueKind.String ->
                    Themes.tryFind (elem.GetString())
                | _ -> None
            else
                None
        with _ ->
            None

    let private saveConfig (themeName: string) =
        let directory = configDirectory ()
        Directory.CreateDirectory directory |> ignore
        let json = $"{{\n  \"theme\": \"{themeName}\"\n}}\n"
        File.WriteAllText(configPath (), json, utf8WithoutBom)

    let private makeNode (path: string) isDirectory children : FileNode =
        let rawName = Path.GetFileName path

        { Path = path
          Name = if String.IsNullOrWhiteSpace rawName then path else rawName
          IsDirectory = isDirectory
          Children = children }

    let private shouldSkip (path: string) =
        Workspace.excludedNames.Contains(Path.GetFileName path)

    let rec private scanNode (path: string) =
        let attributes = File.GetAttributes path
        let isDirectory = attributes.HasFlag FileAttributes.Directory

        if isDirectory then
            if attributes.HasFlag FileAttributes.ReparsePoint then
                makeNode path true []
            else
                let children =
                    seq {
                        for childDir in Directory.EnumerateDirectories path do
                            if not (shouldSkip childDir) then
                                try
                                    yield scanNode childDir
                                with _ ->
                                    ()

                        for childFile in Directory.EnumerateFiles path do
                            if not (shouldSkip childFile) then
                                yield makeNode childFile false []
                    }
                    |> Seq.toList

                makeNode path true children
        else
            makeNode path false []

    let private runEffect effect =
        match effect with
        | ScanWorkspace rootPath ->
            try
                WorkspaceLoaded(Result.Ok(scanNode rootPath))
            with ex ->
                WorkspaceLoaded(Result.Error ex.Message)
        | LoadFile path ->
            try
                FileOpened(path, Result.Ok(File.ReadAllText path))
            with ex ->
                FileOpened(path, Result.Error ex.Message)
        | SaveBuffer(bufferId, path, contents) ->
            try
                let directory = Path.GetDirectoryName path

                if not (String.IsNullOrWhiteSpace directory) then
                    Directory.CreateDirectory directory |> ignore

                File.WriteAllText(path, contents, utf8WithoutBom)
                BufferSaved(bufferId, path, Result.Ok())
            with ex ->
                BufferSaved(bufferId, path, Result.Error ex.Message)
        | SaveConfig themeName ->
            try
                saveConfig themeName
                ConfigSaved(Result.Ok())
            with ex ->
                ConfigSaved(Result.Error ex.Message)

    let rec private dispatch model msg =
        let nextModel, effects = Editor.update msg model

        effects
        |> List.fold (fun state effect -> dispatch state (runEffect effect)) nextModel

    let private consoleSize () =
        { Width = max 1 Console.WindowWidth
          Height = max 1 Console.WindowHeight }

    let run rootPath =
        Console.OutputEncoding <- Encoding.UTF8
        Console.TreatControlCAsInput <- true

        let theme = loadConfig () |> Option.defaultValue Themes.defaultTheme
        let initialModel, startupEffects = Editor.init rootPath (consoleSize ()) theme

        let mutable model =
            startupEffects
            |> List.fold (fun state effect -> dispatch state (runEffect effect)) initialModel

        let mutable needsRender = true
        let renderer = Renderer.create Console.Out

        try
            Renderer.enter renderer

            while not model.ShouldQuit do
                let size = consoleSize ()

                if size <> model.Terminal then
                    model <- dispatch model (Resize size)
                    needsRender <- true

                if needsRender then
                    model |> Layout.render |> Renderer.render renderer
                    needsRender <- false

                if Console.KeyAvailable then
                    match Console.ReadKey true |> Input.tryMap with
                    | Some key ->
                        model <- dispatch model (KeyPressed key)
                        needsRender <- true
                    | None -> ()
                else
                    Thread.Sleep 16
        finally
            Renderer.leave renderer

[<EntryPoint>]
let main argv =
    let rootPath =
        match argv |> Array.tryHead with
        | Some path -> Path.GetFullPath path
        | None -> Directory.GetCurrentDirectory()

    Runtime.run rootPath
    0
