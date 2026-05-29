namespace Fedit

open System

[<RequireQualifiedAccess>]
module Input =
    let private hasModifier modifier (keyInfo: ConsoleKeyInfo) = keyInfo.Modifiers.HasFlag modifier

    let tryMap (keyInfo: ConsoleKeyInfo) : Chord option =
        let hasAlt = hasModifier ConsoleModifiers.Alt keyInfo
        let hasCtrl = hasModifier ConsoleModifiers.Control keyInfo
        let hasShift = hasModifier ConsoleModifiers.Shift keyInfo

        // Structural keys map to Named regardless of modifiers; the modifier
        // set is carried verbatim (Shift is NOT dropped under Ctrl).
        let named =
            match keyInfo.Key with
            | ConsoleKey.Enter -> Some Enter
            | ConsoleKey.Escape -> Some Escape
            | ConsoleKey.Backspace -> Some Backspace
            | ConsoleKey.Delete -> Some Delete
            | ConsoleKey.Tab -> Some Tab
            | ConsoleKey.LeftArrow -> Some Left
            | ConsoleKey.RightArrow -> Some Right
            | ConsoleKey.UpArrow -> Some Up
            | ConsoleKey.DownArrow -> Some Down
            | ConsoleKey.Home -> Some Home
            | ConsoleKey.End -> Some End
            | ConsoleKey.PageUp -> Some PageUp
            | ConsoleKey.PageDown -> Some PageDown
            | ConsoleKey.Spacebar -> Some Space
            | _ -> None

        let mods =
            [ if hasCtrl then
                  Ctrl
              if hasAlt then
                  Alt
              if hasShift then
                  Shift ]
            |> Set.ofList

        // Function keys F1..F24 are a contiguous ConsoleKey range.
        let fnKey =
            if keyInfo.Key >= ConsoleKey.F1 && keyInfo.Key <= ConsoleKey.F24 then
                Some(int keyInfo.Key - int ConsoleKey.F1 + 1)
            else
                None

        match named, fnKey with
        | Some n, _ -> Some { Mods = mods; Key = Named n }
        | None, Some n -> Some { Mods = mods; Key = Fn n }
        | None, None ->
            if hasCtrl || hasAlt then
                // Ctrl/Alt + letter: lowercase the letter, keep Shift in Mods
                // so Ctrl+Shift+P is distinct from Ctrl+P. Use the ConsoleKey
                // letter (KeyChar is often NUL/control under Ctrl).
                //
                // ConsoleKey.D0..D9 form a contiguous range; subtract D0 to
                // recover the digit (used for Ctrl+digit buffer jumps). macOS
                // Terminal.app may not produce these — see docs/wip-keybinds.md.
                let baseChar =
                    if keyInfo.Key >= ConsoleKey.A && keyInfo.Key <= ConsoleKey.Z then
                        Some(char (int 'a' + (int keyInfo.Key - int ConsoleKey.A)))
                    elif keyInfo.Key >= ConsoleKey.D0 && keyInfo.Key <= ConsoleKey.D9 then
                        Some(char (int '0' + (int keyInfo.Key - int ConsoleKey.D0)))
                    elif not (Char.IsControl keyInfo.KeyChar) then
                        Some(Char.ToLowerInvariant keyInfo.KeyChar)
                    else
                        None

                baseChar |> Option.map (fun c -> { Mods = mods; Key = Key.Char c })
            else if
                // Bare printable key: Shift lives in the character itself
                // (A vs a), NOT in Mods. This is the text fast-path.
                Char.IsControl keyInfo.KeyChar
            then
                None
            else
                Some
                    { Mods = Set.empty
                      Key = Key.Char keyInfo.KeyChar }

    /// Decode an SGR mouse report body (`"[<Cb;Cx;Cy"` followed by `'M'`/`'m'`)
    /// into a signed wheel-tick count: `Some -1` for wheel-up, `Some 1` for
    /// wheel-down, `None` for clicks, drags, the horizontal wheel, or garbage.
    /// Modifier bits (shift 4 / meta 8 / ctrl 16) are masked off.
    let parseSgrMouse (sequence: string) : int option =
        if
            sequence.Length >= 4
            && sequence.StartsWith("[<", StringComparison.Ordinal)
            && (sequence.EndsWith("M", StringComparison.Ordinal)
                || sequence.EndsWith("m", StringComparison.Ordinal))
        then
            let body = sequence.Substring(2, sequence.Length - 3)

            match body.Split(';') with
            | [| code; _; _ |] ->
                match Int32.TryParse code with
                | true, value ->
                    match value &&& 0b1100_0011 with
                    | 64 -> Some -1
                    | 65 -> Some 1
                    | _ -> None
                | false, _ -> None
            | _ -> None
        else
            None
