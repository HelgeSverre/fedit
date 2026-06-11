module Fedit.Benchmarks.FrameBench

open System
open System.Diagnostics
open System.IO
open Fedit

let private withBuffer f (model: Model) =
    let id = model.Editors.ActiveBufferId
    let buffer = model.Editors.Buffers[id]

    { model with
        Editors =
            { model.Editors with
                Buffers = Map.add id (f buffer) model.Editors.Buffers } }

/// A realistic editing model: `Editor.init`'s state (startup effects
/// discarded — init is pure) with the scratch buffer replaced by the corpus
/// and highlight spans installed, mirroring what tests do (SnapshotTests).
let private buildModel width height text spans =
    let model, _ =
        Editor.init "/bench" { Width = width; Height = height } (Config.defaults Themes.defaultTheme) []

    let buffer = Buffer.fromText 1 (Some "/bench/corpus.fs") "corpus.fs" text "\n"

    { model with
        Editors =
            { model.Editors with
                Buffers = Map.ofList [ 1, buffer ] }
        HighlightStates = Map.ofList [ 1, spans ]
        Notification = None }

/// Render `frames` consecutive frames, advancing the model between frames.
/// `carryPrev = true` feeds each frame's screen back as `previous`, so
/// `Renderer.render` exercises its real diff path; `false` forces the
/// first-frame/resize full-repaint path every time. Timing covers
/// Layout.render + Renderer.render only — `advance` runs outside the clock.
let private runScenario label frames carryPrev (initial: Model) (advance: Model -> Model) =
    let mutable model = initial
    let mutable prev: Screen voption = ValueNone

    for _ in 1..20 do
        model <- advance model
        let screen = Layout.render model
        Renderer.render TextWriter.Null ColorTrueColor prev screen
        prev <- if carryPrev then ValueSome screen else ValueNone

    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()

    let timings = Array.zeroCreate frames
    let allocBefore = GC.GetAllocatedBytesForCurrentThread()
    let sw = Stopwatch()

    for i in 0 .. frames - 1 do
        model <- advance model
        sw.Restart()
        let screen = Layout.render model
        Renderer.render TextWriter.Null ColorTrueColor prev screen
        sw.Stop()
        prev <- if carryPrev then ValueSome screen else ValueNone
        timings[i] <- sw.Elapsed.TotalMilliseconds

    Manual.report label timings (GC.GetAllocatedBytesForCurrentThread() - allocBefore)

let run () =
    let text = Corpus.generate 100_000

    let spans, grammarNote =
        match Grammar.pickLanguage () with
        | Some lang ->
            match Grammar.parse lang text with
            | Some spans -> spans, $"highlight spans: {spans.Length} via '{lang}' grammar"
            | None -> Array.empty, "highlight spans: parse failed — overlay disabled"
        | None -> Array.empty, "highlight spans: no grammars loaded — overlay disabled (run 'just build-grammars')"

    printfn ""
    printfn "── Frame pipeline: Layout.render + Renderer.render (100k-char buffer) ──"
    printfn "%s" grammarNote
    Manual.header ()

    for width, height in [ 80, 24; 250, 70 ] do
        let initial = buildModel width height text spans
        let metrics = Dock.metrics initial

        let viewportWidth =
            max 1 (metrics.EditorWidth - Buffer.gutterWidth initial.Editors.Buffers[1])

        let cursorWalk model =
            model
            |> withBuffer (fun b -> Buffer.moveDown b |> Buffer.ensureViewport 5 metrics.MainHeight viewportWidth)

        let noHighlight =
            { initial with
                Config =
                    { initial.Config with
                        SyntaxHighlightingEnabled = false } }

        runScenario $"{width}x{height} full repaint" 120 false initial id
        runScenario $"{width}x{height} identical-frame diff" 120 true initial id
        runScenario $"{width}x{height} cursor walk, highlight on" 240 true initial cursorWalk
        runScenario $"{width}x{height} cursor walk, highlight off" 240 true noHighlight cursorWalk
