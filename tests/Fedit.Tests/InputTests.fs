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

// --- SGR mouse wheel parsing ("[<Cb;Cx;Cy" + 'M'/'m') — unchanged ---

[<Fact>]
let ``parseSgrMouse decodes wheel up to -1`` () =
    Input.parseSgrMouse "[<64;10;5M" |> should equal (Some -1)

[<Fact>]
let ``parseSgrMouse decodes wheel down to +1`` () =
    Input.parseSgrMouse "[<65;10;5M" |> should equal (Some 1)

[<Fact>]
let ``parseSgrMouse ignores modifier bits on the wheel code`` () =
    // 64 + shift(4) = 68 is still wheel-up
    Input.parseSgrMouse "[<68;1;1M" |> should equal (Some -1)

[<Fact>]
let ``parseSgrMouse rejects a plain button press`` () =
    Input.parseSgrMouse "[<0;10;5M" |> should equal None

[<Fact>]
let ``parseSgrMouse rejects the horizontal wheel`` () =
    Input.parseSgrMouse "[<66;10;5M" |> should equal None

[<Fact>]
let ``parseSgrMouse rejects malformed input`` () =
    Input.parseSgrMouse "garbage" |> should equal None
