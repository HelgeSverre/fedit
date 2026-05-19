namespace Fedit

open System
open System.IO


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
      Lines: string[]
      Cursor: Position
      PreferredColumn: int option
      Selection: int option
      ViewportTop: int
      ViewportLeft: int
      Dirty: bool
      Newline: string
      Undo: BufferRevision list
      Redo: BufferRevision list }

[<RequireQualifiedAccess>]
module Buffer =
    let private tabText = "    "

    let private computeLines (document: PieceTable) =
        let contents = PieceTable.toString document

        if String.IsNullOrEmpty contents then
            [| "" |]
        else
            contents.Split('\n')

    let private snapshot buffer =
        { Document = buffer.Document
          Cursor = buffer.Cursor
          PreferredColumn = buffer.PreferredColumn
          Dirty = buffer.Dirty }

    let private maxUndoDepth = 200

    let private pushUndo buffer =
        { buffer with
            Undo = snapshot buffer :: buffer.Undo |> List.truncate maxUndoDepth
            Redo = [] }

    let createEmpty id =
        { Id = id
          FilePath = None
          Name = "scratch"
          Document = PieceTable.empty
          Lines = [| "" |]
          Cursor = Position.zero
          PreferredColumn = None
          Selection = None
          ViewportTop = 0
          ViewportLeft = 0
          Dirty = false
          Newline = "\n"
          Undo = []
          Redo = [] }

    let fromText id filePath name text newline =
        let document = PieceTable.ofString text

        { Id = id
          FilePath = filePath
          Name = name
          Document = document
          Lines = computeLines document
          Cursor = Position.zero
          PreferredColumn = None
          Selection = None
          ViewportTop = 0
          ViewportLeft = 0
          Dirty = false
          Newline = newline
          Undo = []
          Redo = [] }

    let text buffer = PieceTable.toString buffer.Document

    let serialize (buffer: BufferState) =
        (text buffer).Replace("\n", buffer.Newline)

    let private rawLines (buffer: BufferState) = buffer.Lines

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

    let moveToOffset index buffer = moveToIndex index buffer

    let private withDocument document (buffer: BufferState) =
        { buffer with
            Document = document
            Lines = computeLines document }

    let private changeDocument newDocument newCursor (buffer: BufferState) =
        { (pushUndo buffer) with
            Document = newDocument
            Lines = computeLines newDocument
            Cursor = newCursor
            PreferredColumn = None
            Dirty = true }

    let private replaceRange startIndex count replacement buffer =
        let deleted = PieceTable.deleteRange startIndex count buffer.Document
        let inserted = PieceTable.insert startIndex replacement deleted

        let nextCursor =
            indexToPosition (startIndex + replacement.Length) (buffer |> withDocument inserted)

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
            let nextCursor = indexToPosition (index - 1) (buffer |> withDocument nextDocument)
            changeDocument nextDocument nextCursor buffer

    let deleteForward buffer =
        let index = positionToIndex buffer.Cursor buffer

        if index >= PieceTable.length buffer.Document then
            buffer
        else
            let nextDocument = PieceTable.deleteRange index 1 buffer.Document
            let nextCursor = indexToPosition index (buffer |> withDocument nextDocument)
            changeDocument nextDocument nextCursor buffer

    let private isWordChar (c: char) =
        System.Char.IsLetterOrDigit c || c = '_'

    let private wordIndexLeft (txt: string) startIdx =
        let mutable i = startIdx

        while i > 0 && not (isWordChar txt[i - 1]) do
            i <- i - 1

        while i > 0 && isWordChar txt[i - 1] do
            i <- i - 1

        i

    let private wordIndexRight (txt: string) startIdx =
        let mutable i = startIdx
        let len = txt.Length

        while i < len && not (isWordChar txt[i]) do
            i <- i + 1

        while i < len && isWordChar txt[i] do
            i <- i + 1

        i

    let moveLeftWord buffer =
        let txt = text buffer
        let target = wordIndexLeft txt (positionToIndex buffer.Cursor buffer)
        moveToIndex target buffer

    let moveRightWord buffer =
        let txt = text buffer
        let target = wordIndexRight txt (positionToIndex buffer.Cursor buffer)
        moveToIndex target buffer

    let backspaceWord buffer =
        let startIdx = positionToIndex buffer.Cursor buffer

        if startIdx = 0 then
            buffer
        else
            let target = wordIndexLeft (text buffer) startIdx
            let count = startIdx - target
            let nextDocument = PieceTable.deleteRange target count buffer.Document
            let nextCursor = indexToPosition target (buffer |> withDocument nextDocument)
            changeDocument nextDocument nextCursor buffer

    let setSelection anchor (buffer: BufferState) = { buffer with Selection = Some anchor }

    let clearSelection (buffer: BufferState) = { buffer with Selection = None }

    let extendSelectionToCursor (buffer: BufferState) =
        match buffer.Selection with
        | Some _ -> buffer
        | None ->
            let anchor = positionToIndex buffer.Cursor buffer
            { buffer with Selection = Some anchor }

    let selectionRange (buffer: BufferState) =
        buffer.Selection
        |> Option.map (fun anchor ->
            let cur = positionToIndex buffer.Cursor buffer
            if anchor <= cur then anchor, cur else cur, anchor)

    let selectionText (buffer: BufferState) =
        match selectionRange buffer with
        | Some(s, e) when e > s ->
            let txt = text buffer
            txt.Substring(s, e - s)
        | _ -> ""

    let selectAll (buffer: BufferState) =
        let len = PieceTable.length buffer.Document

        { buffer with
            Selection = Some 0
            Cursor = indexToPosition len buffer
            PreferredColumn = None }

    let deleteSelection (buffer: BufferState) =
        match selectionRange buffer with
        | Some(s, e) when e > s ->
            let count = e - s
            let nextDocument = PieceTable.deleteRange s count buffer.Document
            let nextCursor = indexToPosition s (buffer |> withDocument nextDocument)

            { (changeDocument nextDocument nextCursor buffer) with
                Selection = None }
        | _ -> buffer

    let findAll (needle: string) buffer =
        if String.IsNullOrEmpty needle then
            []
        else
            let haystack = text buffer
            let mutable matches = []
            let mutable index = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase)

            while index >= 0 do
                matches <- index :: matches
                index <- haystack.IndexOf(needle, index + 1, StringComparison.OrdinalIgnoreCase)

            List.rev matches

    let deleteForwardWord buffer =
        let txt = text buffer
        let len = txt.Length
        let startIdx = positionToIndex buffer.Cursor buffer

        if startIdx >= len then
            buffer
        else
            let target = wordIndexRight txt startIdx
            let count = target - startIdx
            let nextDocument = PieceTable.deleteRange startIdx count buffer.Document
            let nextCursor = indexToPosition startIdx (buffer |> withDocument nextDocument)
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

    let markSaved (filePath: string) (buffer: BufferState) =
        let name = Path.GetFileName filePath |> Option.ofObj |> Option.defaultValue filePath

        { buffer with
            FilePath = Some filePath
            Name = name
            Dirty = false
            Undo = []
            Redo = [] }

    let undo buffer =
        match buffer.Undo with
        | previous :: rest ->
            let current = snapshot buffer

            { buffer with
                Document = previous.Document
                Lines = computeLines previous.Document
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
                Lines = computeLines next.Document
                Cursor = next.Cursor
                PreferredColumn = next.PreferredColumn
                Dirty = next.Dirty
                Undo = current :: buffer.Undo
                Redo = rest }
        | [] -> buffer
