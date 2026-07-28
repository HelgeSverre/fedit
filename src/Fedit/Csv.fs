namespace Fedit

open System
open System.Text

/// A display-only row filter on a CSV grid: rows whose `Column` cell
/// doesn't equal `Value` are hidden from the grid — never from the file.
/// Saving always writes every row; the filter touches rendering, cursor
/// motion, mouse mapping, and column stats only.
type CsvRowFilter =
    {
        Column: int
        Value: string
        /// Data-line indices (all ≥ 1 — the header never hides) passing
        /// the filter, ascending. Derived: recomputed by the
        /// `refreshCsvFilter` chokepoint when the buffer's line count
        /// changes (and explicitly after `:sort`); cell edits that change
        /// membership keep the current set until then — the spreadsheet
        /// convention (a filter is a snapshot, not a live query).
        VisibleRows: int[]
        /// Line count `VisibleRows` was computed against (staleness check).
        LineCount: int
    }

/// Per-buffer CSV view state, held in `Model.CsvViews` keyed by
/// `BufferState.Id`. Presence in that map IS the "this buffer is a CSV
/// grid" flag — the buffer itself stays plain text (the file's real
/// separators and quoting are untouched), so the piece table, undo,
/// selection, search, and the save path work unchanged. The view is a
/// projection only: cells pad to shared column widths, separators paint
/// as grid rules, and line 0 pins as a header row.
type CsvViewState =
    {
        /// The detected (or forced) cell separator — a real character in
        /// the underlying text, e.g. ',', ';', '\t', '|'.
        Separator: char
        /// Column widths sampled from the first `Csv.sampleLimit` lines at
        /// toggle time. Fixed for the life of the view: a longer cell only
        /// pushes its own row's tail right (`effectiveWidth`), so layout
        /// stays a pure function of (widths, line) and the renderer, cursor
        /// mapping, and mouse hit-testing can never disagree.
        Widths: int[]
        /// Active row filter, if any (`:filter <value>` / `:filter off`).
        Filter: CsvRowFilter option
    }

/// Pure CSV-grid helpers: quote-aware cell splitting, separator
/// detection, sampled column widths, and the text-column ↔ rendered-x
/// mapping shared by the renderer, cursor placement, and mouse
/// hit-testing (the `Dock.metrics` convention). No I/O, no Model.
///
/// Cells are line-scoped: an RFC 4180 quoted cell that spans lines
/// renders as its constituent lines (the quotes make that visible);
/// editing it still round-trips because the text is never rewritten.
///
/// The per-frame primitives (`cellRanges`, `renderedX`, `textColAt`,
/// `renderLine`) and the O(rows) `columnStats` pass are imperative
/// scans on purpose — they run for every painted row every frame (or
/// over millions of data rows); everything else stays functional.
[<RequireQualifiedAccess>]
module Csv =
    /// Lines sampled for column widths at toggle time — keeps `:csv` O(1)
    /// in the file size, so a huge export can't hang the toggle.
    let sampleLimit = 1000

    /// Lines sampled for separator detection.
    let private detectLimit = 20

    let private candidates = [ ','; ';'; '\t'; '|' ]

    /// Cell boundaries of one line as struct (start, length) in text
    /// columns, quote-aware: separators inside a double-quoted region
    /// don't split, and a doubled quote is the RFC 4180 escape (two
    /// toggles, net no-op for splitting). Cells keep their quotes
    /// verbatim — the view never reinterprets the underlying text.
    /// Two counting passes into one exact-size struct-tuple array: this
    /// runs per painted row per frame (and per data row in stats/sort),
    /// so it allocates a single object instead of a cons cell + boxed
    /// tuple per cell.
    let cellRanges (sep: char) (line: string) : struct (int * int)[] =
        let mutable inQuotes = false
        let mutable count = 1

        for i in 0 .. line.Length - 1 do
            let c = line[i]

            if c = '"' then
                inQuotes <- not inQuotes
            elif c = sep && not inQuotes then
                count <- count + 1

        let cells = Array.zeroCreate count
        let mutable start = 0
        let mutable index = 0
        inQuotes <- false

        for i in 0 .. line.Length - 1 do
            let c = line[i]

            if c = '"' then
                inQuotes <- not inQuotes
            elif c = sep && not inQuotes then
                cells[index] <- struct (start, i - start)
                index <- index + 1
                start <- i + 1

        cells[index] <- struct (start, line.Length - start)
        cells

    /// Pick the separator: the candidate that splits every sampled
    /// non-empty line (highest minimum split count wins; total count
    /// breaks ties). Falls back to ',' when nothing splits anything.
    let detectSeparator (lines: string[]) : char =
        let sample =
            lines
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Array.truncate detectLimit

        if sample.Length = 0 then
            ','
        else
            candidates
            |> List.map (fun sep ->
                let counts = sample |> Array.map (fun l -> (cellRanges sep l).Length - 1)

                let minCount = if counts.Length = 0 then 0 else Array.min counts
                sep, minCount, Array.sum counts)
            |> List.sortByDescending (fun (_, minCount, total) -> minCount, total)
            |> List.head
            |> fun (sep, _, _) -> sep

    /// Column widths over the first `sampleLimit` lines: the widest cell
    /// seen per column index.
    let sampleWidths (sep: char) (lines: string[]) : int[] =
        // Zip-to-the-longer merge: columns a line doesn't have keep the
        // widths seen so far.
        let rec merge widths lengths =
            match widths, lengths with
            | rest, []
            | [], rest -> rest
            | width :: widthTail, length :: lengthTail -> max width length :: merge widthTail lengthTail

        let cellLengths line =
            cellRanges sep line |> Array.map (fun (struct (_, len)) -> len) |> Array.toList

        lines
        |> Array.truncate sampleLimit
        |> Array.fold (fun widths line -> merge widths (cellLengths line)) []
        |> List.toArray

    /// Width column `index` renders at on a line whose cell is `len`
    /// chars: the sampled width, grown for this line only when its own
    /// cell is longer (or the column wasn't sampled at all).
    let private effectiveWidth (widths: int[]) (index: int) (len: int) =
        if index < widths.Length then max widths[index] len else len

    /// The grid glyph a separator paints as. One screen cell, always —
    /// the mapping below depends on that.
    let displaySep (_sep: char) = '│'

    /// Human name for the separator (status bar, notifications).
    let sepName (sep: char) =
        match sep with
        | ',' -> "comma"
        | ';' -> "semicolon"
        | '\t' -> "tab"
        | '|' -> "pipe"
        | c -> $"'{c}'"

    /// Rendered x of text column `textCol` on `line`. Columns inside a
    /// cell map 1:1 (cells are never truncated, only padded); the
    /// separator character itself sits on the grid rule after the cell's
    /// padding; the end-of-line caret sits just past the last cell's text.
    let renderedX (widths: int[]) (sep: char) (line: string) (textCol: int) : int =
        let cells = cellRanges sep line
        let mutable x = 0
        let mutable result = -1

        for i in 0 .. cells.Length - 1 do
            if result < 0 then
                let (struct (start, len)) = cells[i]
                let w = effectiveWidth widths i len
                let isLast = i = cells.Length - 1

                if textCol < start + len then
                    result <- x + textCol - start
                elif textCol = start + len && not isLast then
                    result <- x + w
                elif isLast then
                    result <- x + min (textCol - start) len
                else
                    x <- x + w + 1

        max 0 result

    /// Inverse of `renderedX` for mouse hit-testing: the text column a
    /// rendered x lands on. Padding clicks snap to the cell's end (the
    /// separator position), clicks past the last cell to the line end.
    let textColAt (widths: int[]) (sep: char) (line: string) (x: int) : int =
        let cells = cellRanges sep line
        let mutable acc = 0
        let mutable result = -1

        for i in 0 .. cells.Length - 1 do
            if result < 0 then
                let (struct (start, len)) = cells[i]
                let w = effectiveWidth widths i len
                let isLast = i = cells.Length - 1

                if x < acc + w then
                    result <- start + min (max 0 (x - acc)) len
                elif isLast || x = acc + w then
                    result <- start + len
                else
                    acc <- acc + w + 1

        if result < 0 then line.Length else result

    /// One line's rendered form: cells padded to their effective widths
    /// with a `displaySep` grid rule between them, plus the rendered
    /// columns those rules land on (for styling). Geometry is identical
    /// to `renderedX`/`textColAt` by construction.
    let renderLine (widths: int[]) (sep: char) (line: string) : string * int list =
        let cells = cellRanges sep line
        let sb = StringBuilder(line.Length + 16)
        let mutable sepCols = []

        for i in 0 .. cells.Length - 1 do
            let (struct (start, len)) = cells[i]
            let w = effectiveWidth widths i len
            sb.Append(line, start, len) |> ignore

            if i < cells.Length - 1 then
                sb.Append(' ', w - len) |> ignore
                sepCols <- sb.Length :: sepCols
                sb.Append(displaySep sep) |> ignore

        sb.ToString(), List.rev sepCols

    /// The cell a text column sits in: (cell index, offset inside the
    /// cell). A column on the separator itself reports the cell it
    /// terminates with offset = cell length.
    let cellIndexAt (sep: char) (line: string) (textCol: int) : int * int =
        let cells = cellRanges sep line

        // Cells are ordered by start, so the owner is the last cell whose
        // start is at or before the column.
        let rec pick index =
            if index < 0 then
                0, 0
            else
                let (struct (start, len)) = cells[index]

                if textCol >= start then
                    index, min (textCol - start) len
                else
                    pick (index - 1)

        pick (cells.Length - 1)

    /// Rendered x-span [start, end) of the whole grid column containing
    /// `textCol` — end covers the cell's padding and its grid rule, so
    /// scrolling this span into view shows the complete column, not the
    /// one-character sliver a caret-only follow would reveal.
    let cellSpanX (widths: int[]) (sep: char) (line: string) (textCol: int) : int * int =
        let cells = cellRanges sep line

        let rec walk x index =
            let (struct (start, len)) = cells[index]
            let width = effectiveWidth widths index len

            if index = cells.Length - 1 then x, x + width
            elif textCol <= start + len then x, x + width + 1
            else walk (x + width + 1) (index + 1)

        walk 0 0

    /// Strip one layer of RFC 4180 quoting for value comparison and
    /// number parsing: surrounding double quotes drop, doubled quotes
    /// collapse. Non-quoted text passes through trimmed.
    let unquote (cell: string) : string =
        let trimmed = cell.Trim()

        if trimmed.Length >= 2 && trimmed.StartsWith '"' && trimmed.EndsWith '"' then
            trimmed.Substring(1, trimmed.Length - 2).Replace("\"\"", "\"")
        else
            trimmed

    /// The unquoted text of column `col` on a line ("" when the line has
    /// fewer cells).
    let cellTextAt (sep: char) (col: int) (line: string) : string =
        match cellRanges sep line |> Array.tryItem col with
        | Some(struct (start, len)) -> unquote (line.Substring(start, len))
        | None -> ""

    /// Parse a cell as a number (invariant culture, quotes stripped).
    /// `voption`: this runs per cell inside `columnStats` (O(rows)) and
    /// per comparison inside `sortedLines` (O(n log n)) — the two loops
    /// hot enough that a heap-allocated `Some` per call would matter.
    let tryNumber (cell: string) : float voption =
        match
            Double.TryParse(unquote cell, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture)
        with
        | true, value -> ValueSome value
        | _ -> ValueNone

    /// Index of the last data line: the final line, except a trailing
    /// empty line (the file's closing newline) which is never a row.
    let private lastDataRow (lines: string[]) : int =
        if lines.Length > 1 && lines[lines.Length - 1] = "" then
            lines.Length - 2
        else
            lines.Length - 1

    /// The cell predicate a `:filter` value means: a `>` / `>=` / `<` /
    /// `<=` prefix followed by a number compares numerically (cells that
    /// don't parse as numbers never match — an operator filter is a
    /// numeric question); anything else is an exact match on the
    /// unquoted, trimmed cell text. An operator followed by a non-number
    /// falls back to exact match rather than guessing.
    let private cellPredicate (value: string) : string -> bool =
        let wanted = value.Trim()

        let numeric (op: float -> float -> bool) (boundText: string) =
            match tryNumber boundText with
            | ValueSome bound ->
                Some(fun cell ->
                    match tryNumber cell with
                    | ValueSome v -> op v bound
                    | ValueNone -> false)
            | ValueNone -> None

        let comparison =
            if wanted.StartsWith ">=" then
                numeric (>=) (wanted.Substring 2)
            elif wanted.StartsWith "<=" then
                numeric (<=) (wanted.Substring 2)
            elif wanted.StartsWith ">" then
                numeric (>) (wanted.Substring 1)
            elif wanted.StartsWith "<" then
                numeric (<) (wanted.Substring 1)
            else
                None

        match comparison with
        | Some predicate -> predicate
        | None -> fun cell -> cell = wanted

    /// Whether a filter row matches: the `:filter` semantics shared by
    /// `filterRows` and `columnStats`, so the visible rows and the
    /// filtered aggregates can never disagree.
    let matchesFilter (sep: char) (col: int) (value: string) : string -> bool =
        let predicate = cellPredicate value
        fun line -> predicate (cellTextAt sep col line)

    /// Data-line indices (1..last) whose `col` cell matches `value` (see
    /// `cellPredicate`: exact text, or `>` / `>=` / `<` / `<=` numeric
    /// comparisons). Line 0 is the header and never participates.
    let filterRows (sep: char) (col: int) (value: string) (lines: string[]) : int[] =
        let matches = matchesFilter sep col value

        [| for i in 1 .. lastDataRow lines do
               if matches lines[i] then
                   yield i |]

    /// Index of the first element ≥ `line` in a sorted array (the
    /// insertion point; `rows.Length` when every element is below).
    /// Shared by the renderer and mouse hit-testing to find the first
    /// filtered row at or below the viewport top.
    let rankAtOrAbove (rows: int[]) (line: int) : int =
        match Array.BinarySearch(rows, line) with
        | found when found >= 0 -> found
        | missing -> ~~~missing

    /// Largest element of a sorted array strictly below `line`.
    let prevIn (rows: int[]) (line: int) : int option =
        match rankAtOrAbove rows line with
        | 0 -> None
        | rank -> Some rows[rank - 1]

    /// Smallest element of a sorted array strictly above `line`.
    let nextIn (rows: int[]) (line: int) : int option =
        match rankAtOrAbove rows (line + 1) with
        | rank when rank < rows.Length -> Some rows[rank]
        | _ -> None

    /// Aggregates over one column's numeric cells — the Excel status-bar
    /// readout. The floats are `System.Double` (F#'s `float`); `Count` is
    /// int64 so the type stays correct even if loading ever stops
    /// materializing the whole document (today's string-backed buffer
    /// caps rows well under Int32, but that's a loading detail, not a
    /// stats contract).
    type ColumnStats =
        { Count: int64
          Sum: float
          Avg: float
          Min: float
          Max: float }

    /// Aggregate the numeric cells in column `col` over the data rows
    /// (respecting `filter` when given), or None when the column holds no
    /// numeric values — the `Avg` division is guarded by that empty case.
    /// Pure — the `ComputeCsvStats` interpreter runs this off the UI
    /// thread, so a huge file costs a background pass, never a UI freeze.
    let columnStats (sep: char) (col: int) (filter: (int * string) option) (lines: string[]) : ColumnStats option =
        let mutable count = 0L
        let mutable sum = 0.0
        let mutable low = Double.PositiveInfinity
        let mutable high = Double.NegativeInfinity

        let included =
            match filter with
            | Some(filterCol, value) -> matchesFilter sep filterCol value
            | None -> fun _ -> true

        for i in 1 .. lastDataRow lines do
            let line = lines[i]

            if included line then
                match tryNumber (cellTextAt sep col line) with
                | ValueSome value ->
                    count <- count + 1L
                    sum <- sum + value
                    low <- min low value
                    high <- max high value
                | ValueNone -> ()

        if count = 0L then
            None
        else
            Some
                { Count = count
                  Sum = sum
                  Avg = sum / float count
                  Min = low
                  Max = high }

    /// The full line array with the data rows stably sorted by column
    /// `col`: numeric when both cells parse as numbers, ordinal
    /// case-insensitive otherwise. The header (line 0) and a trailing
    /// empty line (the closing newline) stay put.
    let sortedLines (sep: char) (col: int) (ascending: bool) (lines: string[]) : string[] =
        let last = lastDataRow lines

        if last < 1 then
            lines
        else
            let compareRows (a: string) (b: string) =
                let ka = cellTextAt sep col a
                let kb = cellTextAt sep col b

                let ordered =
                    match tryNumber ka, tryNumber kb with
                    | ValueSome x, ValueSome y -> compare x y
                    | _ -> String.Compare(ka, kb, StringComparison.OrdinalIgnoreCase)

                if ascending then ordered else -ordered

            Array.concat
                [ lines[0..0]
                  lines[1..last] |> Array.sortWith compareRows
                  lines[last + 1 ..] ]

    /// Text column of the next cell's first character after `textCol`,
    /// or None on the last cell (Tab navigation wraps to the next line).
    let nextCellStart (sep: char) (line: string) (textCol: int) : int option =
        cellRanges sep line
        |> Array.tryPick (fun (struct (start, _)) -> if start > textCol then Some start else None)

    /// Text column of the previous cell's first character before
    /// `textCol`, or None on the first cell.
    let previousCellStart (sep: char) (line: string) (textCol: int) : int option =
        cellRanges sep line
        |> Array.filter (fun (struct (start, _)) -> start < textCol)
        |> Array.tryLast
        |> Option.map (fun (struct (start, _)) -> start)
