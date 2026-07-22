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

/// An explicit selection span in char-index space. `Anchor` is the fixed
/// end; `Head` is the live end. Selection-producing operations keep
/// `Head` equal to the cursor; pure-view movements (viewport scrolling)
/// may move the cursor without touching the span.
[<Struct>]
type SelectionSpan = { Anchor: int; Head: int }

type BufferRevision =
    {
        Document: PieceTable
        Cursor: Position
        PreferredColumn: int option
        /// The `EditTick` the buffer carried when this snapshot was current.
        /// Restoring a revision derives `Dirty` by comparing this against
        /// `SavedTick`, so undoing back to the last-saved revision shows clean.
        Tick: int
    }

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
        /// The active selection span, if any. Lives in char-index space and
        /// is independent of the cursor — `selectionRange` reads only this.
        Selection: SelectionSpan option
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
        /// Monotonically-increasing revision marker. Bumped on every mutating
        /// edit. Used by `SaveBuffer` to detect concurrent edits and avoid
        /// marking the buffer clean for changes the writer didn't capture.
        EditTick: int
        /// The `EditTick` whose contents were last written to disk (0 =
        /// never saved; the initial contents). `Dirty` is maintained as
        /// "current revision differs from `SavedTick`" so undo history can
        /// survive saves.
        SavedTick: int
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
          Tick = buffer.EditTick }

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
          Redo = []
          EditTick = 0
          SavedTick = 0 }

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
          Redo = []
          EditTick = 0
          SavedTick = 0 }

    let text buffer = PieceTable.toString buffer.Document

    let serialize (buffer: BufferState) =
        (text buffer).Replace("\n", buffer.Newline)

    /// The cached per-line array. Callers must treat it as read-only — it is
    /// shared with the buffer, not a copy (the renderer reads it every frame).
    let lines (buffer: BufferState) : string[] = buffer.Lines

    let line lineIndex buffer =
        lines buffer |> Array.tryItem lineIndex |> Option.defaultValue ""

    let lineCount buffer = lines buffer |> Array.length

    let gutterWidth buffer =
        let digits = max 3 (string (lineCount buffer)).Length
        digits + 2

    let private clamp position buffer =
        let rows = lines buffer
        let lineIndex = max 0 (min position.Line (rows.Length - 1))

        { Line = lineIndex
          Column = max 0 (min position.Column rows[lineIndex].Length) }

    let positionToIndex position buffer =
        let rows = lines buffer
        let safe = clamp position buffer

        (rows |> Seq.take safe.Line |> Seq.sumBy (fun lineText -> lineText.Length + 1))
        + safe.Column

    let indexToPosition index buffer =
        let bounded = max 0 (min index (PieceTable.length buffer.Document))
        let rows = lines buffer

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

    /// Incrementally update the cached line array for an edit that, in the
    /// PRE-edit document, replaced `removedLen` chars at `offset` with
    /// `inserted`. Clamping mirrors `PieceTable.deleteRange`/`insert`: the
    /// start clamps into [0, length]; the removed end clamps to the document
    /// end. Only the lines overlapping the edit are re-split; every other
    /// line is shared (same string references) with the previous array.
    let private spliceLines (offset: int) (removedLen: int) (inserted: string) (lines: string[]) : string[] =
        // Defensive: a runtime null can still arrive across the plugin
        // boundary despite the non-null type (FS3261 bars a plain isNull).
        let inserted = if String.IsNullOrEmpty inserted then "" else inserted
        let lastLine = lines.Length - 1

        // Line i spans document offsets [start_i, start_i + length_i]; the
        // upper bound is its '\n' (or EOF) slot, so containment is
        // unambiguous — line i+1 starts at start_i + length_i + 1. Running
        // past the last line clamps to it.
        let rec locate lineIdx lineStart (target: int) =
            let lineEnd = lineStart + lines[lineIdx].Length

            if target <= lineEnd || lineIdx = lastLine then
                lineIdx, lineStart
            else
                locate (lineIdx + 1) (lineEnd + 1) target

        let startOff = max 0 offset
        let startLine, startLineOff = locate 0 0 startOff
        let startCol = min (startOff - startLineOff) lines[startLine].Length
        let endTarget = startLineOff + startCol + max 0 removedLen
        let endLine, endLineOff = locate startLine startLineOff endTarget
        let endCol = min (endTarget - endLineOff) lines[endLine].Length

        let region =
            String.Concat(lines[startLine].Substring(0, startCol), inserted, lines[endLine].Substring(endCol))

        let middle = region.Split('\n')
        let result = Array.zeroCreate (startLine + middle.Length + (lastLine - endLine))
        Array.blit lines 0 result 0 startLine
        Array.blit middle 0 result startLine middle.Length
        Array.blit lines (endLine + 1) result (startLine + middle.Length) (lastLine - endLine)
        result

    /// Apply an edited document plus the edit's shape so the line cache is
    /// spliced instead of rebuilt from a full `toString`.
    let private withEdit (offset: int) (removedLen: int) (inserted: string) document (buffer: BufferState) =
        { buffer with
            Document = document
            Lines = spliceLines offset removedLen inserted buffer.Lines }

    /// Finalize an edit. Caller passes the pre-edit `original` (for undo),
    /// the already-updated `withDoc` buffer (Lines spliced once via
    /// `withEdit`), and the new cursor position. Sets cursor, marks
    /// dirty, pushes undo, clears redo, bumps EditTick. No second
    /// line-cache pass.
    let private finalizeEdit (original: BufferState) (newCursor: Position) (withDoc: BufferState) =
        { withDoc with
            Cursor = newCursor
            PreferredColumn = None
            Dirty = true
            EditTick = original.EditTick + 1
            Undo = snapshot original :: original.Undo |> List.truncate maxUndoDepth
            Redo = [] }

    /// Replace `count` chars starting at `startIndex` with `replacement`
    /// as a single edit: one undo entry, cursor just past the inserted
    /// text. Backs `insertText` and the plugin `ReplaceRange` action.
    let replaceRange startIndex count replacement buffer =
        let deleted = PieceTable.deleteRange startIndex count buffer.Document
        let inserted = PieceTable.insert startIndex replacement deleted
        // `inserted` is the post-edit DOCUMENT; `replacement` is the text.
        let withDoc = buffer |> withEdit startIndex count replacement inserted
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
            let withDoc = buffer |> withEdit (index - 1) 1 "" nextDocument
            let nextCursor = indexToPosition (index - 1) withDoc
            finalizeEdit buffer nextCursor withDoc

    let deleteForward buffer =
        let index = positionToIndex buffer.Cursor buffer

        if index >= PieceTable.length buffer.Document then
            buffer
        else
            let nextDocument = PieceTable.deleteRange index 1 buffer.Document
            let withDoc = buffer |> withEdit index 1 "" nextDocument
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
            let withDoc = buffer |> withEdit target count "" nextDocument
            let nextCursor = indexToPosition target withDoc
            finalizeEdit buffer nextCursor withDoc

    /// Establish a selection with the cursor at `head`. The one constructor
    /// for selections — mouse, select-all, and plugin SelectRange all route
    /// through here, so a future multi-caret list changes one function.
    let selectRange anchor head (buffer: BufferState) =
        { moveToIndex head buffer with
            Selection = Some { Anchor = anchor; Head = head } }

    let clearSelection (buffer: BufferState) = { buffer with Selection = None }

    let hasSelection (buffer: BufferState) = buffer.Selection.IsSome

    /// Double-click: select the word under `idx`, using the same
    /// word-boundary scans as the word motions so click- and key-selection
    /// can never disagree on what a word is. Sitting on whitespace lands on
    /// the next word, mirroring `wordIndexRight`.
    let selectWordAt (idx: int) (buffer: BufferState) =
        let txt = text buffer
        let finish = wordIndexRight WordEnd txt idx
        let start = wordIndexLeft txt finish

        if finish > start then
            selectRange start finish buffer
        else
            buffer

    /// Triple-click: select the whole of line `lineIndex`, including its
    /// trailing newline so consecutive line selections tile cleanly. The
    /// last line has no newline to include.
    let selectLineAt (lineIndex: int) (buffer: BufferState) =
        let lineIndex = max 0 (min lineIndex (lineCount buffer - 1))
        let start = positionToIndex { Line = lineIndex; Column = 0 } buffer
        let finish = start + (line lineIndex buffer).Length + 1
        selectRange start (min finish (PieceTable.length buffer.Document)) buffer

    /// Shift+motion: pin the anchor (when no selection), run the motion,
    /// then sync the span's Head to the new cursor. If a detached scroll
    /// moved the cursor away from Head, snap it back first so extension
    /// continues from the visible selection end, not the drifted cursor.
    let extendWith (motion: BufferState -> BufferState) (buffer: BufferState) =
        let basis =
            match buffer.Selection with
            | Some span when positionToIndex buffer.Cursor buffer <> span.Head -> moveToIndex span.Head buffer
            | _ -> buffer

        let anchor =
            match basis.Selection with
            | Some span -> span.Anchor
            | None -> positionToIndex basis.Cursor basis

        let moved = motion basis

        { moved with
            Selection =
                Some
                    { Anchor = anchor
                      Head = positionToIndex moved.Cursor moved } }

    let selectionRange (buffer: BufferState) =
        buffer.Selection
        |> Option.map (fun span ->
            let len = PieceTable.length buffer.Document
            let clampIndex index = index |> max 0 |> min len
            let a = clampIndex span.Anchor
            let h = clampIndex span.Head

            if a <= h then a, h else h, a)

    let selectionText (buffer: BufferState) =
        match selectionRange buffer with
        | Some(s, e) when e > s ->
            let txt = text buffer
            txt.Substring(s, e - s)
        | _ -> ""

    let selectAll (buffer: BufferState) =
        selectRange 0 (PieceTable.length buffer.Document) buffer

    let deleteSelection (buffer: BufferState) =
        match selectionRange buffer with
        | Some(s, e) when e > s ->
            let count = e - s
            let nextDocument = PieceTable.deleteRange s count buffer.Document
            let withDoc = buffer |> withEdit s count "" nextDocument
            let nextCursor = indexToPosition s withDoc

            { finalizeEdit buffer nextCursor withDoc with
                Selection = None }
        | _ -> buffer

    /// Move the current line, or every line touched by the selection, as one
    /// edit. A selection ending at column zero does not include that trailing
    /// line: only the preceding newline belongs to the selection. Movement
    /// clamps at document boundaries and preserves selection direction.
    let private moveLines upward count (buffer: BufferState) =
        if count <= 0 then
            buffer
        else
            let blockStart, blockEnd =
                match selectionRange buffer with
                | Some(startIndex, endIndex) ->
                    let startPosition = indexToPosition startIndex buffer
                    let endPosition = indexToPosition endIndex buffer

                    let endLine =
                        if endIndex > startIndex && endPosition.Column = 0 then
                            max startPosition.Line (endPosition.Line - 1)
                        else
                            endPosition.Line

                    startPosition.Line, endLine
                | None -> buffer.Cursor.Line, buffer.Cursor.Line

            let available =
                if upward then
                    blockStart
                else
                    lineCount buffer - 1 - blockEnd

            let distance = min count available

            if distance = 0 then
                buffer
            else
                let lineDelta = if upward then -distance else distance
                let affectedStart = if upward then blockStart - distance else blockStart
                let affectedEnd = if upward then blockEnd else blockEnd + distance
                let rows = lines buffer
                let block = rows[blockStart..blockEnd]

                let reordered =
                    if upward then
                        Array.append block rows[affectedStart .. blockStart - 1]
                    else
                        Array.append rows[blockEnd + 1 .. affectedEnd] block

                let regionStart = positionToIndex { Line = affectedStart; Column = 0 } buffer

                let regionEnd =
                    if affectedEnd < rows.Length - 1 then
                        positionToIndex { Line = affectedEnd + 1; Column = 0 } buffer
                    else
                        PieceTable.length buffer.Document

                let replacement =
                    String.concat "\n" reordered
                    + if affectedEnd < rows.Length - 1 then "\n" else ""

                let edited = replaceRange regionStart (regionEnd - regionStart) replacement buffer

                let translateIndex index =
                    let position = indexToPosition index buffer

                    positionToIndex
                        { position with
                            Line = position.Line + lineDelta }
                        edited

                match buffer.Selection with
                | Some span ->
                    let anchor = translateIndex span.Anchor
                    let head = translateIndex span.Head

                    { edited with
                        Cursor = indexToPosition head edited
                        Selection = Some { Anchor = anchor; Head = head } }
                | None ->
                    let cursor =
                        positionToIndex
                            { buffer.Cursor with
                                Line = buffer.Cursor.Line + lineDelta }
                            edited
                        |> fun index -> indexToPosition index edited

                    { edited with
                        Cursor = cursor
                        Selection = None }

    let moveLinesUp count buffer = moveLines true count buffer

    let moveLinesDown count buffer = moveLines false count buffer

    /// Every match offset of `needle` in `haystack`, in document order.
    /// Literal, case-insensitive ordinal match — the single definition of
    /// fedit's search semantics, shared by the prompt's `RunSearch` effect
    /// and the `search-next` / `search-previous` repeat actions. Takes the
    /// text (not a buffer) so both callers use the same pure core.
    let findAllMatches (needle: string) (haystack: string) : int list =
        if String.IsNullOrEmpty needle || String.IsNullOrEmpty haystack then
            []
        else
            let mutable matches = []
            let mutable index = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase)

            while index >= 0 do
                matches <- index :: matches
                index <- haystack.IndexOf(needle, index + 1, StringComparison.OrdinalIgnoreCase)

            List.rev matches

    let findAll (needle: string) buffer = findAllMatches needle (text buffer)

    /// Offset of the first match at or after `fromIndex`, wrapping to the
    /// start of the text when nothing follows — the same cyclic semantics
    /// as the search prompt's Up/Down match cycling. `None` when the needle
    /// is empty or absent entirely.
    let findNextMatch (needle: string) (fromIndex: int) (haystack: string) : int option =
        match findAllMatches needle haystack with
        | [] -> None
        | matches ->
            matches
            |> List.tryFind (fun offset -> offset >= fromIndex)
            |> Option.orElse (Some(List.head matches))

    /// Offset of the last match at or before `fromIndex`, wrapping to the
    /// end of the text when nothing precedes. Mirror of `findNextMatch`.
    let findPreviousMatch (needle: string) (fromIndex: int) (haystack: string) : int option =
        match findAllMatches needle haystack with
        | [] -> None
        | matches ->
            matches
            |> List.filter (fun offset -> offset <= fromIndex)
            |> List.tryLast
            |> Option.orElse (List.tryLast matches)

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
            let withDoc = buffer |> withEdit startIdx count "" nextDocument
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

            let withDoc = buffer |> withEdit lineStart removable "" nextDocument
            finalizeEdit buffer nextCursor withDoc

    /// Slide the viewport just enough to keep `cursor` at least `margin`
    /// lines/columns inside `[viewport, viewport + span)` (vim/helix
    /// `scrolloff`). Returns the current `viewport` unchanged if the cursor
    /// is already within the margin band; otherwise pulls the window along
    /// the cursor by exactly the minimum. `margin` is capped at half the
    /// span so it can never invert the band; the caller's `clamp 0 maxTop`
    /// relaxes it at the document edges.
    let private slideViewport margin cursor viewport span =
        let m = min (max 0 margin) ((span - 1) / 2)

        if cursor < viewport + m then
            cursor - m
        elif cursor >= viewport + span - m then
            cursor - span + 1 + m
        else
            viewport

    let ensureViewport scrolloff viewportHeight viewportWidth buffer =
        let safeHeight = max 1 viewportHeight
        let safeWidth = max 1 viewportWidth
        let maxTop = max 0 (lineCount buffer - safeHeight)
        let maxLeft = max 0 ((line buffer.Cursor.Line buffer).Length - safeWidth)

        let clamp lo hi value = value |> max lo |> min hi

        // Vertical motion honours `scrolloff`; horizontal has no
        // `sidescrolloff` concept, so its margin is 0.
        { buffer with
            ViewportTop =
                slideViewport scrolloff buffer.Cursor.Line buffer.ViewportTop safeHeight
                |> clamp 0 maxTop
            ViewportLeft =
                slideViewport 0 buffer.Cursor.Column buffer.ViewportLeft safeWidth
                |> clamp 0 maxLeft }

    /// Viewport-led scroll (mouse wheel). Moves `ViewportTop` by `delta`
    /// (clamped to the document), then drags the cursor only as far as
    /// needed to keep it `scrolloff` lines from the edge. The margin is
    /// relaxed against whichever document edge the viewport is pinned to, so
    /// scrolling up at the top (or down at the bottom) leaves the cursor put.
    /// Designed so the subsequent `ensureViewport scrolloff …` is a fixed
    /// point — the reconcile pass leaves this scroll intact.
    let scrollViewport scrolloff viewportHeight delta (buffer: BufferState) =
        let h = max 1 viewportHeight
        let maxTop = max 0 (lineCount buffer - h)
        let newTop = (buffer.ViewportTop + delta) |> max 0 |> min maxTop
        let m = min (max 0 scrolloff) ((h - 1) / 2)
        let lo = newTop + (if newTop > 0 then m else 0)
        let hi = newTop + h - 1 - (if newTop < maxTop then m else 0)

        let targetColumn =
            buffer.PreferredColumn |> Option.defaultValue buffer.Cursor.Column

        let newLine = buffer.Cursor.Line |> max lo |> min hi

        { buffer with ViewportTop = newTop }
        |> withCursor
            { Line = newLine
              Column = targetColumn }
        |> withPreferredColumn (Some targetColumn)

    /// Mark a buffer as saved. `SavedTick` records which revision the disk
    /// now holds; `Dirty` follows from comparing it with `EditTick`. Undo
    /// history always survives — undoing back to the saved revision shows
    /// clean again via the `Tick` comparison in `undo`/`redo`. If the user
    /// typed while the async write was in flight
    /// (`expectedTick < buffer.EditTick`), the buffer stays dirty.
    let markSaved (expectedTick: int) (filePath: string) (buffer: BufferState) =
        let name = Path.GetFileName filePath |> Option.ofObj |> Option.defaultValue filePath

        { buffer with
            FilePath = Some filePath
            Name = name
            SavedTick = expectedTick
            Dirty = expectedTick <> buffer.EditTick }

    let undo buffer =
        match buffer.Undo with
        | previous :: rest ->
            let current = snapshot buffer

            { buffer with
                Document = previous.Document
                Lines = computeLines previous.Document
                Cursor = previous.Cursor
                PreferredColumn = previous.PreferredColumn
                Selection = None
                Dirty = previous.Tick <> buffer.SavedTick
                Undo = rest
                Redo = current :: buffer.Redo
                EditTick = buffer.EditTick + 1 }
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
                Selection = None
                Dirty = next.Tick <> buffer.SavedTick
                Undo = current :: buffer.Undo
                Redo = rest
                EditTick = buffer.EditTick + 1 }
        | [] -> buffer
