namespace Fedit

open System
open System.Globalization

/// Color helpers — named statics for theme readability, hex/name parsing for
/// JSON loading, and Rgb→Indexed quantization for terminals that don't
/// support truecolor. The renderer currently emits whatever's stored
/// without downgrading; the quantization helper is here ready for a
/// future capability-detection pass.
///
/// The `CompilationRepresentation` attribute lets a module share its name
/// with the `Color` DU in `Screen.fs`: the compiler suffixes the module's
/// CLR name so there's no IL collision, while F# call sites still see
/// `Color.brightWhite`, `Color.ofHex …`, etc.
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Color =
    // ─────────────────────────────────────────────────────────────────
    // Standard 16 — every ANSI terminal speaks these by name.
    // ─────────────────────────────────────────────────────────────────
    let black = Indexed 0uy
    let red = Indexed 1uy
    let green = Indexed 2uy
    let yellow = Indexed 3uy
    let blue = Indexed 4uy
    let magenta = Indexed 5uy
    let cyan = Indexed 6uy
    let white = Indexed 7uy
    let brightBlack = Indexed 8uy
    let brightRed = Indexed 9uy
    let brightGreen = Indexed 10uy
    let brightYellow = Indexed 11uy
    let brightBlue = Indexed 12uy
    let brightMagenta = Indexed 13uy
    let brightCyan = Indexed 14uy
    let brightWhite = Indexed 15uy

    // ─────────────────────────────────────────────────────────────────
    // Curated cube picks — names for the shades used by the bundled
    // themes, so Themes.fs reads as palette intent rather than indices.
    // ─────────────────────────────────────────────────────────────────
    let phosphorGreen = Indexed 35uy // brand accent (#00AF5F)
    let forestGreen = Indexed 22uy
    let mossGreen = Indexed 28uy
    let electricBlue = Indexed 33uy
    let midnightBlue = Indexed 17uy
    let steelBlue = Indexed 25uy
    let deepSkyBlue = Indexed 81uy
    let teal = Indexed 80uy
    let darkTeal = Indexed 23uy
    let seafoam = Indexed 30uy
    let paleCyan = Indexed 159uy
    let oceanBlue = Indexed 24uy
    let azure = Indexed 31uy
    let paleSky = Indexed 153uy
    let burntOrange = Indexed 166uy
    let saddleBrown = Indexed 94uy
    let copper = Indexed 130uy
    let peach = Indexed 173uy
    let amber = Indexed 220uy
    let lemonChiffon = Indexed 229uy
    let goldenrod = Indexed 100uy
    let mustard = Indexed 178uy
    let crimson = Indexed 203uy
    let darkRed = Indexed 88uy
    let firebrick = Indexed 124uy
    let salmon = Indexed 217uy

    // ─────────────────────────────────────────────────────────────────
    // Constructors
    // ─────────────────────────────────────────────────────────────────

    let rgb (r: byte) (g: byte) (b: byte) = Rgb(r, g, b)

    /// Clamp into the cube range and box as `Indexed`.
    let indexed (n: int) = Indexed(byte (max 0 (min 255 n)))

    // ─────────────────────────────────────────────────────────────────
    // Hex parsing — "#RGB", "#RRGGBB", "RRGGBB"
    // ─────────────────────────────────────────────────────────────────

    let private parseHexByte (s: string) =
        match Byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
        | true, value -> Some value
        | false, _ -> None

    let tryOfHex (input: string) : Color option =
        if String.IsNullOrWhiteSpace input then
            None
        else
            let stripped =
                let s = input.Trim()
                if s.StartsWith "#" then s.Substring 1 else s

            match stripped.Length with
            | 3 ->
                // #RGB shorthand: expand each nibble (#abc → #aabbcc).
                let expand (c: char) = String([| c; c |])

                match
                    parseHexByte (expand stripped[0]),
                    parseHexByte (expand stripped[1]),
                    parseHexByte (expand stripped[2])
                with
                | Some r, Some g, Some b -> Some(Rgb(r, g, b))
                | _ -> None
            | 6 ->
                match
                    parseHexByte (stripped.Substring(0, 2)),
                    parseHexByte (stripped.Substring(2, 2)),
                    parseHexByte (stripped.Substring(4, 2))
                with
                | Some r, Some g, Some b -> Some(Rgb(r, g, b))
                | _ -> None
            | _ -> None

    /// Throws on invalid input. For bundled themes only — user-facing
    /// loaders should use `tryOfHex`.
    let ofHex (input: string) : Color =
        match tryOfHex input with
        | Some color -> color
        | None -> invalidArg "input" $"'{input}' is not a valid hex color (expected #RGB or #RRGGBB)"

    // ─────────────────────────────────────────────────────────────────
    // Named-color lookup for user-theme JSON
    // ─────────────────────────────────────────────────────────────────

    let private namedTable: Map<string, Color> =
        let normalize (s: string) =
            s.ToLowerInvariant().Replace("-", "").Replace("_", "")

        [
          // Standard 16
          "black", black
          "red", red
          "green", green
          "yellow", yellow
          "blue", blue
          "magenta", magenta
          "cyan", cyan
          "white", white
          "brightblack", brightBlack
          "brightred", brightRed
          "brightgreen", brightGreen
          "brightyellow", brightYellow
          "brightblue", brightBlue
          "brightmagenta", brightMagenta
          "brightcyan", brightCyan
          "brightwhite", brightWhite
          // Curated picks
          "phosphorgreen", phosphorGreen
          "forestgreen", forestGreen
          "mossgreen", mossGreen
          "electricblue", electricBlue
          "midnightblue", midnightBlue
          "steelblue", steelBlue
          "deepskyblue", deepSkyBlue
          "teal", teal
          "darkteal", darkTeal
          "seafoam", seafoam
          "palecyan", paleCyan
          "oceanblue", oceanBlue
          "azure", azure
          "palesky", paleSky
          "burntorange", burntOrange
          "saddlebrown", saddleBrown
          "copper", copper
          "peach", peach
          "amber", amber
          "lemonchiffon", lemonChiffon
          "goldenrod", goldenrod
          "mustard", mustard
          "crimson", crimson
          "darkred", darkRed
          "firebrick", firebrick
          "salmon", salmon ]
        |> List.map (fun (name, color) -> normalize name, color)
        |> Map.ofList

    /// Case-insensitive; tolerant of `kebab-case`, `snake_case`, and `camelCase`.
    let tryOfName (input: string) : Color option =
        if String.IsNullOrWhiteSpace input then
            None
        else
            let key = input.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "")

            Map.tryFind key namedTable

    // ─────────────────────────────────────────────────────────────────
    // Conversions
    // ─────────────────────────────────────────────────────────────────

    /// The ANSI 256-color cube's RGB approximations. Used by the
    /// quantizer when reducing a truecolor value to the nearest cube slot.
    let private cubeLevels = [| 0; 95; 135; 175; 215; 255 |]

    let private cubeRgb (n: byte) : int * int * int =
        // 16-231 is a 6×6×6 cube; 232-255 is a 24-step gray ramp.
        let i = int n

        if i < 16 then
            // Standard 16: approximations are good enough for nearest-match.
            let table =
                [| 0, 0, 0
                   170, 0, 0
                   0, 170, 0
                   170, 85, 0
                   0, 0, 170
                   170, 0, 170
                   0, 170, 170
                   170, 170, 170
                   85, 85, 85
                   255, 85, 85
                   85, 255, 85
                   255, 255, 85
                   85, 85, 255
                   255, 85, 255
                   85, 255, 255
                   255, 255, 255 |]

            table[i]
        elif i < 232 then
            let idx = i - 16
            let r = idx / 36
            let g = (idx / 6) % 6
            let b = idx % 6
            cubeLevels[r], cubeLevels[g], cubeLevels[b]
        else
            let level = 8 + (i - 232) * 10
            level, level, level

    /// Squared-RGB nearest neighbor against the full 256 palette.
    /// Reasonable approximation for 24-bit → 8-bit downgrade; CIELab
    /// would be more accurate but overkill for 256 entries.
    let private quantizeRgb (r: byte) (g: byte) (b: byte) =
        let target = int r, int g, int b
        let mutable bestIdx = 0
        let mutable bestDist = Int32.MaxValue

        for i in 0..255 do
            let cr, cg, cb = cubeRgb (byte i)
            let tr, tg, tb = target
            let dr = cr - tr
            let dg = cg - tg
            let db = cb - tb
            let d = dr * dr + dg * dg + db * db

            if d < bestDist then
                bestDist <- d
                bestIdx <- i

        byte bestIdx

    let toRgb (color: Color) : (byte * byte * byte) option =
        match color with
        | Default -> None
        | Indexed n ->
            let r, g, b = cubeRgb n
            Some(byte r, byte g, byte b)
        | Rgb(r, g, b) -> Some(r, g, b)

    let toIndexed (color: Color) : byte option =
        match color with
        | Default -> None
        | Indexed n -> Some n
        | Rgb(r, g, b) -> Some(quantizeRgb r g b)

    let toHex (color: Color) : string option =
        match toRgb color with
        | None -> None
        | Some(r, g, b) -> Some(sprintf "#%02X%02X%02X" r g b)

    /// Accept "#RRGGBB" / "#RGB" first; otherwise try the named-color
    /// table. Used by user-theme JSON loaders.
    let tryParse (input: string) : Color option =
        match tryOfHex input with
        | Some color -> Some color
        | None -> tryOfName input
