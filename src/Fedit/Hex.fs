namespace Fedit

open System
open System.Text

/// Which pane of the hex view holds the caret.
type HexPane =
    | HexBytes
    | HexAscii

/// Per-buffer hex view state, held in `Model.HexViews` keyed by
/// `BufferState.Id`. Presence in that map IS the "this buffer is a hex
/// view" flag — the buffer itself stays a plain latin1-projected
/// `BufferState`, so the piece table, undo stacks, and selection spans
/// work unchanged in byte space (one byte per char, offsets identical).
type HexViewState =
    {
        Pane: HexPane
        /// True when the next typed hex digit writes the high nibble of
        /// the byte under the caret. Reset by any cursor motion.
        HighNibble: bool
        /// First visible hex row (rows are `BytesPerRow` bytes each —
        /// the hex sibling of `BufferState.ViewportTop`).
        Top: int
    }

/// Column layout of one hex row for a given content width:
/// `00000010  74 68 69 73 20 69 73 20  61 20 74 65 73 74 2c 20  this is a test, `
/// offset column | hex byte cells (extra gap after each 8) | ASCII cells.
/// All columns are relative to the editor content origin. Shared by the
/// renderer, mouse hit-testing, and cursor movement (the `Dock.metrics`
/// convention) so paint and input can never drift.
type HexLayout =
    { BytesPerRow: int
      HexStart: int
      AsciiStart: int
      RowWidth: int }

/// Byte-space helpers for the hex view: the latin1 byte↔text projection,
/// binary-file detection, row/column geometry, hex-query parsing, and
/// byte-exact search. Pure — no I/O, no Model.
[<RequireQualifiedAccess>]
module Hex =
    let initialView =
        { Pane = HexBytes
          HighNibble = true
          Top = 0 }

    // ── latin1 projection ────────────────────────────────────────────────
    // ISO-8859-1 maps byte n to char n for all 256 values, so a projected
    // document has char offset = byte offset and round-trips losslessly.

    let bytesToText (bytes: byte[]) : string = Encoding.Latin1.GetString bytes

    let textToBytes (text: string) : byte[] = Encoding.Latin1.GetBytes text

    /// Decode raw bytes as text the way `File.ReadAllText` would: BOM
    /// detection with a UTF-8 default. The hex→text view flip.
    let decodeText (bytes: byte[]) : string =
        use reader =
            new IO.StreamReader(new IO.MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks = true)

        reader.ReadToEnd()

    /// Git's binary heuristic: a NUL byte in the first 8000 bytes.
    let looksBinary (bytes: byte[]) : bool =
        let limit = min bytes.Length 8000
        let mutable index = 0
        let mutable found = false

        while not found && index < limit do
            found <- bytes[index] = 0uy
            index <- index + 1

        found

    // ── geometry ─────────────────────────────────────────────────────────

    /// Width of the offset column ("00000010"), excluding its two-space gap.
    let offsetWidth = 8

    /// Total columns a row of `bytesPerRow` needs: offset column + gap +
    /// hex cells (3 per byte, minus the trailing space, plus one extra gap
    /// per 8-byte group after the first) + gap + ASCII cells.
    let private widthFor bytesPerRow =
        offsetWidth
        + 2
        + (bytesPerRow * 3 - 1)
        + max 0 ((bytesPerRow - 1) / 8)
        + 2
        + bytesPerRow

    /// Pick the widest classic row (16, 8, or 4 bytes) that fits.
    let layoutFor (contentWidth: int) : HexLayout =
        let bytesPerRow =
            [ 16; 8; 4 ]
            |> List.tryFind (fun n -> widthFor n <= contentWidth)
            |> Option.defaultValue 4

        { BytesPerRow = bytesPerRow
          HexStart = offsetWidth + 2
          AsciiStart = offsetWidth + 2 + (bytesPerRow * 3 - 1) + max 0 ((bytesPerRow - 1) / 8) + 2
          RowWidth = widthFor bytesPerRow }

    /// Column of byte `byteInRow`'s high-nibble cell (low nibble is +1).
    let hexColOf (layout: HexLayout) (byteInRow: int) : int =
        layout.HexStart + byteInRow * 3 + byteInRow / 8

    /// Column of byte `byteInRow`'s ASCII cell.
    let asciiColOf (layout: HexLayout) (byteInRow: int) : int = layout.AsciiStart + byteInRow

    /// Inverse of the two above for mouse hit-testing: which byte-in-row,
    /// pane, and nibble a content-relative column lands on. `None` for the
    /// offset column and the gaps.
    let targetAt (layout: HexLayout) (col: int) : (int * HexPane * bool) option =
        if col >= layout.AsciiStart && col < layout.AsciiStart + layout.BytesPerRow then
            Some(col - layout.AsciiStart, HexAscii, true)
        elif col >= layout.HexStart && col < layout.AsciiStart then
            [ 0 .. layout.BytesPerRow - 1 ]
            |> List.tryPick (fun i ->
                let cell = hexColOf layout i

                if col = cell then Some(i, HexBytes, true)
                elif col = cell + 1 then Some(i, HexBytes, false)
                else None)
        else
            None

    /// Rows a document of `length` bytes occupies. Always at least one, and
    /// the append position (offset = length) gets a cell, mirroring the text
    /// view's cursor-past-last-char convention.
    let rowCount (bytesPerRow: int) (length: int) : int = length / bytesPerRow + 1

    // ── hex digits & query parsing ───────────────────────────────────────

    let digitValue (c: char) : int option =
        if c >= '0' && c <= '9' then Some(int c - int '0')
        elif c >= 'a' && c <= 'f' then Some(int c - int 'a' + 10)
        elif c >= 'A' && c <= 'F' then Some(int c - int 'A' + 10)
        else None

    let isHexDigit (c: char) = (digitValue c).IsSome

    /// Parse a byte-sequence literal — "1A 2C 78", "1a2c78" — into its
    /// latin1-projected text. `None` unless the query is entirely hex
    /// digits and whitespace with an even digit count.
    let tryParseBytes (query: string) : string option =
        let digits = query |> Seq.filter (fun c -> not (Char.IsWhiteSpace c)) |> Seq.toArray

        if
            digits.Length = 0
            || digits.Length % 2 <> 0
            || not (digits |> Array.forall isHexDigit)
        then
            None
        else
            let sb = StringBuilder(digits.Length / 2)

            for i in 0..2 .. digits.Length - 2 do
                let hi = digitValue digits[i] |> Option.defaultValue 0
                let lo = digitValue digits[i + 1] |> Option.defaultValue 0
                sb.Append(char (hi * 16 + lo)) |> ignore

            Some(sb.ToString())

    /// What a search query means in a hex buffer: a hex byte sequence when
    /// it parses as one, else the literal text. Pure and deterministic —
    /// the effect interpreter and the renderer both call it, so the match
    /// offsets and the painted match width can never disagree.
    let searchNeedle (query: string) : string =
        tryParseBytes query |> Option.defaultValue query

    /// Render a latin1-projected slice as a spaced hex string ("74 68 69")
    /// — the clipboard form of a hex-view copy.
    let toHexString (text: string) : string =
        text |> Seq.map (fun c -> (int c &&& 0xFF).ToString "x2") |> String.concat " "

    // ── byte-exact search ────────────────────────────────────────────────
    // The hex sibling of `Buffer.findAllMatches`, which is deliberately
    // case-insensitive for text. Bytes 0x41 and 0x61 are different data,
    // so hex buffers match ordinally.

    let findAllExact (needle: string) (haystack: string) : int list =
        if String.IsNullOrEmpty needle || String.IsNullOrEmpty haystack then
            []
        else
            let mutable matches = []
            let mutable index = haystack.IndexOf(needle, StringComparison.Ordinal)

            while index >= 0 do
                matches <- index :: matches
                index <- haystack.IndexOf(needle, index + 1, StringComparison.Ordinal)

            List.rev matches

    /// First exact match at or after `fromIndex`, wrapping — the cyclic
    /// semantics of `Buffer.findNextMatch`.
    let findNextExact (needle: string) (fromIndex: int) (haystack: string) : int option =
        match findAllExact needle haystack with
        | [] -> None
        | matches ->
            matches
            |> List.tryFind (fun offset -> offset >= fromIndex)
            |> Option.orElse (Some(List.head matches))

    /// Last exact match at or before `fromIndex`, wrapping backwards.
    let findPreviousExact (needle: string) (fromIndex: int) (haystack: string) : int option =
        match findAllExact needle haystack with
        | [] -> None
        | matches ->
            matches
            |> List.filter (fun offset -> offset <= fromIndex)
            |> List.tryLast
            |> Option.orElse (List.tryLast matches)

    // ── reading bytes out of a projected buffer ──────────────────────────

    /// The document slice [start, start+count) as latin1 chars, read from
    /// the buffer's cached line array ('\n' slots are real 0x0A bytes) —
    /// one O(lines) locate then O(count), so the renderer never pays a
    /// full `PieceTable.toString` per frame.
    let slice (buffer: BufferState) (start: int) (count: int) : string =
        let length = PieceTable.length buffer.Document
        let start = max 0 (min start length)
        let count = max 0 (min count (length - start))

        if count = 0 then
            ""
        else
            let rows = Buffer.lines buffer
            let position = Buffer.indexToPosition start buffer
            let sb = StringBuilder count
            let mutable line = position.Line
            let mutable col = position.Column

            while sb.Length < count && line < rows.Length do
                let text = rows[line]

                if col < text.Length then
                    let take = min (text.Length - col) (count - sb.Length)
                    sb.Append(text, col, take) |> ignore
                    col <- col + take
                else
                    // The '\n' between cached lines is a real document byte.
                    if sb.Length < count then
                        sb.Append '\n' |> ignore

                    line <- line + 1
                    col <- 0

            sb.ToString()

    /// The byte under `offset`, or `None` past the end.
    let byteAt (buffer: BufferState) (offset: int) : int option =
        match slice buffer offset 1 with
        | "" -> None
        | s -> Some(int s[0] &&& 0xFF)
