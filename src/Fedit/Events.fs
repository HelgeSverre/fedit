namespace Fedit

/// Which physical button generated a mouse event.
type MouseButton =
    | LeftButton
    | MiddleButton
    | RightButton
    | ScrollUp
    | ScrollDown
    | ScrollLeft
    | ScrollRight
    | Back
    | Forward

/// What happened to a mouse button.
type MouseAction =
    | Press
    | Release
    | Drag

/// A single mouse event decoded from the terminal.
/// Coordinates are zero-based cell positions (not pixels).
type MouseEvent =
    { Button: MouseButton
      Action: MouseAction
      Position: Position
      Modifiers: Set<Modifier> }

/// Events that can originate from the terminal layer.
/// This is the ONLY interface between the terminal I/O world and the
/// pure editor model. No escape sequences, encoding details, or
/// capability records leak past this boundary.
///
/// Case names are deliberately distinct from `Msg` cases (`KeyEvent` vs
/// `KeyPressed`, `FocusIn` vs `FocusGained`) so that both types can be
/// used unqualified in the same file without ambiguity.
///
/// Note: resize is handled separately by polling `Console.WindowWidth/Height`
/// in the runtime loop; it does not flow through `Terminal.tryReadEvent`.
type TerminalEvent =
    | KeyEvent of Chord
    | MouseEvent of MouseEvent
    | FocusIn
    | FocusOut
