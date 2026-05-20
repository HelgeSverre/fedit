namespace Fedit.PluginApi

/// Severity of a notification raised by a plugin.
type Severity =
    | Info
    | Warning
    | Error

/// Cursor position inside a buffer. 1-based line, 0-based column —
/// matches the fedit `BufferState.Cursor` convention surfaced to UI.
type CursorPosition = { Line: int; Column: int }

/// A read-only snapshot of a buffer at the moment a plugin command runs.
type BufferView =
    { Id: int
      Name: string
      FilePath: string option
      Text: string
      Cursor: CursorPosition
      Selection: (CursorPosition * CursorPosition) option }

/// Workspace-level metadata available to plugins.
type WorkspaceView = { RootPath: string }

/// The execution context handed to every plugin command. Plugins never see
/// mutable state — the host builds this fresh per invocation.
type PluginContext =
    { ActiveBuffer: BufferView
      AllBuffers: BufferView list
      Workspace: WorkspaceView }

/// Side effects a plugin can request. The host translates these into
/// core editor effects and model changes. Closed DU — new variants in v2+
/// must be additive.
type PluginAction =
    | Notify of severity: Severity * message: string
    | InsertText of string
    | ReplaceSelection of string
    | MoveCursor of CursorPosition
    | OpenFile of path: string
    | SaveActiveBuffer
    | RunCommand of name: string
    | SetClipboard of string

/// A command definition a plugin registers with the host. `Run` is invoked
/// synchronously when the command fires; it should be fast (< 50ms).
type PluginCommand =
    { Name: string
      Usage: string
      Summary: string
      Run: PluginContext -> PluginAction list }

/// Keyboard chord a plugin can bind to a command name. MVP supports
/// modifier+character and function keys. Plain `Char` is reserved (basic
/// text input); the host rejects those registrations with a warning.
type KeyChord =
    | Char of char
    | Ctrl of char
    | Alt of char
    | CtrlShift of char
    | F of int
