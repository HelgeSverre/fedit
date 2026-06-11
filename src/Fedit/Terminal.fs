namespace Fedit

open System
open System.IO
open System.Text

/// All mutable terminal state lives here. The editor core never sees this.
type TerminalState =
    {
        Writer: TextWriter
        mutable Capabilities: TerminalCapabilities
        mutable PreviousFrame: Screen voption
        mutable CurrentStyle: Style voption
        PendingKeys: System.Collections.Generic.Queue<ConsoleKeyInfo>
        /// `Some` while a bracketed paste is in flight: everything read between
        /// the ESC[200~ and ESC[201~ markers accumulates here instead of being
        /// dispatched as key events. `None` when no paste is active.
        mutable PasteAccumulator: System.Text.StringBuilder option
    }

/// Private ANSI sequence helpers.
[<RequireQualifiedAccess>]
module private Ansi =
    let esc = "\u001b"
    let resetStyle = $"{esc}[0m"
    let clearScreen = $"{esc}[2J"
    let homeCursor = $"{esc}[H"
    let cursorPosition row col = $"{esc}[{row + 1};{col + 1}H"
    let showCursor = $"{esc}[?25h"
    let hideCursor = $"{esc}[?25l"

    // -----------------------------------------------------------------------
    // Capability-driven enable / disable sequences.
    // -----------------------------------------------------------------------

    let enable (caps: TerminalCapabilities) : string =
        let parts = ResizeArray<string>()

        if caps.SupportsAlternateScreen then
            parts.Add($"{esc}[?1049h")

        match caps.KeyboardProtocol with
        | KeyboardKittyBasic -> parts.Add($"{esc}[>1u")
        | KeyboardKittyExtended -> parts.Add($"{esc}[>3u")
        | KeyboardKittyFull -> parts.Add($"{esc}[>5u")
        | KeyboardLegacy -> ()

        match caps.MouseEventMode, caps.MouseEncoding with
        | MouseNormal, MouseSgr -> parts.Add($"{esc}[?1000h{esc}[?1006h")
        | MouseButton, MouseSgr -> parts.Add($"{esc}[?1002h{esc}[?1006h")
        | MouseAll, MouseSgr -> parts.Add($"{esc}[?1003h{esc}[?1006h")
        | _ -> ()

        if caps.SupportsFocusEvents then
            parts.Add($"{esc}[?1004h")

        if caps.SupportsBracketedPaste then
            parts.Add($"{esc}[?2004h")

        parts.Add hideCursor
        parts.Add clearScreen
        String.concat "" parts

    let disable (caps: TerminalCapabilities) : string =
        let parts = ResizeArray<string>()

        parts.Add resetStyle
        parts.Add showCursor

        if caps.SupportsBracketedPaste then
            parts.Add($"{esc}[?2004l")

        if caps.SupportsFocusEvents then
            parts.Add($"{esc}[?1004l")

        match caps.MouseEventMode, caps.MouseEncoding with
        | MouseNormal, MouseSgr -> parts.Add($"{esc}[?1006l{esc}[?1000l")
        | MouseButton, MouseSgr -> parts.Add($"{esc}[?1006l{esc}[?1002l")
        | MouseAll, MouseSgr -> parts.Add($"{esc}[?1006l{esc}[?1003l")
        | _ -> ()

        match caps.KeyboardProtocol with
        | KeyboardKittyBasic
        | KeyboardKittyExtended
        | KeyboardKittyFull -> parts.Add($"{esc}[<u")
        | KeyboardLegacy -> ()

        if caps.SupportsAlternateScreen then
            parts.Add($"{esc}[?1049l")

        String.concat "" parts

/// The single abstraction over terminal input/output.
/// Runtime owns the event loop; Terminal owns every byte that crosses
/// the terminal boundary.
[<RequireQualifiedAccess>]
module Terminal =

    let create () : TerminalState =
        let caps = TerminalCapabilities.fromEnv ()

        { Writer = Console.Out
          Capabilities = caps
          PreviousFrame = ValueNone
          CurrentStyle = ValueNone
          PendingKeys = System.Collections.Generic.Queue<ConsoleKeyInfo>()
          PasteAccumulator = None }

    /// Test helper: construct a terminal with an explicit capability set
    /// and a custom writer (e.g. StringWriter).
    let createWithCapabilities (writer: TextWriter) (caps: TerminalCapabilities) : TerminalState =
        { Writer = writer
          Capabilities = caps
          PreviousFrame = ValueNone
          CurrentStyle = ValueNone
          PendingKeys = System.Collections.Generic.Queue<ConsoleKeyInfo>()
          PasteAccumulator = None }

    let enter (t: TerminalState) =
        t.Writer.Write(Ansi.enable t.Capabilities)

    let leave (t: TerminalState) =
        t.Writer.Write(Ansi.disable t.Capabilities)

    let logCapabilities (t: TerminalState) (log: string -> unit) =
        log $"capabilities: {TerminalCapabilities.toLogString t.Capabilities}"

    // -----------------------------------------------------------------------
    // Startup capability query: DA1 + DA2
    // -----------------------------------------------------------------------

    let detectCapabilities (t: TerminalState) : TerminalCapabilities =
        let writer = t.Writer
        let timeout = TimeSpan.FromMilliseconds 500.0

        // Send DA1 and DA2 queries.
        writer.Write("\u001b[c\u001b[>0c")
        writer.Flush()

        let mutable da1: int list option = None
        let mutable da2: (int * int * int) option = None
        let sw = System.Diagnostics.Stopwatch.StartNew()

        while (Option.isNone da1 || Option.isNone da2) && sw.Elapsed < timeout do
            if Console.KeyAvailable then
                let keyInfo = Console.ReadKey true

                if keyInfo.Key = ConsoleKey.Escape then
                    let sb = StringBuilder()
                    sb.Append '\u001b' |> ignore

                    let mutable terminated = false
                    let mutable guard = 0

                    while not terminated && guard < 64 do
                        if Console.KeyAvailable then
                            let c = Console.ReadKey true
                            sb.Append c.KeyChar |> ignore
                            let s = sb.ToString()

                            if s.EndsWith("c", StringComparison.Ordinal) then
                                terminated <- true

                                match TerminalCapabilities.parseDa1Response s with
                                | Some r -> da1 <- Some r
                                | None ->
                                    match TerminalCapabilities.parseDa2Response s with
                                    | Some r -> da2 <- Some r
                                    | None -> ()
                        else
                            Threading.Thread.Sleep 10

                        guard <- guard + 1

        let nextCaps = TerminalCapabilities.fromQueries da1 da2

        // Replace capabilities in the terminal state.
        t.Capabilities <- nextCaps
        nextCaps

    // -----------------------------------------------------------------------
    // Output: delegates to the pure Renderer module.
    // -----------------------------------------------------------------------

    let writeFrame (t: TerminalState) (screen: Screen) =
        Renderer.render t.Writer t.Capabilities.ColorSupport t.PreviousFrame screen
        t.PreviousFrame <- ValueSome screen

    // -----------------------------------------------------------------------
    // Input: read one key or escape sequence and turn it into a domain event.
    // -----------------------------------------------------------------------

    let private hasPendingInput (t: TerminalState) =
        t.PendingKeys.Count > 0 || Console.KeyAvailable

    let private dequeueOrRead (t: TerminalState) =
        if t.PendingKeys.Count > 0 then
            t.PendingKeys.Dequeue()
        else
            Console.ReadKey true

    let private replayKeys (t: TerminalState) (keys: ResizeArray<ConsoleKeyInfo>) =
        keys |> Seq.iter t.PendingKeys.Enqueue

    /// Drain the remainder of an ESC-initiated sequence from pending input.
    /// The caller has already read the ESC key and verified more input is
    /// available. Returns the full sequence text (ESC included), the keys
    /// consumed after the ESC (for replay), and whether the sequence reached
    /// a recognized terminator.
    let private drainEscape (t: TerminalState) : string * ResizeArray<ConsoleKeyInfo> * bool =
        let consumed = ResizeArray<ConsoleKeyInfo>()
        let sb = StringBuilder()
        sb.Append '\u001b' |> ignore

        let consume () =
            let c = dequeueOrRead t
            consumed.Add c
            sb.Append c.KeyChar |> ignore
            c

        let first = consume ()
        let mutable terminated = false

        if first.KeyChar = '[' then
            let mutable guard = 0

            while not terminated && hasPendingInput t && guard < 64 do
                let c = consume ()

                if c.KeyChar >= '@' && c.KeyChar <= '~' then
                    terminated <- true

                guard <- guard + 1
        elif first.KeyChar = 'O' && hasPendingInput t then
            let second = consume ()

            if Char.IsDigit second.KeyChar && hasPendingInput t then
                consume () |> ignore

            terminated <- true
        else
            terminated <- true

        sb.ToString(), consumed, terminated

    /// Soft cap on a single paste payload (8 MiB). Beyond it the accumulated
    /// text is flushed as one `Paste` event and accumulation restarts, so a
    /// runaway (or end-marker-less) paste degrades into chunked Paste events
    /// instead of unbounded memory growth.
    let private pasteSoftCap = 8 * 1024 * 1024

    let private pasteEndMarker = "\u001b" + PasteEvents.pasteEnd

    /// Consume input into the active bracketed-paste payload. Plain keys
    /// append verbatim; an ESC run that decodes to the ESC[201~ end marker
    /// finishes the paste; any other complete ESC run is appended VERBATIM
    /// to the payload (a terminal report inside a paste is payload, never
    /// input). An incomplete run that could still become the end marker is
    /// re-queued so the next call resumes it. Returns `None` and KEEPS the
    /// accumulator when input dries up mid-paste.
    let private readPasteTail (t: TerminalState) : TerminalEvent option =
        match t.PasteAccumulator with
        | None -> None
        | Some accumulator ->
            let mutable sb = accumulator
            let mutable result: TerminalEvent option = None
            let mutable finished = false

            while not finished && hasPendingInput t do
                let keyInfo = dequeueOrRead t

                if keyInfo.Key = ConsoleKey.Escape then
                    if hasPendingInput t then
                        let sequence, consumed, terminated = drainEscape t

                        if terminated && sequence = pasteEndMarker then
                            result <- Some(TerminalEvent.Paste(sb.ToString()))
                            t.PasteAccumulator <- None
                            finished <- true
                        elif not terminated && pasteEndMarker.StartsWith(sequence, StringComparison.Ordinal) then
                            // Possibly a split end marker: put the run back
                            // (input dried up, so the queue is empty and
                            // order is preserved) and resume next call.
                            t.PendingKeys.Enqueue keyInfo
                            replayKeys t consumed
                            finished <- true
                        else
                            // Any other ESC run - complete or not - is paste
                            // payload; it must never replay as input.
                            sb.Append sequence |> ignore
                    else
                        // Lone ESC at the end of available input: it may be
                        // the start of the end marker. Put it back (queue is
                        // empty here) and resume on the next call.
                        t.PendingKeys.Enqueue keyInfo
                        finished <- true
                else
                    sb.Append keyInfo.KeyChar |> ignore

                if not finished && sb.Length > pasteSoftCap then
                    // Chunked paste: flush what we have and keep going.
                    result <- Some(TerminalEvent.Paste(sb.ToString()))
                    sb <- StringBuilder()
                    t.PasteAccumulator <- Some sb
                    finished <- true

            result

    /// Read a single event from the terminal, if one is available.
    /// Returns `None` when no input is waiting.
    let tryReadEvent (t: TerminalState) : TerminalEvent option =
        // A paste in flight resumes first: every byte belongs to the payload
        // until the end marker arrives.
        if t.PasteAccumulator.IsSome then
            readPasteTail t
        elif not (hasPendingInput t) then
            None
        else
            let keyInfo = dequeueOrRead t

            // Drain a complete escape sequence when the first byte is ESC
            // and more bytes are immediately available.
            if keyInfo.Key = ConsoleKey.Escape && hasPendingInput t then
                let sequence, consumed, terminated = drainEscape t

                if terminated then
                    match Input.classifyEscapeSequence t.Capabilities.MouseEncoding sequence with
                    | Some(Input.EscapeSequence.MouseEvent event) -> Some(TerminalEvent.MouseEvent event)
                    | Some Input.EscapeSequence.MouseIgnored -> None
                    | Some(Input.EscapeSequence.Chord chord) -> Some(TerminalEvent.KeyEvent chord)
                    | Some Input.EscapeSequence.FocusGained -> Some TerminalEvent.FocusIn
                    | Some Input.EscapeSequence.FocusLost -> Some TerminalEvent.FocusOut
                    | Some Input.EscapeSequence.PasteBegin ->
                        t.PasteAccumulator <- Some(StringBuilder())
                        readPasteTail t
                    | Some Input.EscapeSequence.PasteEnd ->
                        // Stray end marker with no paste in flight: swallow.
                        None
                    | None when sequence.Length >= 2 && (sequence[1] = '[' || sequence[1] = 'O') ->
                        // Unknown but COMPLETE CSI/SS3 report (cursor position,
                        // DA responses, exotic keys): swallow it. Replaying
                        // would type the report's bytes into the buffer.
                        None
                    | None ->
                        // Not CSI/SS3 (e.g. ESC + control char): keep the
                        // legacy behavior - replay and surface the Escape.
                        replayKeys t consumed
                        Input.tryMap keyInfo |> Option.map TerminalEvent.KeyEvent
                else
                    // Incomplete drain (input dried up mid-sequence): keep
                    // the legacy replay/Escape behavior.
                    replayKeys t consumed
                    Input.tryMap keyInfo |> Option.map TerminalEvent.KeyEvent
            else
                Input.tryMap keyInfo |> Option.map TerminalEvent.KeyEvent
