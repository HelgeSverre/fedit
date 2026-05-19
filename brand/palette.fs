// fedit — brand palette
// Source: brand/palette.json. Do not edit by hand.
// Accent: Phosphor Green #00B86B
//
// Usage in TUI rendering:
//   open Fedit.Brand.Palette
//   let cursorColor = if isTrueColor then Truecolor Semantic.Dark.accent else Ansi256 (Semantic.Dark.accent.Ansi256)

namespace Fedit.Brand

module Palette =

    type Color =
        { Hex: string
          Rgb: byte * byte * byte
          Ansi256: byte }

    let private c hex (r: byte) (g: byte) (b: byte) (ansi: byte) =
        { Hex = hex
          Rgb = (r, g, b)
          Ansi256 = ansi }

    module Neutrals =
        let n50 = c "#FAFAFA" 250uy 250uy 250uy 231uy
        let n100 = c "#F4F4F5" 244uy 244uy 245uy 255uy
        let n200 = c "#E7E7EA" 231uy 231uy 234uy 253uy
        let n300 = c "#D3D3D8" 211uy 211uy 216uy 251uy
        let n400 = c "#A6A6AD" 166uy 166uy 173uy 247uy
        let n500 = c "#787880" 120uy 120uy 128uy 243uy
        let n600 = c "#58585F" 88uy 88uy 95uy 240uy
        let n700 = c "#3F3F46" 63uy 63uy 70uy 238uy
        let n800 = c "#27272A" 39uy 39uy 42uy 235uy
        let n900 = c "#18181B" 24uy 24uy 27uy 233uy
        let n950 = c "#0B0B0D" 11uy 11uy 13uy 232uy

    module Accent =
        let a100 = c "#D1FAE5" 209uy 250uy 229uy 195uy
        let a300 = c "#6EE7B7" 110uy 231uy 183uy 121uy
        let a500 = c "#00B86B" 0uy 184uy 107uy 35uy
        let a700 = c "#047857" 4uy 120uy 87uy 29uy
        let a900 = c "#064E3B" 6uy 78uy 59uy 22uy

    module Semantic =
        module Light =
            let bg = Neutrals.n50
            let bgElevated = Neutrals.n100
            let fg = Neutrals.n900
            let fgMuted = Neutrals.n600
            let fgSubtle = Neutrals.n500
            let border = Neutrals.n200
            let borderStrong = Neutrals.n300
            let accent = Accent.a500
            let accentFg = Neutrals.n900 // dark text on green
            let accentBg = Accent.a100

        module Dark =
            let bg = Neutrals.n950
            let bgElevated = Neutrals.n900
            let fg = Neutrals.n50
            let fgMuted = Neutrals.n400
            let fgSubtle = Neutrals.n500
            let border = Neutrals.n800
            let borderStrong = Neutrals.n700
            let accent = Accent.a500
            let accentFg = Neutrals.n900
            let accentBg = Accent.a900
