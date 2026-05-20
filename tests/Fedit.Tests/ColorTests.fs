module Fedit.Tests.ColorTests

open Fedit
open Xunit
open FsUnit.Xunit

// ─────────────────────────────────────────────────────────────────────
// Hex parsing
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``ofHex accepts #RRGGBB`` () =
    Color.ofHex "#FF6EC7" |> should equal (Rgb(0xFFuy, 0x6Euy, 0xC7uy))

[<Fact>]
let ``ofHex accepts bare RRGGBB without leading hash`` () =
    Color.ofHex "0087AF" |> should equal (Rgb(0x00uy, 0x87uy, 0xAFuy))

[<Fact>]
let ``ofHex accepts #RGB shorthand`` () =
    Color.ofHex "#abc" |> should equal (Rgb(0xAAuy, 0xBBuy, 0xCCuy))

[<Fact>]
let ``tryOfHex rejects malformed input`` () =
    Color.tryOfHex "" |> should equal None
    Color.tryOfHex "#ZZZZZZ" |> should equal None
    Color.tryOfHex "#12345" |> should equal None
    Color.tryOfHex "not-a-color" |> should equal None

// ─────────────────────────────────────────────────────────────────────
// Named-color parsing (case- and shape-insensitive)
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``tryOfName resolves standard 16 by name`` () =
    Color.tryOfName "red" |> should equal (Some Color.red)
    Color.tryOfName "brightWhite" |> should equal (Some Color.brightWhite)

[<Fact>]
let ``tryOfName is case- kebab- and snake-insensitive`` () =
    Color.tryOfName "DEEP-SKY-BLUE" |> should equal (Some Color.deepSkyBlue)

    Color.tryOfName "deep_sky_blue" |> should equal (Some Color.deepSkyBlue)

    Color.tryOfName "DeepSkyBlue" |> should equal (Some Color.deepSkyBlue)

[<Fact>]
let ``tryOfName returns None for unknown colors`` () =
    Color.tryOfName "burntumber" |> should equal None
    Color.tryOfName "" |> should equal None

// ─────────────────────────────────────────────────────────────────────
// tryParse — the loader entrypoint (hex first, then name)
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``tryParse accepts hex and named colors`` () =
    Color.tryParse "#FF0000" |> should equal (Some(Rgb(0xFFuy, 0uy, 0uy)))
    Color.tryParse "deepSkyBlue" |> should equal (Some Color.deepSkyBlue)

[<Fact>]
let ``tryParse rejects bare integers (no longer schema)`` () =
    // After Color unification, theme JSON uses hex or names — raw "81"
    // is malformed and must be rejected so the loader can flag it.
    Color.tryParse "81" |> should equal None

// ─────────────────────────────────────────────────────────────────────
// Quantization — used when downgrading Rgb → Indexed for legacy
// terminals. Round-trip checks against well-known cube slots.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``toIndexed quantizes pure red into the cube near index 196`` () =
    let red = Rgb(0xFFuy, 0uy, 0uy)

    match Color.toIndexed red with
    | Some idx -> idx |> should equal 196uy
    | None -> failwith "expected Some _"

[<Fact>]
let ``toIndexed quantizes mid-gray into the gray ramp`` () =
    let gray = Rgb(0x80uy, 0x80uy, 0x80uy)

    match Color.toIndexed gray with
    | Some idx ->
        // The 24-step gray ramp lives at 232..255; mid-gray should
        // land somewhere in the middle.
        let i = int idx
        i |> should be (greaterThanOrEqualTo 240)
        i |> should be (lessThanOrEqualTo 248)
    | None -> failwith "expected Some _"

[<Fact>]
let ``toIndexed of an Indexed value is identity`` () =
    Color.toIndexed (Indexed 42uy) |> should equal (Some 42uy)

[<Fact>]
let ``toIndexed of Default is None`` () =
    Color.toIndexed Default |> should equal None

// ─────────────────────────────────────────────────────────────────────
// toHex — useful for serializing themes back to JSON later
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``toHex round-trips an Rgb`` () =
    let c = Rgb(0x12uy, 0x34uy, 0x56uy)
    Color.toHex c |> should equal (Some "#123456")

[<Fact>]
let ``toHex of Default is None`` () =
    Color.toHex Default |> should equal None
