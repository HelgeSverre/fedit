namespace Fedit.Benchmarks

open BenchmarkDotNet.Attributes
open Fedit

/// Finding B pins: piece-table costs at document scale. `fragmented`
/// simulates a mid-session table (512 scattered inserts, ~1000 pieces) so
/// traversal costs are real rather than the single-piece fast path.
[<MemoryDiagnoser>]
type PieceTableBenchmarks() =
    let mutable fragmented = PieceTable.empty
    let mutable mid = 0

    [<Params(10_000, 100_000, 1_000_000)>]
    member val Size = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        mid <- this.Size / 2
        let mutable t = PieceTable.ofString (Corpus.generate this.Size)

        for i in 1..512 do
            t <- PieceTable.insert ((i * 7919) % this.Size) "x" t

        fragmented <- t

    [<Benchmark>]
    member _.InsertMiddle() = PieceTable.insert mid "x" fragmented

    [<Benchmark>]
    member _.DeleteMiddle() =
        PieceTable.deleteRange mid 64 fragmented

    [<Benchmark>]
    member _.ToStringWhole() = PieceTable.toString fragmented

    [<Benchmark>]
    member _.LengthWhole() = PieceTable.length fragmented

/// Finding B headline: a typing burst end to end. `PieceTableTyping` isolates
/// the add-buffer copy (`Added = table.Added + text`, O(session^2) over a
/// session); `BufferTyping` additionally pays `computeLines` (full toString +
/// Split) per keystroke — the number the incremental line-splice must move.
[<MemoryDiagnoser>]
type EditSessionBenchmarks() =
    let mutable table = PieceTable.empty
    let mutable buffer = Unchecked.defaultof<BufferState>

    [<Params(256, 1024)>]
    member val Keystrokes = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        let text = Corpus.generate 100_000
        table <- PieceTable.ofString text
        buffer <- Buffer.fromText 1 None "bench" text "\n" |> Buffer.moveToOffset 50_000

    [<Benchmark>]
    member this.PieceTableTyping() =
        let mutable t = table
        let mutable index = 50_000

        for _ in 1 .. this.Keystrokes do
            t <- PieceTable.insert index "x" t
            index <- index + 1

        t

    [<Benchmark>]
    member this.BufferTyping() =
        let mutable b = buffer

        for _ in 1 .. this.Keystrokes do
            b <- Buffer.insertText "x" b

        b

/// Per-keystroke and lookup costs on one buffer. `InsertTextMiddle` is a
/// single keystroke including the `computeLines` rebuild — the direct
/// before/after pin for the incremental line-splice plan. `computeLines`
/// itself is private; it is measured through `insertText` and `LoadFromText`.
[<MemoryDiagnoser>]
type BufferBenchmarks() =
    let mutable buffer = Unchecked.defaultof<BufferState>
    let mutable text = ""
    let mutable midIndex = 0
    let mutable midPosition = Position.zero

    [<Params(10_000, 100_000, 1_000_000)>]
    member val Size = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        text <- Corpus.generate this.Size
        midIndex <- this.Size / 2
        buffer <- Buffer.fromText 1 None "bench" text "\n" |> Buffer.moveToOffset midIndex
        midPosition <- buffer.Cursor

    [<Benchmark>]
    member _.InsertTextMiddle() = Buffer.insertText "x" buffer

    [<Benchmark>]
    member _.PositionToIndex() =
        Buffer.positionToIndex midPosition buffer

    [<Benchmark>]
    member _.IndexToPosition() = Buffer.indexToPosition midIndex buffer

    [<Benchmark>]
    member _.LoadFromText() =
        Buffer.fromText 1 None "bench" text "\n"

/// `Highlight.spanAt` runs once per visible glyph per frame in the renderer
/// overlay (View.renderEditor). `OverlayRow200` replays that inner loop for
/// one 200-column row. Every 8th span carries a nested child, matching the
/// overlap shape that finding D3's flatten-to-disjoint pass will normalize;
/// numbers stay comparable because the call shape is unchanged.
[<MemoryDiagnoser>]
type HighlightLookupBenchmarks() =
    let mutable spans: HighlightSpan array = Array.empty
    let mutable rowStart = 0

    [<Params(1_000, 20_000)>]
    member val SpanCount = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        spans <-
            [| for i in 0 .. this.SpanCount - 1 do
                   let s = i * 50

                   yield
                       { Capture = HighlightCapture.Keyword
                         StartByte = s
                         EndByte = s + 12 }

                   if i % 8 = 0 then
                       yield
                           { Capture = HighlightCapture.String
                             StartByte = s + 2
                             EndByte = s + 6 } |]

        rowStart <- (this.SpanCount / 2) * 50

    [<Benchmark>]
    member _.SpanAtHit() = Highlight.spanAt spans (rowStart + 3)

    [<Benchmark>]
    member _.OverlayRow200() =
        let mutable hits = 0

        for col in 0..199 do
            match Highlight.spanAt spans (rowStart + col) with
            | Some _ -> hits <- hits + 1
            | None -> ()

        hits

/// Rgb -> 256 quantization. `quantizeRgb` scans all 256 cube entries and
/// `cubeRgb` allocates its standard-16 lookup table on every call for indices
/// below 16 — MemoryDiagnoser makes that visible. Exercised by the renderer's
/// downgrade path on non-truecolor terminals.
[<MemoryDiagnoser>]
type ColorBenchmarks() =
    [<Benchmark>]
    member _.QuantizeAccent() =
        Color.toIndexed (Rgb(0x00uy, 0xB8uy, 0x6Buy))

    [<Benchmark>]
    member _.QuantizeRamp64() =
        let mutable acc = 0

        for i in 0..63 do
            let v = byte (i * 4)

            match Color.toIndexed (Rgb(v, byte (255 - i * 4), v)) with
            | Some idx -> acc <- acc + int idx
            | None -> ()

        acc

/// Screen-diff costs at the two reference terminal sizes. Stateless and pure
/// managed, so it lives in BDN: `FullRepaint` is the resize/first-frame path,
/// `DiffOneCell` the steady-state caret move, `DiffScroll` the worst-case
/// everything-changed diff without a clear-screen.
[<MemoryDiagnoser>]
type RendererBenchmarks() =
    let mutable baseScreen = Screen.create 1 1
    let mutable oneCell = Screen.create 1 1
    let mutable scrolled = Screen.create 1 1

    let buildScreen width height offset =
        let screen = Screen.create width height

        for y in 0 .. height - 1 do
            let style =
                if (y + offset) % 5 = 0 then
                    Style.withColors (Rgb(204uy, 120uy, 50uy)) Default
                elif (y + offset) % 3 = 0 then
                    Style.withColors (Indexed 35uy) Default
                else
                    Style.defaultStyle

            let text = (Corpus.line (y + offset)).PadRight(width)
            Screen.writeText 0 y style width text screen

        screen

    [<Params("80x24", "250x70")>]
    member val Size = "" with get, set

    [<GlobalSetup>]
    member this.Setup() =
        let parts = this.Size.Split 'x'
        let width = int parts[0]
        let height = int parts[1]
        baseScreen <- buildScreen width height 0
        oneCell <- buildScreen width height 0
        Screen.setCell (width / 2) (height / 2) Style.defaultStyle '@' oneCell
        scrolled <- buildScreen width height 1

    [<Benchmark>]
    member _.FullRepaint() =
        Renderer.render System.IO.TextWriter.Null ColorTrueColor ValueNone baseScreen

    [<Benchmark>]
    member _.DiffOneCell() =
        Renderer.render System.IO.TextWriter.Null ColorTrueColor (ValueSome baseScreen) oneCell

    [<Benchmark>]
    member _.DiffScroll() =
        Renderer.render System.IO.TextWriter.Null ColorTrueColor (ValueSome baseScreen) scrolled
