namespace Fedit

/// A keyboard modifier. `Super` is the Command/Win/Meta key; the editor never
/// decodes it today (terminals rarely report it), but the model carries it so
/// the keybinds-file grammar can name it without a later type change.
type Modifier =
    | Ctrl
    | Alt
    | Shift
    | Super

/// Structural keys whose meaning is layout-independent.
type NamedKey =
    | Enter
    | Escape
    | Tab
    | Backspace
    | Delete
    | Left
    | Right
    | Up
    | Down
    | Home
    | End
    | PageUp
    | PageDown
    | Space

type Key =
    /// Layout-dependent produced character (the default). For Ctrl/Alt chords
    /// this is the lowercased base letter; for bare text it is the literal
    /// character including case (so `A` vs `a` carries Shift in the char).
    | Char of char
    | Named of NamedKey
    | Fn of int // F1..F24

/// A single key event: a set of modifiers plus the key. Structural equality
/// makes it usable as a Map key and in List.tryFind.
type Chord = { Mods: Set<Modifier>; Key: Key }

/// A sequence of one or more chords (length > 1 == a multi-key sequence).
type KeyStroke = Chord list

[<RequireQualifiedAccess>]
module Chord =
    /// Build a chord from a modifier list and a key.
    let make (mods: Modifier list) (key: Key) : Chord = { Mods = Set.ofList mods; Key = key }

    let private modToken =
        function
        | Ctrl -> "ctrl"
        | Alt -> "alt"
        | Shift -> "shift"
        | Super -> "super"

    let private namedToken =
        function
        | Enter -> "enter"
        | Escape -> "esc"
        | Tab -> "tab"
        | Backspace -> "backspace"
        | Delete -> "delete"
        | Left -> "left"
        | Right -> "right"
        | Up -> "up"
        | Down -> "down"
        | Home -> "home"
        | End -> "end"
        | PageUp -> "pageup"
        | PageDown -> "pagedown"
        | Space -> "space"

    let private keyToken =
        function
        | Char c -> string c
        | Named n -> namedToken n
        | Fn n -> $"f{n}"

    /// Render a chord to a display string, e.g. `ctrl+shift+p`, `alt+left`,
    /// `f5`. Modifiers in canonical order (ctrl, alt, shift, super). Used by
    /// the status-bar pending indicator and matches the keybinds-file grammar.
    let render (chord: Chord) : string =
        let order = [ Ctrl; Alt; Shift; Super ]
        let mods = order |> List.filter chord.Mods.Contains |> List.map modToken
        String.concat "+" (mods @ [ keyToken chord.Key ])

    /// Render a whole stroke (sequence) space-separated, e.g. `ctrl+k ctrl+c`.
    let renderStroke (stroke: KeyStroke) : string =
        stroke |> List.map render |> String.concat " "

    let private parseModifier (token: string) : Modifier option =
        match token.ToLowerInvariant() with
        | "ctrl"
        | "control" -> Some Ctrl
        | "alt"
        | "opt"
        | "option" -> Some Alt
        | "shift" -> Some Shift
        | "super"
        | "cmd"
        | "command"
        | "win"
        | "meta" -> Some Super
        | _ -> None

    let private parseNamed (token: string) : NamedKey option =
        match token.ToLowerInvariant() with
        | "enter"
        | "return" -> Some Enter
        | "esc"
        | "escape" -> Some Escape
        | "tab" -> Some Tab
        | "backspace"
        | "bs" -> Some Backspace
        | "delete"
        | "del" -> Some Delete
        | "left" -> Some Left
        | "right" -> Some Right
        | "up" -> Some Up
        | "down" -> Some Down
        | "home" -> Some Home
        | "end" -> Some End
        | "pageup"
        | "pgup" -> Some PageUp
        | "pagedown"
        | "pgdn" -> Some PageDown
        | "space" -> Some Space
        | _ -> None

    let private parseKey (token: string) : Key option =
        match parseNamed token with
        | Some n -> Some(Named n)
        | None ->
            let lower = token.ToLowerInvariant()

            if
                lower.Length >= 2
                && lower[0] = 'f'
                && lower[1..] |> Seq.forall System.Char.IsDigit
            then
                match System.Int32.TryParse lower[1..] with
                | true, n when n >= 1 && n <= 24 -> Some(Fn n)
                | _ -> None
            elif token.Length = 1 then
                Some(Char(System.Char.ToLowerInvariant token[0]))
            else
                None

    /// Parse a single chord token such as `ctrl+shift+p`, `f6`, `enter`,
    /// `alt+left`. Returns `None` for malformed input. The final `+`-separated
    /// segment is the key; everything before it is a modifier. (A bare `+`
    /// key is supported by treating a trailing empty segment as the literal.)
    let parse (token: string) : Chord option =
        if System.String.IsNullOrWhiteSpace token then
            None
        else
            // Split on '+', but a trailing '+' means the key itself is '+'.
            let raw = token.Trim()

            let segments =
                if raw.EndsWith "+" && raw.Length > 1 then
                    (raw.Substring(0, raw.Length - 1).Split('+') |> Array.toList) @ [ "+" ]
                else
                    raw.Split('+') |> Array.toList

            match List.rev segments with
            | keyTok :: modToks ->
                match parseKey keyTok with
                | None -> None
                | Some key ->
                    let mods = modToks |> List.rev |> List.map parseModifier

                    if mods |> List.exists Option.isNone then
                        None
                    else
                        Some
                            { Mods = mods |> List.choose id |> Set.ofList
                              Key = key }
            | [] -> None

    /// Bridge an editor chord to the frozen plugin-API KeyChord (apiVersion
    /// "1"). Total: returns `None` for anything the v1 KeyChord cannot name
    /// (Super, Named keys, Fn beyond the v1 range, etc.).
    let toKeyChord (chord: Chord) : Fedit.PluginApi.KeyChord option =
        let mods = chord.Mods

        match chord.Key with
        | Char c when mods = Set.ofList [ Ctrl ] -> Some(Fedit.PluginApi.KeyChord.Ctrl c)
        | Char c when mods = Set.ofList [ Ctrl; Shift ] -> Some(Fedit.PluginApi.KeyChord.CtrlShift c)
        | Char c when mods = Set.ofList [ Alt ] -> Some(Fedit.PluginApi.KeyChord.Alt c)
        | Char c when mods.IsEmpty -> Some(Fedit.PluginApi.KeyChord.Char c)
        | Fn n when mods.IsEmpty -> Some(Fedit.PluginApi.KeyChord.F n)
        | _ -> None

[<RequireQualifiedAccess>]
module Sequence =
    /// Outcome of feeding one chord to the sequence engine, given the chord
    /// prefixes currently bound as sequence-prefixes.
    type Step =
        /// The accumulated candidate is itself a (proper) prefix of a bound
        /// sequence — keep it pending and show it in the status bar.
        | Pending of KeyStroke
        /// No pending prefix extends — dispatch the candidate as a single
        /// stroke (the normal path).
        | Fire of KeyStroke
        /// A pending prefix existed but this chord did not extend it — the
        /// sequence failed; do NOT fall through to text insert.
        | Failed of KeyStroke

    /// Feed one chord. `pending` is the prefix accumulated so far (or []);
    /// `isPrefix stroke` is true when `stroke` is a proper prefix of some
    /// bound sequence. Pure and total.
    let step (isPrefix: KeyStroke -> bool) (pending: KeyStroke) (chord: Chord) : Step =
        let candidate = pending @ [ chord ]

        if isPrefix candidate then Pending candidate
        elif List.isEmpty pending then Fire candidate
        else Failed candidate
