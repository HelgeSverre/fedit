module Fedit.Tests.InputTests

open System
open Fedit
open Xunit
open FsUnit.Xunit

/// Small constructor for ConsoleKeyInfo — the full signature takes five
/// args and the call site is otherwise noisy.
let private keyInfo (ch: char) (key: ConsoleKey) (shift: bool) (alt: bool) (ctrl: bool) =
    ConsoleKeyInfo(ch, key, shift, alt, ctrl)

let private chord mods key : Chord = { Mods = Set.ofList mods; Key = key }

// --- Structural keys + modifiers (no dropped Shift under Ctrl) ---

[<Fact>]
let ``Ctrl+PageUp parses to ctrl+pageup`` () =
    let info = keyInfo ' ' ConsoleKey.PageUp false false true
    Input.tryMap info |> should equal (Some(chord [ Ctrl ] (Named PageUp)))

[<Fact>]
let ``Ctrl+PageDown parses to ctrl+pagedown`` () =
    let info = keyInfo ' ' ConsoleKey.PageDown false false true
    Input.tryMap info |> should equal (Some(chord [ Ctrl ] (Named PageDown)))

[<Fact>]
let ``Plain PageUp parses to a bare named key`` () =
    let info = keyInfo ' ' ConsoleKey.PageUp false false false
    Input.tryMap info |> should equal (Some(chord [] (Named PageUp)))

[<Fact>]
let ``Ctrl+Left decodes with Ctrl in the modifier set (wip #8)`` () =
    let info = keyInfo ' ' ConsoleKey.LeftArrow false false true
    Input.tryMap info |> should equal (Some(chord [ Ctrl ] (Named Left)))

[<Fact>]
let ``Shift+Tab keeps Shift in the modifier set`` () =
    let info = keyInfo '\t' ConsoleKey.Tab true false false
    Input.tryMap info |> should equal (Some(chord [ Shift ] (Named Tab)))

[<Fact>]
let ``Spacebar maps to the named Space key, not Char space`` () =
    // The whole prompt/editor/sidebar text-insert path keys off this: the
    // spacebar is a structural Named key, so every fallthrough that inserts
    // text must special-case it (regression guard for the ":theme " bug).
    let info = keyInfo ' ' ConsoleKey.Spacebar false false false
    Input.tryMap info |> should equal (Some(chord [] (Named Space)))

// --- Ctrl/Alt + character (case-folded, Shift preserved) ---

[<Fact>]
let ``Ctrl+S and Ctrl+s both fold to ctrl+s`` () =
    let upper = keyInfo '\000' ConsoleKey.S true false true
    let lower = keyInfo '\000' ConsoleKey.S false false true
    Input.tryMap lower |> should equal (Some(chord [ Ctrl ] (Key.Char 's')))
    // Shift is preserved in Mods, so the cased press is distinct from bare.
    Input.tryMap upper |> should equal (Some(chord [ Ctrl; Shift ] (Key.Char 's')))

[<Fact>]
let ``Ctrl+Shift+P is distinct from Ctrl+P (audit #5 fix)`` () =
    let ctrlShift = keyInfo '\000' ConsoleKey.P true false true
    let ctrl = keyInfo '\000' ConsoleKey.P false false true

    Input.tryMap ctrlShift
    |> should equal (Some(chord [ Ctrl; Shift ] (Key.Char 'p')))

    Input.tryMap ctrl |> should equal (Some(chord [ Ctrl ] (Key.Char 'p')))
    Input.tryMap ctrlShift |> should not' (equal (Input.tryMap ctrl))

[<Fact>]
let ``Ctrl+O decodes (wip #4)`` () =
    let info = keyInfo '\000' ConsoleKey.O false false true
    Input.tryMap info |> should equal (Some(chord [ Ctrl ] (Key.Char 'o')))

[<Fact>]
let ``Ctrl+1 parses to ctrl+'1'`` () =
    let info = keyInfo ' ' ConsoleKey.D1 false false true
    Input.tryMap info |> should equal (Some(chord [ Ctrl ] (Key.Char '1')))

[<Fact>]
let ``Alt+B compatibility maps to Alt+Left word-motion chord`` () =
    let info = keyInfo 'b' ConsoleKey.B false true false
    Input.tryMap info |> should equal (Some(chord [ Alt ] (Named Left)))

// --- Function keys ---

[<Fact>]
let ``F5 parses to Fn 5`` () =
    let info = keyInfo '\000' ConsoleKey.F5 false false false
    Input.tryMap info |> should equal (Some(chord [] (Fn 5)))

[<Fact>]
let ``Shift+F3 keeps Shift in the modifier set`` () =
    let info = keyInfo '\000' ConsoleKey.F3 true false false
    Input.tryMap info |> should equal (Some(chord [ Shift ] (Fn 3)))

// --- Bare printable text fast-path (Shift lives in the char, not Mods) ---

[<Fact>]
let ``bare lowercase letter parses to a no-modifier Char`` () =
    let info = keyInfo 'a' ConsoleKey.A false false false
    Input.tryMap info |> should equal (Some(chord [] (Key.Char 'a')))

[<Fact>]
let ``bare capital letter carries case in the char, not Mods`` () =
    let info = keyInfo 'A' ConsoleKey.A true false false
    Input.tryMap info |> should equal (Some(chord [] (Key.Char 'A')))

[<Fact>]
let ``plain digit (no Ctrl) parses to a bare Char`` () =
    let info = keyInfo '5' ConsoleKey.D5 false false false
    Input.tryMap info |> should equal (Some(chord [] (Key.Char '5')))

// --- Raw ANSI escape parsing for sequences Console.ReadKey leaves split ---

[<Fact>]
let ``CSI modified Left decodes Ctrl modifier`` () =
    Input.tryParseAnsiSequence "\u001b[1;5D"
    |> should equal (Some(chord [ Ctrl ] (Named Left)))

[<Fact>]
let ``CSI modified Left decodes Shift plus Super modifier`` () =
    Input.tryParseAnsiSequence "\u001b[1;10D"
    |> should equal (Some(chord [ Shift; Super ] (Named Left)))

[<Fact>]
let ``CSI-u decodes Ctrl+digit for buffer jumps`` () =
    Input.tryParseAnsiSequence "\u001b[49;5u"
    |> should equal (Some(chord [ Ctrl ] (Key.Char '1')))

[<Fact>]
let ``ESC b compatibility maps to Alt+Left word-motion chord`` () =
    Input.tryParseAnsiSequence "\u001bb"
    |> should equal (Some(chord [ Alt ] (Named Left)))

[<Fact>]
let ``generic ESC-prefixed printable key still decodes as Alt key`` () =
    Input.tryParseAnsiSequence "\u001bx"
    |> should equal (Some(chord [ Alt ] (Key.Char 'x')))

[<Fact>]
let ``classifyEscapeSequence routes SGR mouse before keyboard parsing`` () =
    Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[<65;10;5M"
    |> Option.map (function
        | Input.EscapeSequence.MouseEvent e -> Some e
        | _ -> None)
    |> Option.flatten
    |> Option.isSome
    |> should equal true

[<Fact>]
let ``classifyEscapeSequence treats Ghostty shift wheel as handled mouse input`` () =
    match Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[<71;151;42M" with
    | Some(Input.EscapeSequence.Chord _) -> failwith "SGR mouse reports must not be decoded as keyboard chords"
    | Some _ -> ()
    | None -> failwith "valid SGR mouse reports must be handled so their bytes are not replayed into the buffer"

[<Fact>]
let ``classifyEscapeSequence routes CSI-u key reports to chords`` () =
    Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[49;5u"
    |> should equal (Some(Input.EscapeSequence.Chord(chord [ Ctrl ] (Key.Char '1'))))

// --- SGR mouse wheel parsing ("[<Cb;Cx;Cy" + 'M'/'m') — unchanged ---

[<Fact>]
let ``MouseProtocol tryParseSgr decodes wheel up to scroll event`` () =
    let event = MouseProtocol.tryParseSgr "[<64;10;5M"
    event |> Option.isSome |> should equal true
    event.Value.Button |> should equal ScrollUp
    event.Value.Action |> should equal Press

[<Fact>]
let ``MouseProtocol tryParseSgr decodes wheel down to scroll event`` () =
    let event = MouseProtocol.tryParseSgr "[<65;10;5M"
    event |> Option.isSome |> should equal true
    event.Value.Button |> should equal ScrollDown
    event.Value.Action |> should equal Press

[<Fact>]
let ``MouseProtocol tryParseSgr ignores modifier bits on the wheel code`` () =
    // 64 + shift(4) = 68 is still wheel-up
    let event = MouseProtocol.tryParseSgr "[<68;1;1M"
    event |> Option.isSome |> should equal true
    event.Value.Button |> should equal ScrollUp

[<Fact>]
let ``MouseProtocol tryParseSgr decodes a plain button press`` () =
    let event = MouseProtocol.tryParseSgr "[<0;10;5M"
    event |> Option.isSome |> should equal true
    event.Value.Button |> should equal LeftButton
    event.Value.Action |> should equal Press

[<Fact>]
let ``MouseProtocol tryParseSgr decodes horizontal wheel codes`` () =
    // 66/67 are the conventional SGR horizontal-wheel codes (64+2 / 64+3).
    let left = MouseProtocol.tryParseSgr "[<66;10;5M"
    left |> Option.isSome |> should equal true
    left.Value.Button |> should equal ScrollLeft
    left.Value.Action |> should equal Press

    let right = MouseProtocol.tryParseSgr "[<67;10;5M"
    right |> Option.isSome |> should equal true
    right.Value.Button |> should equal ScrollRight
    right.Value.Action |> should equal Press

[<Fact>]
let ``MouseProtocol tryParseSgr keeps modifier bits on horizontal wheel`` () =
    // 66 + shift(4) = 70 is still wheel-left, with Shift in the modifiers.
    let event = MouseProtocol.tryParseSgr "[<70;1;1M"
    event |> Option.isSome |> should equal true
    event.Value.Button |> should equal ScrollLeft
    event.Value.Modifiers |> should equal (Set.ofList [ Shift ])

[<Fact>]
let ``MouseProtocol toWheelTicks ignores horizontal wheel events`` () =
    // Horizontal wheel must not scroll vertically. Without a tick mapping
    // the press flows to MousePressed, where the editor matches LeftButton
    // only — a harmless no-op.
    let wheelEvent button : MouseEvent =
        { Button = button
          Action = Press
          Position = { Line = 0; Column = 0 }
          Modifiers = Set.empty }

    MouseProtocol.toWheelTicks (wheelEvent ScrollLeft) |> should equal None
    MouseProtocol.toWheelTicks (wheelEvent ScrollRight) |> should equal None
    // Vertical mapping is unchanged.
    MouseProtocol.toWheelTicks (wheelEvent ScrollUp) |> should equal (Some -1)
    MouseProtocol.toWheelTicks (wheelEvent ScrollDown) |> should equal (Some 1)

[<Fact>]
let ``MouseProtocol tryParseSgr rejects buttons with the high bit`` () =
    // Codes 128+ are the extended buttons 8-11 (browser back/forward etc.);
    // without the guard they misdecode as left/middle/right via `&&& 3`.
    MouseProtocol.tryParseSgr "[<128;10;5M" |> should equal None
    MouseProtocol.tryParseSgr "[<129;10;5M" |> should equal None

[<Fact>]
let ``MouseProtocol tryParseSgr rejects malformed input`` () =
    MouseProtocol.tryParseSgr "garbage" |> should equal None

// --- classifyEscapeSequence: focus events + bracketed-paste markers ---

[<Fact>]
let ``classifyEscapeSequence decodes focus in and out`` () =
    Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[I"
    |> should equal (Some Input.EscapeSequence.FocusGained)

    Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[O"
    |> should equal (Some Input.EscapeSequence.FocusLost)

[<Fact>]
let ``classifyEscapeSequence decodes bracketed paste markers`` () =
    Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[200~"
    |> should equal (Some Input.EscapeSequence.PasteBegin)

    Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[201~"
    |> should equal (Some Input.EscapeSequence.PasteEnd)

[<Fact>]
let ``classifyEscapeSequence swallows high-bit mouse buttons as handled input`` () =
    // The SGR shape still parses, so the report is consumed (MouseIgnored)
    // rather than replayed into the buffer as text.
    Input.classifyEscapeSequence MouseEncoding.MouseSgr "\u001b[<128;10;5M"
    |> should equal (Some Input.EscapeSequence.MouseIgnored)

// --- Terminal.tryReadEvent over a preloaded PendingKeys queue. Console is
// never consulted: hasPendingInput short-circuits on the queue count, and
// each scenario completes its event exactly when the queue empties. ---

let private terminalWithKeys (text: string) =
    let writer = new IO.StringWriter()
    let term = Terminal.createWithCapabilities writer TerminalCapabilities.modern

    for ch in text do
        let key =
            match ch with
            | '\u001b' -> ConsoleKey.Escape
            | '\r' -> ConsoleKey.Enter
            | c when c >= 'a' && c <= 'z' -> enum<ConsoleKey> (int ConsoleKey.A + int c - int 'a')
            | _ -> enum<ConsoleKey> 0

        term.PendingKeys.Enqueue(ConsoleKeyInfo(ch, key, false, false, false))

    term

[<Fact>]
let ``tryReadEvent accumulates a bracketed paste into one event`` () =
    let term = terminalWithKeys "\u001b[200~ab\rc\u001b[201~"

    Terminal.tryReadEvent term |> should equal (Some(TerminalEvent.Paste "ab\rc"))

    term.PendingKeys.Count |> should equal 0
    term.PasteAccumulator |> should equal (None: Text.StringBuilder option)

[<Fact>]
let ``tryReadEvent swallows an unknown complete CSI without replaying`` () =
    let term = terminalWithKeys "\u001b[1;2Xa"

    // First read consumes and swallows the unknown report.
    Terminal.tryReadEvent term |> should equal (None: TerminalEvent option)
    term.PendingKeys.Count |> should equal 1

    // Second read sees the trailing 'a' as an ordinary key event.
    Terminal.tryReadEvent term
    |> should equal (Some(TerminalEvent.KeyEvent { Mods = Set.empty; Key = Key.Char 'a' }))

    term.PendingKeys.Count |> should equal 0
