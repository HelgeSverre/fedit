# Mouse Support in fedit

## What works today

### Click-to-place-cursor

Left-click in the editor moves the cursor to the clicked position. The screen coordinates are mapped to a buffer position via `Editor.mouseToBufferPosition`, which inverts the same layout arithmetic used by `View.Layout.render` (sidebar width, gutter width, dock height, viewport offsets).

### Drag-to-select

Hold the left button and drag to extend a selection. The anchor is set on press; each drag event moves the live cursor and redraws the selection overlay. The selection is handled by the same `Buffer.setSelection` primitive used by keyboard shift+motion.

### Mouse wheel

The wheel scrolls the viewport by `mouseScrollLines` (default 3) per tick. The cursor is dragged only when it would cross the `scrollOff` margin. Set `scrollMode` to `line` in config to make the wheel move the cursor instead.

### Focus restoration

Clicking in the editor restores `Focus = Editor`, so you can click out of the sidebar or prompt and immediately resume typing.

## Architecture

Mouse events flow through the terminal as SGR-encoded sequences (`MouseProtocol.fs` decodes them). The runtime turns them into three `Msg` cases:

- `MousePressed` — handled in `Editor.update` for left-button press
- `MouseDragged` — extends the selection while the drag is active
- `MouseReleased` — clears `Model.MouseDrag`

`MouseDragState` (in `Model.fs`) tracks the anchor buffer id and position. The drag is ignored if the active buffer changes mid-drag.

## What's not yet implemented

The following features from the original design are still deferred:

- **Modifier chords** (`Ctrl+Click`, `Shift+Click`, `Alt+Click`) — the current implementation only handles bare left-button events. Modifier handling would need to extend `MouseProtocol` decoding or wire mouse events through the keymap resolver.
- **Context menus** on right-click.
- **Sidebar click/double-click** to select or open files.
- **Gutter clicks** for breakpoints or line selection.
- **Multi-cursor** (`Alt+Click`).
- **Go to definition** via `Ctrl+Click`.
- **Keymap integration** — mouse actions are hardcoded in `Editor.update`, not bound through the `Keymap` system.

These are tracked as future work; the current shipping surface is intentionally small (click + drag + wheel) to avoid the complexity of keymap-mouse interop before the rest of the system is ready for it.
