namespace Fedit

open System
open System.IO


/// Where forward word-motion (Alt+→ / `moveRightWord`) lands.
type WordMotionLanding =
    /// Stop at the end of the current run (matches Zed, VSCode, JetBrains).
    | WordEnd
    /// Skip trailing whitespace too, landing at the start of the next non-whitespace
    /// run (matches vim's `w` motion).
    | NextWordStart

type BufferRevision =
    { Document: PieceTable
      Cursor: Position
      PreferredColumn: int option
      Dirty: bool }

type BufferState =
    {
        /// Unique identifier for the buffer
        Id: int
        /// Optional path to the file this buffer represents
        FilePath: string option
        /// Display name for the buffer (filename or "scratch")
        Name: string
        /// The underlying PieceTable data structure storing text content
        Document: PieceTable
        /// Cached array of lines (split by newlines) for efficient access
        Lines: string[]
        /// Current cursor position (line and column)
        Cursor: Position
        /// Preferred column for vertical movement (maintains horizontal position when moving up/down)
        PreferredColumn: int option
        /// Optional anchor position for text selection
        Selection: int option
        /// Top line of the visible viewport
        ViewportTop: int
        /// Left column of the visible viewport
        ViewportLeft: int
        /// Flag indicating if the buffer has unsaved changes
        Dirty: bool
        /// The newline character(s) to use ("\n", "\r\n", etc.)
        Newline: string
        /// List of previous buffer states for undo functionality
        Undo: BufferRevision list
        /// List of redo states for redo functionality
        Redo: BufferRevision list
    }

[<RequireQualifiedAccess>]
module Buffer =
    let private spaces (n: int) = String.replicate (max 0 n) " "

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

    /// Finalize an edit. Caller passes the pre-edit `original` (for undo),
    /// the already-updated `withDoc` buffer (Lines computed once via
    /// `withDocument`), and the new cursor position. Sets cursor, marks
    /// dirty, pushes undo, clears redo. No second `computeLines`.
    let private finalizeEdit (original: BufferState) (newCursor: Position) (withDoc: BufferState) =
        { withDoc with
            Cursor = newCursor
            PreferredColumn = None
            Dirty = true
            Undo = snapshot original :: original.Undo |> List.truncate maxUndoDepth
            Redo = [] }

    let private replaceRange startIndex count replacement buffer =
        let deleted = PieceTable.deleteRange startIndex count buffer.Document
        let inserted = PieceTable.insert startIndex replacement deleted
        let withDoc = buffer |> withDocument inserted
        let nextCursor = indexToPosition (startIndex + replacement.Length) withDoc
        finalizeEdit buffer nextCursor withDoc

    let insertText value buffer =
        replaceRange (positionToIndex buffer.Cursor buffer) 0 value buffer

    let insertNewline buffer = insertText "\n" buffer

    let backspace buffer =
        let index = positionToIndex buffer.Cursor buffer

        if index = 0 then
            buffer
        else
            let nextDocument = PieceTable.deleteRange (index - 1) 1 buffer.Document
            let withDoc = buffer |> withDocument nextDocument
            let nextCursor = indexToPosition (index - 1) withDoc
            finalizeEdit buffer nextCursor withDoc

    let deleteForward buffer =
        let index = positionToIndex buffer.Cursor buffer

        if index >= PieceTable.length buffer.Document then
            buffer
        else
            let nextDocument = PieceTable.deleteRange index 1 buffer.Document
            let withDoc = buffer |> withDocument nextDocument
            let nextCursor = indexToPosition index withDoc
            finalizeEdit buffer nextCursor withDoc

    // Word-motion classifier — three classes that motion stops between.
    // Pinned to ASCII for predictability (matches IntelliJ / token-editor /
    // Zed's `CharClassifier`). `Other` is a defensive fourth class for
    // non-ASCII characters that aren't IsLetterOrDigit (e.g. § ¶ emoji);
    // they form runs of their own and don't merge with WordChar.

    type private CharClass =
        | Whitespace
        | WordChar
        | Punctuation
        | Other

    let private punctuationChars =
        Set.ofArray
            [| '.'
               ','
               ':'
               ';'
               '!'
               '?'
               '('
               ')'
               '{'
               '}'
               '['
               ']'
               '<'
               '>'
               '='
               '+'
               '-'
               '*'
               '/'
               '%'
               '&'
               '|'
               '^'
               '~'
               '@'
               '#'
               '$'
               '\\'
               '"'
               '\''
               '`' |]

    let private classify (c: char) : CharClass =
        if Char.IsWhiteSpace c then Whitespace
        elif Char.IsLetterOrDigit c || c = '_' then WordChar
        elif Set.contains c punctuationChars then Punctuation
        else Other

    // Backward motion: scan past whitespace, then collapse one homogeneous
    // class run. Symmetric for all landing modes.
    let private wordIndexLeft (txt: string) startIdx =
        let mutable i = startIdx

        while i > 0 && classify txt[i - 1] = Whitespace do
            i <- i - 1

        if i > 0 then
            let target = classify txt[i - 1]

            while i > 0 && classify txt[i - 1] = target do
                i <- i - 1

        i

    // Forward motion:
    //   Phase 1 — skip leading whitespace (puts us at the start of a run).
    //   Phase 2 — consume the run we're sitting on.
    //   Phase 3 — if NextWordStart, eat trailing whitespace too (vim 'w').
    let private wordIndexRight (landing: WordMotionLanding) (txt: string) startIdx =
        let len = txt.Length
        let mutable i = startIdx

        while i < len && classify txt[i] = Whitespace do
            i <- i + 1

        if i < len then
            let current = classify txt[i]

            while i < len && classify txt[i] = current do
                i <- i + 1

        match landing with
        | NextWordStart ->
            while i < len && classify txt[i] = Whitespace do
                i <- i + 1
        | WordEnd -> ()

        i

    let moveLeftWord buffer =
        let txt = text buffer
        let target = wordIndexLeft txt (positionToIndex buffer.Cursor buffer)
        moveToIndex target buffer

    let moveRightWord (landing: WordMotionLanding) buffer =
        let txt = text buffer
        let target = wordIndexRight landing txt (positionToIndex buffer.Cursor buffer)
        moveToIndex target buffer

    let backspaceWord buffer =
        let startIdx = positionToIndex buffer.Cursor buffer

        if startIdx = 0 then
            buffer
        else
            let target = wordIndexLeft (text buffer) startIdx
            let count = startIdx - target
            let nextDocument = PieceTable.deleteRange target count buffer.Document
            let withDoc = buffer |> withDocument nextDocument
            let nextCursor = indexToPosition target withDoc
            finalizeEdit buffer nextCursor withDoc

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
            let withDoc = buffer |> withDocument nextDocument
            let nextCursor = indexToPosition s withDoc

            { finalizeEdit buffer nextCursor withDoc with
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

    let deleteForwardWord (landing: WordMotionLanding) buffer =
        let txt = text buffer
        let len = txt.Length
        let startIdx = positionToIndex buffer.Cursor buffer

        if startIdx >= len then
            buffer
        else
            let target = wordIndexRight landing txt startIdx
            let count = target - startIdx
            let nextDocument = PieceTable.deleteRange startIdx count buffer.Document
            let withDoc = buffer |> withDocument nextDocument
            let nextCursor = indexToPosition startIdx withDoc
            finalizeEdit buffer nextCursor withDoc

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

    let indent (tabWidth: int) buffer = insertText (spaces tabWidth) buffer

    let unindent (tabWidth: int) buffer =
        let currentLine = line buffer.Cursor.Line buffer

        let removable =
            currentLine |> Seq.takeWhile ((=) ' ') |> Seq.length |> min (max 0 tabWidth)

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

            let withDoc = buffer |> withDocument nextDocument
            finalizeEdit buffer nextCursor withDoc

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
