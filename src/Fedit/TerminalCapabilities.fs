namespace Fedit

open System

/// What kind of mouse events the terminal can report.
type MouseEventMode =
    | MouseNone
    | MouseNormal // DECSET 1000: press + release
    | MouseButton // DECSET 1002: press + release + drag while held
    | MouseAll // DECSET 1003: all motion (overkill, avoid)

/// How mouse coordinates and buttons are encoded in escape sequences.
type MouseEncoding =
    | MouseEncodingNone
    | MouseX10 // Original X10 encoding (limited coords, single char)
    | MouseUtf8 // DECSET 1005 (UTF-8 extension, buggy in some terminals)
    | MouseSgr // DECSET 1006 (modern standard, unambiguous)
    | MouseUrxvt // DECSET 1015 (colon-delimited)
    | MouseSgrPixels // DECSET 1016 (SGR but pixel coords)

/// Which inline image protocol the terminal supports.
type ImageProtocolKind =
    | ImageNone
    | ImageKitty // Kitty APC graphics protocol
    | ImageIterm2 // iTerm2 OSC 1337
    | ImageSixel // Sixel DCS protocol
    | ImageUnicodeBlocks // Unicode half-block fallback

/// Color depth the terminal claims to support.
type ColorSupport =
    | ColorNone
    | ColorAnsi16
    | ColorAnsi256
    | ColorTrueColor

/// Kitty keyboard protocol enhancement level.
type KeyboardProtocolLevel =
    | KeyboardLegacy
    | KeyboardKittyBasic // >1u: disambiguate escape codes
    | KeyboardKittyExtended // >3u: report key release + alternate keys
    | KeyboardKittyFull // >5u: report associated text

/// A snapshot of terminal capabilities detected at startup.
/// All fields are pure data — no mutable state.
type TerminalCapabilities =
    { TerminalName: string option
      TerminalVersion: string option
      ColorSupport: ColorSupport
      MouseEventMode: MouseEventMode
      MouseEncoding: MouseEncoding
      ImageProtocol: ImageProtocolKind
      KeyboardProtocol: KeyboardProtocolLevel
      SupportsFocusEvents: bool
      SupportsAlternateScreen: bool
      SupportsBracketedPaste: bool
      SupportsUnicodePlaceholder: bool } // Kitty U+10EEEE image placeholders

[<RequireQualifiedAccess>]
module TerminalCapabilities =

    /// The most conservative capability set: assume almost nothing.
    let minimal =
        { TerminalName = None
          TerminalVersion = None
          ColorSupport = ColorAnsi16
          MouseEventMode = MouseNone
          MouseEncoding = MouseEncodingNone
          ImageProtocol = ImageNone
          KeyboardProtocol = KeyboardLegacy
          SupportsFocusEvents = false
          SupportsAlternateScreen = false
          SupportsBracketedPaste = false
          SupportsUnicodePlaceholder = false }

    /// Liberal default for a "modern" terminal when we have no better info.
    let modern =
        { TerminalName = None
          TerminalVersion = None
          ColorSupport = ColorTrueColor
          MouseEventMode = MouseButton
          MouseEncoding = MouseSgr
          ImageProtocol = ImageKitty
          KeyboardProtocol = KeyboardKittyBasic
          SupportsFocusEvents = true
          SupportsAlternateScreen = true
          SupportsBracketedPaste = true
          SupportsUnicodePlaceholder = false }

    let private env (name: string) : string option =
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | value when String.IsNullOrWhiteSpace value -> None
        | value -> Some value

    let private termProgram () = env "TERM_PROGRAM"
    let private term () = env "TERM" |> Option.defaultValue ""

    let private isKitty () =
        (env "KITTY_WINDOW_ID").IsSome || term () = "xterm-kitty"

    let private isGhostty () = (env "GHOSTTY_RESOURCES_DIR").IsSome

    let private isWezTerm () =
        match termProgram () with
        | Some "WezTerm" -> true
        | _ -> false

    let private isIterm2 () =
        match termProgram () with
        | Some "iTerm.app" -> true
        | _ -> false

    let private isTmux () = (env "TMUX").IsSome
    let private isScreen () = (env "STY").IsSome

    /// Infer capabilities from environment variables only.
    /// Fast (~0ms) but fragile; used as the default path and as a
    /// fallback when OSC/DA queries time out or are disabled.
    let fromEnv () : TerminalCapabilities =
        let termValue = term ()
        let termProgramValue = termProgram ()

        // Color: trust COLORTERM first, then TERM suffixes.
        let colorSupport =
            match env "COLORTERM" with
            | Some "truecolor"
            | Some "24bit" -> ColorTrueColor
            | _ ->
                if termValue.Contains "256" then ColorAnsi256
                elif termValue.Contains "color" then ColorAnsi256
                else ColorAnsi16

        // Mouse: basically every terminal emulator supports SGR mode 1006 today,
        // including the Windows 11 console. The only common exceptions are very
        // old or deliberately minimal terminals (linux tty, some embedded).
        let mouseEventMode, mouseEncoding =
            if termValue = "dumb" || termValue = "linux" then
                MouseNone, MouseEncodingNone
            else
                MouseButton, MouseSgr

        // Image protocol: rank by specificity.
        let imageProtocol =
            if isKitty () || isGhostty () then ImageKitty
            elif isWezTerm () then ImageIterm2
            elif isIterm2 () then ImageIterm2
            elif termValue.Contains "sixel" then ImageSixel
            else ImageNone

        let keyboardProtocol =
            if isKitty () || isGhostty () || isWezTerm () then
                KeyboardKittyBasic
            else
                KeyboardLegacy

        let supportsUnicodePlaceholder = isKitty () || isGhostty ()

        // Focus events: widely supported but not universal.
        let supportsFocusEvents = not (termValue = "dumb" || termValue = "linux")

        // Alternate screen: universal among terminal emulators; dumb terminals
        // and the raw linux console don't have it.
        let supportsAlternateScreen = not (termValue = "dumb" || termValue = "linux")

        // Bracketed paste: widely supported.
        let supportsBracketedPaste = not (termValue = "dumb" || termValue = "linux")

        { TerminalName = termProgramValue
          TerminalVersion = env "TERM_PROGRAM_VERSION"
          ColorSupport = colorSupport
          MouseEventMode = mouseEventMode
          MouseEncoding = mouseEncoding
          ImageProtocol = imageProtocol
          KeyboardProtocol = keyboardProtocol
          SupportsFocusEvents = supportsFocusEvents
          SupportsAlternateScreen = supportsAlternateScreen
          SupportsBracketedPaste = supportsBracketedPaste
          SupportsUnicodePlaceholder = supportsUnicodePlaceholder }

    // -----------------------------------------------------------------------
    // Query-based refinement (DA1 / DA2)
    // -----------------------------------------------------------------------

    let private tryParseInt (s: string) =
        match Int32.TryParse s with
        | true, v -> Some v
        | false, _ -> None

    /// Parse a DA1 response: `CSI ? Ps ; Ps ; ... c`.
    /// Returns the list of integer parameters after the `?`.
    let parseDa1Response (sequence: string) : int list option =
        if String.IsNullOrEmpty sequence then
            None
        elif not (sequence.StartsWith("\u001b[?", StringComparison.Ordinal)) then
            None
        else
            let body = sequence.Substring(3)
            // Remove trailing 'c' and any extra bytes
            let trimmed = body.TrimEnd('c', '\u001b', '\\')
            let parts = trimmed.Split(';')
            let nums = parts |> Array.choose tryParseInt |> List.ofArray
            if nums.IsEmpty then None else Some nums

    /// Parse a DA2 response: `CSI > Ps ; Ps ; Ps c`.
    /// Returns (terminalType, version, firmware) as a triple.
    let parseDa2Response (sequence: string) : (int * int * int) option =
        if String.IsNullOrEmpty sequence then
            None
        elif not (sequence.StartsWith("\u001b[>", StringComparison.Ordinal)) then
            None
        else
            let body = sequence.Substring(3)
            let trimmed = body.TrimEnd('c', '\u001b', '\\')
            let parts = trimmed.Split(';')

            if parts.Length >= 3 then
                match tryParseInt parts[0], tryParseInt parts[1], tryParseInt parts[2] with
                | Some t, Some v, Some f -> Some(t, v, f)
                | _ -> None
            else
                None

    /// Merge env-based detection with DA1/DA2 query responses.
    /// Query results override env when they identify a known terminal.
    let fromQueries (da1: int list option) (da2: (int * int * int) option) : TerminalCapabilities =
        let baseCaps = fromEnv ()

        let fromDa2 (t, v, _f) =
            // Known DA2 signatures:
            //   kitty   : (1, 4000, 1)
            //   ghostty : (1, 6000, 1)  (estimated)
            //   wezterm : (0, 207, 0)   (varies by version)
            //   iterm2  : (0, 95, 0)
            //   xterm   : (0, 241, 0)
            if t = 1 && v = 4000 then
                Some "kitty", KeyboardKittyBasic, ImageKitty, true
            elif t = 1 && v >= 5000 && v <= 7000 then
                Some "ghostty", KeyboardKittyBasic, ImageKitty, true
            elif t = 0 && v >= 200 && v <= 300 then
                Some "wezterm", KeyboardKittyBasic, ImageIterm2, false
            elif t = 0 && v >= 90 && v <= 100 then
                Some "iterm2", KeyboardLegacy, ImageIterm2, false
            else
                None, baseCaps.KeyboardProtocol, baseCaps.ImageProtocol, baseCaps.SupportsUnicodePlaceholder

        let termName, keyboard, image, unicodePlaceholder =
            match da2 with
            | Some(t, v, f) ->
                match fromDa2 (t, v, f) with
                | Some name, kb, img, up -> Some name, kb, img, up
                | _ ->
                    baseCaps.TerminalName,
                    baseCaps.KeyboardProtocol,
                    baseCaps.ImageProtocol,
                    baseCaps.SupportsUnicodePlaceholder
            | None ->
                baseCaps.TerminalName,
                baseCaps.KeyboardProtocol,
                baseCaps.ImageProtocol,
                baseCaps.SupportsUnicodePlaceholder

        // If DA1 contains the kitty keyboard param (1), upgrade keyboard.
        let keyboard =
            match da1 with
            | Some nums when nums |> List.contains 1 -> KeyboardKittyBasic
            | _ -> keyboard

        { baseCaps with
            TerminalName = termName
            KeyboardProtocol = keyboard
            ImageProtocol = image
            SupportsUnicodePlaceholder = unicodePlaceholder }

    /// Human-readable summary for debugging / logging.
    let toLogString (caps: TerminalCapabilities) : string =
        let mouse =
            match caps.MouseEventMode with
            | MouseNone -> "none"
            | MouseNormal -> "normal"
            | MouseButton -> "button"
            | MouseAll -> "all"

        let img =
            match caps.ImageProtocol with
            | ImageNone -> "none"
            | ImageKitty -> "kitty"
            | ImageIterm2 -> "iterm2"
            | ImageSixel -> "sixel"
            | ImageUnicodeBlocks -> "unicode-blocks"

        let color =
            match caps.ColorSupport with
            | ColorNone -> "none"
            | ColorAnsi16 -> "16"
            | ColorAnsi256 -> "256"
            | ColorTrueColor -> "truecolor"

        let term = caps.TerminalName |> Option.defaultValue "unknown"
        $"term={term} mouse={mouse} encoding={caps.MouseEncoding} image={img} color={color}"
