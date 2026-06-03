# Unified `Color` type for Fedit

## Context

Fedit currently represents color as `Color = Default | Indexed of int` in `Screen.fs:5-7`, and themes (`Themes.fs`) hold five raw `int` ANSI 256-color codes per theme. The `// TODO` on `Themes.fs:5` flags the readability problem: numbers like `81`, `24`, `153` carry no intent. There is also no path to truecolor — `Renderer.sgrColor` (`Renderer.fs:9-12`) only emits `38;5;N` / `48;5;N`.

The goal is a single `Color` value that can be expressed as:

- a named ANSI color (the standard 16, used for readability),
- a 256-color cube index (compatibility, current behaviour),
- a hex/truecolor RGB (for design-tool-friendly themes and future truecolor support),
- with conversion in both directions so the rest of the codebase can mix and match freely.

Bundled themes should read like `Color.ofHex "#5fd7ff"` or `Color.cyan`, not raw integers, and the semantic _role_ of each color in a theme should be visible (Option 5 from the discussion).

## Prior art (what we're borrowing)

- **Ratatui / Crossterm** (Rust): single enum `Reset | Black .. White | Indexed(u8) | Rgb(u8,u8,u8)`. No capability detection inside the type — the renderer decides. Value-typed, trivial equality. This is the closest match to F# idiom.
- **Spectre.Console** (.NET): same DU shape but exposed as `Color` struct with ~140 static named colors + `Color.FromHex`. Quantization is squared-RGB nearest neighbor against the 256-palette.
- **Rich** (Python): tags each color with a `ColorType` so the renderer can downgrade explicitly. Useful concept for _later_ when we add capability detection.
- **Lipgloss** (Go): exposes `AdaptiveColor` / `CompleteColor` — pushes the choice onto the API user rather than auto-quantizing silently.

The de-facto pattern: a value-typed DU + named statics + `ofHex` + a _separate_ renderer that knows the terminal profile. Quantization is universally squared-RGB nearest neighbor against the 256 palette (nobody uses CIELab — not worth it for 256 entries).

## Design

### 1. Color type (in `Screen.fs`, replacing the existing two-case DU)

```fsharp
type Color =
    | Default
    | Indexed of byte          // 0..255, ANSI 256-color
    | Rgb of byte * byte * byte // truecolor
```

`byte` instead of `int` — encodes the 0-255 invariant in the type.

### 2. `Color` module (new file `Color.fs`, placed after `Screen.fs` in the project ordering)

Named statics for the **standard 16** (`Color.black`, `Color.red`, ..., `Color.brightWhite`, indices 0..15) plus a curated set of **~20-30 named cube picks** — the actual colors used by the bundled themes — so theme definitions read poetically (`Color.deepSkyBlue`, `Color.hotPink`, `Color.forestGreen`, `Color.amber`, `Color.crimson`, `Color.royalPurple`, etc.). The full Spectre-style ~140 name set is rejected as overkill; anything outside the curated set is reachable via `Color.ofHex`.

```fsharp
module Color =
    // Standard 16 (any ANSI terminal)
    let black = Indexed 0uy
    let red = Indexed 1uy
    // ...
    let brightWhite = Indexed 15uy

    // Curated cube picks (named for theme readability)
    let deepSkyBlue = Indexed 81uy      // theme cyan accent
    let hotPink = Indexed 213uy         // theme magenta accent
    let forestGreen = Indexed 82uy      // theme green accent
    let amber = Indexed 215uy           // theme orange accent
    let crimson = Indexed 203uy         // theme red accent
    let royalPurple = Indexed 141uy     // theme purple accent
    // ...one per role per bundled theme, ~25 names total

    // Constructors
    let rgb (r: byte) (g: byte) (b: byte) = Rgb (r, g, b)
    let indexed (n: int) = Indexed (byte (max 0 (min 255 n)))

    // Hex parsing — accepts "#RGB", "#RRGGBB", "RRGGBB"
    let tryOfHex (s: string) : Color option
    let ofHex (s: string) : Color   // throws on invalid; for bundled themes

    // Named-color parsing for JSON loading
    let tryOfName (s: string) : Color option   // case-insensitive lookup

    // Conversions
    let toRgb : Color -> (byte * byte * byte) option   // None for Default
    let toIndexed : Color -> byte option               // quantizes Rgb → 256-cube
    let toHex : Color -> string option                 // None for Default
```

`toIndexed` uses the standard 6×6×6 cube quantization with the gray-ramp special case (Rich's algorithm) — squared-RGB nearest neighbor. **Written now, called nowhere yet** — ready for capability detection later; testable in isolation.

### 3. Renderer extension (`Renderer.fs:9-12`)

Extend `sgrColor` with the `Rgb` arm:

```fsharp
let private sgrColor isForeground color =
    match color with
    | Default -> if isForeground then "39" else "49"
    | Indexed v -> if isForeground then $"38;5;{v}" else $"48;5;{v}"
    | Rgb (r, g, b) ->
        if isForeground then $"38;2;{r};{g};{b}" else $"48;2;{r};{g};{b}"
```

No capability detection in this pass — emit what's stored, trust the terminal. Modern terminals (iTerm2, Alacritty, Kitty, WezTerm, Ghostty, modern Terminal.app) all support truecolor. We can add a `ColorProfile` and `Color.downgrade` later behind the same API.

### 4. Theme restructure (Option 5 + named roles)

Reshape `Theme` so the role of each color is obvious and the four hue-related colors are explicitly _shades of the same palette_:

```fsharp
type Theme =
    { Name: string
      Description: string
      // Hue family — four shades of the theme's primary color
      Accent: Color       // brightest, used for emphasis (titles, focused chrome)
      StatusBg: Color     // strong shade for status bar background
      SelectedBg: Color   // medium shade for selection highlight
      CurrentLine: Color  // softest shade for current-line background
      // Foreground policy — text on StatusBg
      StatusFg: Color }
```

Bundled themes become readable — mix named statics where they fit, hex where they don't:

```fsharp
let cyan =
    { Name = "cyan"
      Description = "Default — cool blue accent"
      Accent      = Color.deepSkyBlue              // was 81
      StatusBg    = Color.ofHex "#005f87"          // was 24
      SelectedBg  = Color.ofHex "#0087af"          // was 31
      CurrentLine = Color.ofHex "#afdfff"          // was 153
      StatusFg    = Color.brightWhite }            // was 15
```

The current numeric values are preserved exactly — the curated named statics resolve to the same `Indexed` values currently in the themes, and the hex strings correspond to the 256-cube entries for the other indices. Visual output should be pixel-identical after the swap. View.fs consumers (`View.fs:15-27`) lose their `Indexed` wrappers (theme fields are already `Color`).

### 5. User-theme JSON (Runtime.fs:118-166)

Replace `getIntProp` with `getColorProp` that accepts **hex strings or named colors only** — no raw integers:

- `"#RRGGBB"` or `"#RGB"` → `Color.ofHex` → `Rgb`,
- named string like `"red"`, `"deepSkyBlue"` → `Color.tryOfName` (case-insensitive, kebab- and camel-tolerant) → `Indexed`,
- anything else → malformed, skip the field (matches current "silently skip malformed" behaviour at `Runtime.fs:162`).

This is a **breaking change** for any user themes using bare-int fields. Exploration confirmed:

- No user-authored themes exist in this repo,
- `brand/themes/*.json` already uses hex strings,
- The eight bundled themes are F# code (not JSON), so they're untouched by this schema change.

Field names in JSON change with the record rename (e.g. `"accent"` stays, but if any role names change, JSON keys must follow). Document the new schema with a sample file under `brand/themes/`.

## Files to modify

- `src/Fedit/Screen.fs:5-7` — extend `Color` DU with `Rgb`.
- `src/Fedit/Color.fs` — **new file**, named constants + `ofHex` + conversions + quantization.
- `src/Fedit/Fedit.fsproj` — register `Color.fs` between `Screen.fs` and `Themes.fs`.
- `src/Fedit/Renderer.fs:9-12` — add `Rgb` arm to `sgrColor`.
- `src/Fedit/Themes.fs` — restructure record (role-named fields), rewrite eight bundled themes using `Color.ofHex` / named statics.
- `src/Fedit/View.fs:15-27` — update consumers to use new field names; drop the `Indexed` wrappers (theme fields are already `Color`).
- `src/Fedit/Runtime.fs:118-166` — `getColorProp` accepting int | hex | named in user theme JSON. Update field reads to match new `Theme` shape.
- `brand/themes/*.json` — already uses hex; align field names with new schema if the names changed.

## Out of scope (deferred)

- Terminal capability detection (`COLORTERM` sniffing) and automatic downgrade. The quantization function exists in `Color`; wiring it into the renderer based on profile waits until we have a concrete terminal that doesn't support truecolor.
- A `ColorProfile`-tagged variant of `Color` (Rich-style). Punt until we actually need to communicate lossiness to callers.
- Exposing ~140 named colors like Spectre. Just the standard 16; the rest can be reached via `ofHex`.

## Verification

1. `dotnet build` — clean build, no warnings.
2. `dotnet run` — launch the editor; cycle through all eight bundled themes via the command bar (`:theme cyan`, `:theme red`, …). Visual check: each theme should look pixel-identical to the current output (because hex values are chosen to match the existing 256-cube indices).
3. Drop a user theme file at `~/.config/fedit/themes/test-truecolor.json` with `"accent": "#ff6ec7"` (a color outside the 256-cube) and confirm `:theme test-truecolor` applies it and renders correctly in a truecolor terminal (iTerm2 / Alacritty / Ghostty).
4. Drop a user theme using a _named_ color (`"accent": "deepSkyBlue"`) and confirm it loads.
5. Drop a user theme with the _old_ integer schema (`"accent": 81`) and confirm it is rejected gracefully (skipped as malformed, doesn't crash). Document the new schema in a README or sample JSON under `brand/themes/`.
6. Sanity-check `Color.toIndexed` in isolation (FSI or a one-off `dotnet run` snippet): `toIndexed (ofHex "#ff0000")` should land near `Indexed 196uy` (the cube's red); `toIndexed (ofHex "#808080")` should land in the gray ramp around `Indexed 244uy`.
7. Run any existing tests (`dotnet test` if a test project exists — exploration didn't surface one, so this may be a no-op).
