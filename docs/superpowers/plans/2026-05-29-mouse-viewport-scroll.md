# Mouse-wheel viewport scrolling + configurable scrolloff — Plan

> Status: plan, ready to execute. Decisions locked with the user:
> **enable terminal mouse reporting** (the only way to make the wheel
> distinguishable from real arrow keys), and **mirror helix defaults**
> (`scrollMode = viewport`, `scrollOff = 5`) while keeping every knob
> configurable. Companion to the keybindings research in
> [`../research/keybindings-and-macros.md`](../research/keybindings-and-macros.md).

---

## Context

Today, scrolling the mouse wheel in fedit **moves the cursor line**, not the
view. The reason is that fedit has *no mouse support at all* — input is read via
`Console.ReadKey` (`Runtime.fs:456`), and the terminal itself translates the
wheel into ↑/↓ arrow keys, which fedit handles as ordinary cursor motion. The
viewport then follows the cursor. (`docs/wip-keybinds.md:1614` even records "No
mouse" as a deliberate stance.)

The user wants the wheel to scroll the **viewport** while leaving the cursor put
(dragging it only when it would otherwise scroll off-screen), with a
configurable edge margin (`scrolloff`), and the option to keep the old
cursor-moving behaviour as a selectable mode.

- **Enable terminal mouse reporting.** This is the only way to make the wheel
  distinguishable from real arrow keys. While fedit runs it captures the mouse,
  so terminal-native click-drag selection / wheel-scrollback are suspended
  (Shift/Option bypasses, as in vim/helix). Accepted tradeoff.
- **Mirror helix defaults, fully configurable:** default `scrollMode = viewport`,
  default `scrollOff = 5`, wheel step configurable (default 3 lines/tick).

The model side is clean: `Buffer.ensureViewport` (the cursor→viewport slide) has
exactly **one** call site (`Editor.fs:78`), so the edge margin threads through in
one place, and the cursor↔viewport relationship can be inverted with one new
pure function.

## How helix/neovim model this (reference)

- **scrolloff** = minimum lines kept between the cursor and the top/bottom edge.
  nvim default `0`, helix `editor.scrolloff` default `5`. It applies to *all*
  cursor movement, not just the wheel — so we apply it inside `ensureViewport`.
- **Wheel** scrolls the view by a fixed step (nvim `mousescroll` default `ver:3`).
  The cursor stays on its text line until scrolling would push it inside the
  scrolloff band, at which point the cursor is dragged along to honour the band.
  This is the inverse of fedit's current "viewport follows cursor" rule.

## Design

Two config knobs + one inverted viewport operation + a mouse input path.

### 1. Config (`src/Fedit/Model.fs`, `src/Fedit/Config.fs`)

Add to the `Config` record (`Model.fs:42-76`) and `Config.defaults`
(`Model.fs:78-92`), mirroring the existing `PageOverlap` / `TabWidth` fields:

```fsharp
type ScrollMode = ScrollLine | ScrollViewport   // new DU near IconMode

// in Config record:
ScrollMode: ScrollMode      // what the wheel does. Default ScrollViewport.
ScrollOff: int              // edge margin in lines. Default 5 (helix).
MouseScrollLines: int       // lines per wheel tick. Default 3 (nvim).
```

Load/save follow the canonical `:syntax` end-to-end pattern: parse in
`ConfigIO.load` (`Config.fs:48-145`) using the existing `getStringProp` /
`getIntProp` / `clampInt` helpers, write in `ConfigIO.save` (`Config.fs:246-291`).
JSON keys: `scrollMode` (`"line"`/`"viewport"`), `scrollOff` (clamp `0..50`),
`mouseScrollLines` (clamp `1..20`). Unknown/invalid values fall back to defaults,
like every other setting.

### 2. Viewport math (`src/Fedit/Buffer.fs:501-522`)

**a. Give `ensureViewport` a scrolloff margin** (cursor-led path — keeps the
cursor `margin` lines from the edge on every keystroke):

```fsharp
let private slideViewport cursor viewport span margin =
    let m = min margin ((span - 1) / 2)          // never exceed half the view
    if   cursor < viewport + m            then cursor - m
    elif cursor >= viewport + span - m    then cursor - span + 1 + m
    else viewport

let ensureViewport scrolloff viewportHeight viewportWidth buffer =
    // ... vertical slide uses `scrolloff`; horizontal uses 0 (no sidescrolloff) ...
    // existing `clamp 0 maxTop` naturally relaxes the margin at file top/bottom
```

The lone caller at `Editor.fs:78` passes `model.Config.ScrollOff`.

**b. Add the inverted operation** — viewport-led scroll that drags the cursor
only into the scrolloff band (the helix behaviour):

```fsharp
let scrollViewport scrolloff viewportHeight delta buffer =
    let h = max 1 viewportHeight
    let maxTop = max 0 (lineCount buffer - h)
    let newTop = (buffer.ViewportTop + delta) |> max 0 |> min maxTop
    let m = min scrolloff ((h - 1) / 2)
    let lo = newTop + m
    let hi = newTop + h - 1 - m
    let newLine = buffer.Cursor.Line |> max lo |> min hi |> max 0 |> min (lineCount buffer - 1)
    // keep PreferredColumn; clamp Column to the new line's length
    { buffer with ViewportTop = newTop; Cursor = { Line = newLine; Column = clampedCol } }
```

**Self-consistency note:** `updateActiveBuffer` *always* re-runs `ensureViewport`
after any transform (`Editor.fs:78`). Because `scrollViewport` sets `ViewportTop`
*and* parks the cursor inside the scrolloff band, the follow-up `ensureViewport`
(same `scrolloff`, same height formula) is a fixed point and leaves the scroll
intact. No new code path can fight the reconciler.

### 3. Input case (`src/Fedit/Primitives.fs`)

Add `WheelUp` / `WheelDown` to the `KeyInput` DU (`Primitives.fs:23-55`). They do
**not** come through `Input.tryMap` (which only sees keyboard `ConsoleKeyInfo`);
they are produced by the new mouse parser and dispatched as `KeyPressed WheelUp`,
reusing the existing focus dispatch unchanged.

### 4. Editor dispatch (`src/Fedit/Editor.fs:881-908`)

Beside the existing `pageJump` helper, add the wheel handling in the editor-focus
`match key`:

```fsharp
let scrollBy delta =                      // viewport-led, cursor dragged minimally
    let h = max 1 (model.Terminal.Height - model.Panels.DockHeight - 2)
    updateActiveBuffer (Buffer.scrollViewport model.Config.ScrollOff h delta) model, []

match key with
| WheelUp ->
    match model.Config.ScrollMode with
    | ScrollViewport -> scrollBy (-model.Config.MouseScrollLines)
    | ScrollLine     -> move (Buffer.movePageUp model.Config.MouseScrollLines)   // old behaviour
| WheelDown ->
    match model.Config.ScrollMode with
    | ScrollViewport -> scrollBy ( model.Config.MouseScrollLines)
    | ScrollLine     -> move (Buffer.movePageDown model.Config.MouseScrollLines)
```

`ScrollLine` mode reuses `Buffer.movePageUp/Down` (`Buffer.fs:453-469`) with the
small wheel step — this *is* the current wheel-as-arrows behaviour, retained as
the user requested. Sidebar/prompt focus let `WheelUp/Down` fall through their
existing `| _ -> model, []`; file-tree wheel scrolling is a deferred follow-up.

### 5. Mouse reporting + parsing (`src/Fedit/Renderer.fs`, `Input.fs`, `Runtime.fs`)

- **Enable/disable** in `Renderer.enter`/`leave` (`Renderer.fs:92-96`), alongside
  the existing alt-screen toggles: enable `ESC[?1000h ESC[?1006h` on enter,
  disable `ESC[?1000l ESC[?1006l` on leave. The runtime `finally` already calls
  `Renderer.leave` (`Runtime.fs:498`), so reporting is torn down on crash too.
- **Parse** SGR mouse reports `ESC [ < Cb ; Cx ; Cy (M|m)`. Add a pure, testable
  helper `Input.parseSgrMouse : string -> KeyInput option` that returns `WheelUp`
  when `Cb &&& 0b1100_0011 = 64`, `WheelDown` when `= 65`, else `None`
  (clicks/drags ignored). Wheel low bits are 64/65; modifier bits (4/8/16) are
  masked off.
- **Wire** it into the input loop (`Runtime.fs:456-461`). When `Console.ReadKey`
  yields `Escape` and `Console.KeyAvailable` is immediately true, drain the CSI
  bytes (`ReadKey true` until the `M`/`m` terminator), feed them to
  `parseSgrMouse`, and dispatch `KeyPressed wheel` on a hit; otherwise treat the
  `Escape` exactly as today. Real arrows/function keys are still decoded by
  .NET's terminfo layer and never reach this branch.

  **Implementation risk + spike:** `.NET`'s `Console.ReadKey` behaviour on
  *unrecognised* escape sequences (SGR mouse) is the one uncertain piece on
  macOS. First implementation step is a throwaway spike confirming that the
  post-`ESC` bytes arrive as literal chars via `ReadKey`. **Fallback if not:**
  switch the input loop to read raw bytes from `Console.OpenStandardInput()` and
  run both a key decoder and the mouse decoder over the byte stream (larger, but
  the model/config/dispatch work above is unaffected).

## This is the minimal approach

Everything above is the smallest change that delivers the feature: two config
fields + one DU, one new pure `Buffer` function + a one-arg addition to
`ensureViewport`, two `match` arms in the editor, and an escape-sequence sniffer
gated behind the wheel. No change to the MVU shape — the viewport stays
derived-and-reconciled, the cursor stays the source of truth.

## Interesting alternative (to think about, not build now)

**Decouple the viewport from the cursor entirely.** Make `ViewportTop` a
first-class scroll offset that the wheel sets *freely* (including over-scroll
past EOF, or keeping the cursor off-screen the way VSCode does), and enforce the
cursor↔viewport relationship through a single `reconcile cursor top height
scrolloff` invariant that *both* cursor-moves and wheel-scrolls funnel through —
rather than today's "viewport always chases cursor." Upsides: one place owns all
scroll logic, enables features like scroll-past-end and a free-floating cursor,
and makes momentum/smooth scrolling a natural extension. Downside: the cursor can
leave the viewport, so you must define a policy for "type while scrolled away"
(snap back? leave it?), and it's a deeper change to the model. Worth it only if
fedit later wants VSCode-style decoupled scrolling; the minimal approach is a
strict subset, so adopting this later is non-destructive.

## Files to modify

- `src/Fedit/Primitives.fs` — `WheelUp`/`WheelDown` in `KeyInput`.
- `src/Fedit/Model.fs` — `ScrollMode` DU; three `Config` fields + defaults.
- `src/Fedit/Config.fs` — load (`load`) + save (`save`) of the three keys.
- `src/Fedit/Buffer.fs` — `scrolloff` arg on `slideViewport`/`ensureViewport`;
  new `scrollViewport`.
- `src/Fedit/Editor.fs` — pass `Config.ScrollOff` at the `ensureViewport` call
  (`:78`); `WheelUp`/`WheelDown` arms (`:887`).
- `src/Fedit/Renderer.fs` — enable/disable mouse reporting in `enter`/`leave`.
- `src/Fedit/Input.fs` — `parseSgrMouse` helper.
- `src/Fedit/Runtime.fs` — CSI-drain + mouse dispatch in the input loop.

(No `<Compile>` additions — all edits are to existing files. Per CLAUDE.md, only
*new* files need fsproj entries.)

## Verification

- `just test` — add xUnit/FsCheck cases in `tests/Fedit.Tests/` (the pure bits):
  - `ensureViewport` keeps `scrolloff` lines above/below the cursor, and relaxes
    correctly at file top/bottom.
  - `scrollViewport` moves `ViewportTop` by `delta`, leaves the cursor on its
    line while it stays in-band, drags it only when it would breach the band, and
    clamps at `[0, maxTop]`.
  - `parseSgrMouse` decodes wheel-up/down and rejects clicks/drags/garbage.
  - Config round-trips `scrollMode`/`scrollOff`/`mouseScrollLines` through
    load→save, and bad values fall back to defaults.
- `just check` — lint + build + test gate (per CLAUDE.md).
- Manual: `./fedit .`, open a long file, scroll the wheel — viewport moves, the
  cursor holds its line until it reaches the 5-line margin, then rides the edge.
  Set `scrollMode: line` in `~/.config/fedit/config.json` and confirm the old
  cursor-moving behaviour returns. Confirm `Shift`/`Option`+drag still selects in
  the terminal, and that quitting (`Ctrl+Q`) and any crash path restore normal
  mouse behaviour (reporting disabled in `Renderer.leave`).
