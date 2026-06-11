namespace Fedit

open System

[<RequireQualifiedAccess>]
module Input =
    let private hasModifier modifier (keyInfo: ConsoleKeyInfo) = keyInfo.Modifiers.HasFlag modifier

    let private escape = '\u001b'

    type EscapeSequence =
        | Chord of Fedit.Chord
        | MouseEvent of MouseEvent
        | MouseIgnored
        | FocusGained
        | FocusLost
        | PasteBegin
        | PasteEnd

    let private modifierSet (value: int) : Set<Modifier> =
        // XTerm / CSI-u modifier values are 1-based bitmasks:
        //   2=Shift, 3=Alt, 5=Ctrl, 9=Super/Meta, and combinations.
        // See the raw `ESC [ 1 ; 10 D` shape in temp.log: 10 = Shift+Super.
        let mask = max 0 (value - 1)

        [ if mask &&& 0b0100 <> 0 then
              Ctrl
          if mask &&& 0b0010 <> 0 then
              Alt
          if mask &&& 0b0001 <> 0 then
              Shift
          if mask &&& 0b1000 <> 0 then
              Super ]
        |> Set.ofList

    let private tryParseInt (text: string) =
        match Int32.TryParse text with
        | true, value -> Some value
        | false, _ -> None

    let private splitParams (body: string) =
        if String.IsNullOrEmpty body then
            []
        else
            body.Split(';') |> Array.toList

    let private tryNamedFinal =
        function
        | 'A' -> Some Up
        | 'B' -> Some Down
        | 'C' -> Some Right
        | 'D' -> Some Left
        | 'H' -> Some Home
        | 'F' -> Some End
        | _ -> None

    let private tryTildeKey =
        function
        | 1
        | 7 -> Some Home
        | 4
        | 8 -> Some End
        | 3 -> Some Delete
        | 5 -> Some PageUp
        | 6 -> Some PageDown
        | _ -> None

    let private tryCharKey (codepoint: int) =
        match codepoint with
        | 9 -> Some(Named Tab)
        | 13 -> Some(Named Enter)
        | 27 -> Some(Named Escape)
        | 32 -> Some(Named Space)
        | 127 -> Some(Named Backspace)
        | value when value >= 32 && value <= 0xFFFF -> Some(Key.Char(char value))
        | _ -> None

    let private normalizeModifiedChar (mods: Set<Modifier>) (key: Key) =
        match key with
        | Key.Char c when not mods.IsEmpty -> Key.Char(Char.ToLowerInvariant c)
        | _ -> key

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
        | None, None when hasAlt && not hasCtrl && keyInfo.Key = ConsoleKey.B ->
            // Some terminals/.NET builds surface Option+Left as Alt+b. Treat
            // that as the intended arrow chord so the keymap stays bound to
            // Opt/Alt+Left, not to a separate Emacs/Vim-style Alt+b binding.
            Some { Mods = mods; Key = Named Left }
        | None, None when hasAlt && not hasCtrl && keyInfo.Key = ConsoleKey.F ->
            // See the Alt+b compatibility note above for Option+Right.
            Some { Mods = mods; Key = Named Right }
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

    /// Decode a raw terminal escape sequence that `Console.ReadKey` did not
    /// recognize. This covers modified arrows (`ESC [ 1 ; 5 D`), Super/Command
    /// arrows (`ESC [ 1 ; 9 D` / `...;10D`), CSI-u reports such as
    /// Ctrl+1 (`ESC [ 49 ; 5 u`), and focus events (`ESC [ I` / `ESC [ O`).
    /// The runtime still uses `tryMap` first for keys the BCL decodes natively.
    let tryParseAnsiSequence (sequence: string) : Chord option =
        if String.IsNullOrEmpty sequence || sequence[0] <> escape then
            None
        elif sequence.Length = 2 && sequence[1] <> '[' && sequence[1] <> 'O' then
            // ESC-prefixed printable key: traditional Alt/Meta encoding
            // (e.g. Terminal.app Option+Left often sends ESC b).
            let c = sequence[1]

            match Char.ToLowerInvariant c with
            | 'b' ->
                Some
                    { Mods = Set.ofList [ Alt ]
                      Key = Named Left }
            | 'f' ->
                Some
                    { Mods = Set.ofList [ Alt ]
                      Key = Named Right }
            | value when Char.IsControl value -> None
            | value ->
                Some
                    { Mods = Set.ofList [ Alt ]
                      Key = Key.Char value }
        elif sequence.Length >= 3 && sequence[1] = '[' then
            let final = sequence[sequence.Length - 1]
            let body = sequence.Substring(2, sequence.Length - 3)
            let parts = splitParams body

            // Focus events are CSI I / CSI O with no parameters.
            // We handle them in classifyEscapeSequence; here we just reject
            // them so they don't fall through to the generic Chord path.
            if final = 'I' || final = 'O' then
                None
            else
                match final with
                | 'A'
                | 'B'
                | 'C'
                | 'D'
                | 'H'
                | 'F' ->
                    tryNamedFinal final
                    |> Option.map (fun named ->
                        let mods =
                            parts
                            |> List.rev
                            |> List.tryPick tryParseInt
                            |> Option.filter (fun value -> value > 1)
                            |> Option.map modifierSet
                            |> Option.defaultValue Set.empty

                        { Mods = mods; Key = Named named })
                | '~' ->
                    match parts with
                    | head :: rest ->
                        match tryParseInt head |> Option.bind tryTildeKey with
                        | Some named ->
                            let mods =
                                rest
                                |> List.tryPick tryParseInt
                                |> Option.filter (fun value -> value > 1)
                                |> Option.map modifierSet
                                |> Option.defaultValue Set.empty

                            Some { Mods = mods; Key = Named named }
                        | None -> None
                    | [] -> None
                | 'u' ->
                    match parts with
                    | code :: rest ->
                        match tryParseInt code with
                        | Some codepoint ->
                            let mods =
                                rest
                                |> List.tryPick tryParseInt
                                |> Option.filter (fun value -> value > 1)
                                |> Option.map modifierSet
                                |> Option.defaultValue Set.empty

                            tryCharKey codepoint
                            |> Option.map (normalizeModifiedChar mods)
                            |> Option.map (fun key -> { Mods = mods; Key = key })
                        | None -> None
                    | [] -> None
                | _ -> None
        elif sequence.Length >= 3 && sequence[1] = 'O' then
            let final = sequence[sequence.Length - 1]

            tryNamedFinal final
            |> Option.map (fun named ->
                let mods =
                    if sequence.Length = 4 then
                        match tryParseInt (string sequence[2]) with
                        | Some value when value > 1 -> modifierSet value
                        | _ -> Set.empty
                    else
                        Set.empty

                { Mods = mods; Key = Named named })
        else
            None

    let classifyEscapeSequence (mouseEncoding: MouseEncoding) (sequence: string) : EscapeSequence option =
        if String.IsNullOrEmpty sequence || sequence[0] <> escape then
            None
        else
            // Helper conventions differ: `FocusEvents` constants are full
            // ESC-prefixed sequences, while the `MouseProtocol` parsers and
            // `PasteEvents` markers expect the body with the ESC stripped.
            let body = sequence.Substring 1

            // Focus events: very short sequences, check first.
            if FocusEvents.isFocusIn sequence then
                Some FocusGained
            elif FocusEvents.isFocusOut sequence then
                Some FocusLost
            // Bracketed-paste markers (DECSET 2004); the terminal layer
            // accumulates the payload between Begin and End.
            elif PasteEvents.isBegin body then
                Some PasteBegin
            elif PasteEvents.isEnd body then
                Some PasteEnd
            // Mouse events: try the configured encoding first, then SGR as fallback.
            elif MouseProtocol.isMouseSequence mouseEncoding body then
                MouseProtocol.tryParse mouseEncoding body
                |> Option.map MouseEvent
                |> Option.orElse (Some MouseIgnored)
            elif MouseProtocol.isMouseSequence MouseSgr body then
                MouseProtocol.tryParse MouseSgr body
                |> Option.map MouseEvent
                |> Option.orElse (Some MouseIgnored)
            else
                tryParseAnsiSequence sequence |> Option.map Chord
