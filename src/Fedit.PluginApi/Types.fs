namespace Fedit.PluginApi

/// Severity of a notification raised by a plugin.
type Severity =
    | Info
    | Warning
    | Error

/// Cursor position inside a buffer. 1-based line and column — mirrors the
/// status bar's `Ln N · Col M` indicator. The host translates to and from
/// fedit's 0-based internal `Position` at the API boundary, and clamps
/// out-of-range coordinates to the buffer.
type CursorPosition = { Line: int; Column: int }

/// A read-only snapshot of a buffer at the moment a plugin command runs.
type BufferView =
    { Id: int
      Name: string
      FilePath: string option
      Text: string
      Cursor: CursorPosition
      Selection: (CursorPosition * CursorPosition) option }

/// Workspace-level metadata available to plugins. Host-constructed —
/// plugins receive snapshots and never build one, which keeps adding
/// fields here binary-compatible across v1 releases.
type WorkspaceView =
    {
        RootPath: string
        /// The sidebar's selected entry (absolute path), if any.
        SelectedPath: string option
        /// Root-relative path of every file in the workspace index, in
        /// sorted tree order — the same cached list that feeds the file
        /// picker. Empty until the workspace scan completes; not capped.
        Files: string list
    }

/// The execution context handed to every plugin command. Plugins never see
/// mutable state — the host builds this fresh per invocation.
type PluginContext =
    { ActiveBuffer: BufferView
      AllBuffers: BufferView list
      Workspace: WorkspaceView }

/// Side effects a plugin can request. The host translates these into
/// core editor effects and model changes. Closed, append-only DU — new
/// cases append at the end so compiled plugins keep their union tags;
/// inserting or reordering cases is a binary break.
type PluginAction =
    | Notify of severity: Severity * message: string
    | InsertText of string
    | ReplaceSelection of string
    | MoveCursor of CursorPosition
    | OpenFile of path: string
    | SaveActiveBuffer
    | RunCommand of name: string
    | SetClipboard of string
    /// Select the range between two positions. `anchor` is the fixed end;
    /// the caret ends on `cursor` (the live end), so a follow-up
    /// `ReplaceSelection` or `MoveCursor` behaves like a shift+motion
    /// selection. Equal positions collapse to a zero-width selection.
    | SelectRange of anchor: CursorPosition * cursor: CursorPosition
    /// Open a file into the preview slot (the sidebar's Space behavior):
    /// the buffer is replaced by the next preview unless edited. An
    /// already-open file is activated instead. Relative paths resolve
    /// against the workspace root.
    | OpenFilePreview of path: string
    /// Reveal a path in the sidebar: expand its ancestors, select it, and
    /// show the sidebar without stealing focus. Paths outside the
    /// workspace (or not yet indexed) are a no-op. Relative paths resolve
    /// against the workspace root.
    | RevealPath of path: string
    /// Replace the text between two 1-based positions with `text` as a
    /// single edit — one undo entry. Ends swap when `from` is after
    /// `to_`; out-of-range coordinates clamp to the buffer. The cursor
    /// lands just after the inserted text; any selection collapses.
    | ReplaceRange of from: CursorPosition * to_: CursorPosition * text: string
    /// Collapse the active buffer's selection to a caret. No-op without
    /// a selection.
    | ClearSelection
    /// Delete the selected text as one undo entry. No-op without a
    /// selection.
    | DeleteSelection
    /// Activate the buffer with this `BufferView.Id` — the ids visible in
    /// `PluginContext.AllBuffers`. Unknown ids raise an error
    /// notification and change nothing.
    | SwitchBuffer of id: int
    /// Create a scratch buffer (no file path) holding `text`, name it
    /// `name` (empty defaults to "plugin"), and make it active. Later
    /// actions in the same list operate on the new buffer.
    | NewBuffer of name: string * text: string

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
