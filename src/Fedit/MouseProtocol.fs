namespace Fedit

open System

/// Statically-known escape sequences for focus events.
[<RequireQualifiedAccess>]
module FocusEvents =
    let focusIn = "\u001b[I"
    let focusOut = "\u001b[O"
    let isFocusIn (sequence: string) = sequence = focusIn
    let isFocusOut (sequence: string) = sequence = focusOut

/// Mouse event parsing for the three common encodings.
/// All functions are total and pure (string → option).
/// Operates on domain types from Events.fs — no protocol-specific
/// types leak out of this module.
[<RequireQualifiedAccess>]
module MouseProtocol =

    // -----------------------------------------------------------------------
    // SGR encoding (DECSET 1006) — modern standard, unambiguous.
    // Format: CSI < Cb ; Cx ; Cy M   (press)
    //         CSI < Cb ; Cx ; Cy m   (release)
    // -----------------------------------------------------------------------

    let private tryParseSgrParts (sequence: string) : (int * int * int * bool) option =
        // sequence comes in WITHOUT the leading ESC; it starts with "[<"
        if sequence.Length < 4 then
            None
        elif not (sequence.StartsWith("[<", StringComparison.Ordinal)) then
            None
        else
            let isRelease = sequence.EndsWith("m", StringComparison.Ordinal)
            let isPress = sequence.EndsWith("M", StringComparison.Ordinal)

            if not isPress && not isRelease then
                None
            else
                let body = sequence.Substring(2, sequence.Length - 3)
                let parts = body.Split(';')

                if parts.Length <> 3 then
                    None
                else
                    match Int32.TryParse parts[0], Int32.TryParse parts[1], Int32.TryParse parts[2] with
                    | (true, code), (true, x), (true, y) ->
                        // SGR coordinates are 1-based.
                        Some(code, x - 1, y - 1, isRelease)
                    | _ -> None

    let private decodeModifiers (code: int) : Set<Modifier> =
        [ if code &&& 4 <> 0 then
              Shift
          if code &&& 8 <> 0 then
              Alt
          if code &&& 16 <> 0 then
              Ctrl ]
        |> Set.ofList

    let private decodeSgrButton (code: int) : (MouseButton * MouseAction) option =
        let motion = (code &&& 32) <> 0
        let wheel = (code &&& 64) <> 0
        let btn = code &&& 3

        if wheel then
            // Wheel events are instantaneous (no separate release).
            match code &&& 0b1100_0011 with
            | 64 -> Some(ScrollUp, Press)
            | 65 -> Some(ScrollDown, Press)
            | 96 -> Some(ScrollLeft, Press)
            | 97 -> Some(ScrollRight, Press)
            | _ -> None
        elif motion then
            match btn with
            | 0 -> Some(LeftButton, Drag)
            | 1 -> Some(MiddleButton, Drag)
            | 2 -> Some(RightButton, Drag)
            | _ -> None
        else
            match btn with
            | 0 -> Some(LeftButton, Press)
            | 1 -> Some(MiddleButton, Press)
            | 2 -> Some(RightButton, Press)
            | 3 ->
                // Code 3 means "release", but we don't know which button.
                // Many applications track button state externally. For now,
                // report as Left release — the runtime can refine this if it
                // keeps a "last pressed button" variable.
                Some(LeftButton, Release)
            | _ -> None

    let tryParseSgr (sequence: string) : MouseEvent option =
        tryParseSgrParts sequence
        |> Option.bind (fun (code, x, y, isRelease) ->
            decodeSgrButton code
            |> Option.bind (fun (button, action) ->
                let action = if isRelease then Release else action

                Some
                    { Button = button
                      Action = action
                      Position = { Line = y; Column = x }
                      Modifiers = decodeModifiers code }))

    let isSgrSequence (sequence: string) : bool =
        tryParseSgrParts sequence |> Option.isSome

    // -----------------------------------------------------------------------
    // urxvt encoding (DECSET 1015) — colon-delimited, rarely needed today.
    // Format: CSI code ; x ; y M  where code = button + 32
    // -----------------------------------------------------------------------

    let tryParseUrxvt (sequence: string) : MouseEvent option =
        // urxvt: CSI code ; x ; y M
        // sequence starts with "[" (no "<")
        if sequence.Length < 4 then
            None
        elif sequence[0] <> '[' then
            None
        else
            let final = sequence[sequence.Length - 1]

            if final <> 'M' && final <> 'm' then
                None
            else
                let body = sequence.Substring(1, sequence.Length - 2)
                let parts = body.Split(';')

                if parts.Length <> 3 then
                    None
                else
                    match Int32.TryParse parts[0], Int32.TryParse parts[1], Int32.TryParse parts[2] with
                    | (true, code), (true, x), (true, y) ->
                        // urxvt codes are button + 32
                        let realCode = code - 32
                        let isRelease = final = 'm'

                        decodeSgrButton realCode
                        |> Option.bind (fun (button, action) ->
                            let action = if isRelease then Release else action

                            Some
                                { Button = button
                                  Action = action
                                  Position = { Line = y - 1; Column = x - 1 }
                                  Modifiers = decodeModifiers realCode })
                    | _ -> None

    // -----------------------------------------------------------------------
    // X10 encoding — the original, limited to 223x223 coordinates.
    // Format: CSI M Cb Cx Cy  where each param is a single char: value + 32
    // -----------------------------------------------------------------------

    let tryParseX10 (sequence: string) : MouseEvent option =
        // X10: ESC [ M Cb Cx Cy  (5 chars after ESC)
        if sequence.Length <> 6 then
            None
        elif sequence.Substring(0, 3) <> "[M" then
            None
        else
            let cb = int sequence[3] - 32
            let cx = int sequence[4] - 32
            let cy = int sequence[5] - 32

            if cx < 1 || cy < 1 then
                None
            else
                decodeSgrButton cb
                |> Option.map (fun (button, action) ->
                    { Button = button
                      Action = action
                      Position = { Line = cy - 1; Column = cx - 1 }
                      Modifiers = decodeModifiers cb })

    // -----------------------------------------------------------------------
    // Unified parser: try SGR first, then urxvt, then X10.
    // -----------------------------------------------------------------------

    let tryParse (encoding: MouseEncoding) (sequence: string) : MouseEvent option =
        match encoding with
        | MouseSgr
        | MouseSgrPixels -> tryParseSgr sequence
        | MouseUrxvt -> tryParseUrxvt sequence
        | MouseX10 -> tryParseX10 sequence
        | _ -> None

    /// True if the sequence is a mouse report in the given encoding.
    let isMouseSequence (encoding: MouseEncoding) (sequence: string) : bool =
        match encoding with
        | MouseSgr
        | MouseSgrPixels -> isSgrSequence sequence
        | MouseUrxvt -> tryParseUrxvt sequence |> Option.isSome
        | MouseX10 -> tryParseX10 sequence |> Option.isSome
        | _ -> false

    /// Extract a wheel-tick count from a mouse event, if it is a scroll wheel
    /// event. Positive = down, negative = up. Bridges the existing
    /// `MouseScrolled` message.
    let toWheelTicks (event: MouseEvent) : int option =
        match event.Button, event.Action with
        | ScrollUp, Press -> Some -1
        | ScrollDown, Press -> Some 1
        | ScrollLeft, Press -> Some -1
        | ScrollRight, Press -> Some 1
        | _ -> None
