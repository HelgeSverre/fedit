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

/// A language-server diagnostic for a buffer, 1-based positions.
type Diagnostic =
    { Severity: Severity
      Message: string
      Source: string option
      Start: CursorPosition
      End: CursorPosition }

/// A read-only snapshot of a buffer at the moment a plugin command runs.
type BufferView =
    {
        Id: int
        Name: string
        FilePath: string option
        Text: string
        Cursor: CursorPosition
        Selection: (CursorPosition * CursorPosition) option
        /// Highlight language id (`fsharp`, `markdown`, …), None when unknown.
        Language: string option
        /// Unsaved changes since the last write.
        Dirty: bool
        /// Increments on every edit; compare snapshots to detect changes.
        EditTick: int
        /// Current language-server diagnostics for this buffer.
        Diagnostics: Diagnostic list
    }

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

/// Editor events a plugin can hook with `IPluginHost.RegisterHook`. The
/// hooked command runs with `PluginContext.Event` set, after the editor
/// has applied the change; actions it returns never fire hooks again.
type PluginEvent =
    /// A buffer was written to disk (the active buffer in the context).
    | BufferSaved
    /// A file was opened into a new buffer (the active buffer).
    | BufferOpened
    /// The active buffer's text changed. Fires per edit; keep handlers cheap.
    | BufferChanged
    /// Keyboard focus moved between the editor, sidebar and prompt.
    | FocusChanged

/// The execution context handed to every plugin command. Plugins never see
/// mutable state — the host builds this fresh per invocation.
type PluginContext =
    {
        ActiveBuffer: BufferView
        AllBuffers: BufferView list
        Workspace: WorkspaceView
        /// Set when the command runs as an event hook; None for direct calls.
        Event: PluginEvent option
        /// This plugin's own settings: the `plugins.<name>` object in
        /// config.json, values as strings (numbers and booleans stringified).
        Config: Map<string, string>
        /// Input for this run: the text after the command name in the prompt
        /// (`:mycmd foo bar` gives `Some "foo bar"`), the id of the entry
        /// chosen from a `ShowPicker`, or the text submitted to a
        /// `PromptInput`. None when the command ran bare.
        Argument: string option
    }

/// One row of a plugin picker (`PluginAction.ShowPicker`).
type PickerEntry =
    { Id: string
      Title: string
      Subtitle: string option }

/// A theme slot for panel text. The host maps it onto the active theme so
/// plugin output follows the user's palette instead of hardcoding colors.
[<RequireQualifiedAccess>]
type TextStyle =
    | Plain
    | Accent
    | Muted
    | Error
    | Warning
    | Keyword
    | String

/// A run of text in one style. A panel line is a list of segments.
type Segment = { Text: string; Style: TextStyle }

/// A per-line annotation: a one-character gutter mark, text appended
/// after the line (virtual text), or both. `Line` is 1-based.
type Decoration =
    { Line: int
      Gutter: string option
      Text: string option
      Style: TextStyle }

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
    /// Register `commandName` (a command this plugin registered) to run
    /// whenever a line of the ACTIVE buffer is activated — Enter, or a
    /// left click on a line. The command runs with the normal snapshot
    /// context; the cursor sits on the activated line. Intended for
    /// listing buffers created with `NewBuffer`. The registration lives
    /// for the buffer's lifetime and is replaced by the next
    /// SetBufferActivation targeting the same buffer.
    | SetBufferActivation of commandName: string
    /// Open a file and place the cursor at a 1-based position once the
    /// file is loaded — unlike a MoveCursor after OpenFile/OpenFilePreview,
    /// the position survives the asynchronous load and also applies when
    /// the file is already open. Out-of-range positions clamp. `preview`
    /// selects the preview slot (sidebar-Space semantics) vs a permanent
    /// buffer. Relative paths resolve against the workspace root.
    | OpenFileAt of path: string * position: CursorPosition * preview: bool
    /// Move the current line, or every line containing selected text, up by
    /// `count` places as one undoable edit. Movement clamps at the top of the
    /// buffer. A selection ending at column 1 does not include that final
    /// line. Non-positive counts are a no-op.
    | MoveLinesUp of count: int
    /// Move the current line, or every line containing selected text, down by
    /// `count` places as one undoable edit. Movement clamps at the bottom of
    /// the buffer. A selection ending at column 1 does not include that final
    /// line. Non-positive counts are a no-op.
    | MoveLinesDown of count: int
    /// Show a titled panel in the dock (the area below the editor) with
    /// styled lines. The panel stays until the user presses Escape, a
    /// prompt takes the dock, or a later ShowPanel replaces it; an empty
    /// `lines` list closes it. Lines beyond the dock height are cut.
    | ShowPanel of title: string * lines: Segment list list
    /// Set (or with None clear) this plugin's status-bar text, rendered by
    /// the `[PLUGINS]` status token. One item per plugin; the latest wins.
    | SetStatusItem of text: string option
    /// Open a filterable picker of `items`. Choosing one runs `onSelect`
    /// (a command this plugin registered) with `PluginContext.Argument`
    /// set to the entry's `Id`; Escape closes without running anything.
    | ShowPicker of title: string * items: PickerEntry list * onSelect: string
    /// Ask the user for a line of text. Enter runs `onSubmit` with the text
    /// as `PluginContext.Argument`; Escape cancels.
    | PromptInput of label: string * initial: string * onSubmit: string
    /// Replace this plugin's decorations on buffer `bufferId` (a
    /// `BufferView.Id`); an empty list clears them. Decorations follow line
    /// numbers, not text, so refresh them from a `BufferChanged` hook when
    /// they must track edits. Unknown buffers are ignored.
    | SetDecorations of bufferId: int * decorations: Decoration list

/// A command definition a plugin registers with the host. `Run` is invoked
/// synchronously when the command fires; it should be fast (< 50ms).
type PluginCommand =
    { Name: string
      Usage: string
      Summary: string
      Run: PluginContext -> PluginAction list }

/// An asynchronous command. `RunAsync` runs on the host's thread pool and
/// may take as long as it needs — other plugin calls proceed meanwhile.
/// The token is cancelled when the host shuts down or the editor cancels
/// the request; honour it in long loops and I/O.
type PluginAsyncCommand =
    { Name: string
      Usage: string
      Summary: string
      RunAsync: PluginContext -> System.Threading.CancellationToken -> System.Threading.Tasks.Task<PluginAction list> }

/// A language server a plugin makes available, exactly the shape of a
/// `languageServers` entry in config.json. A user entry with the same
/// name wins.
type LanguageServerSpec =
    {
        Name: string
        Command: string
        Args: string list
        /// Extensions without the dot (`fs`, `ts`).
        FileTypes: string list
        /// Files or directories whose presence marks a workspace root.
        RootMarkers: string list
    }

/// A tree-sitter grammar a plugin ships, exactly the shape of a
/// `languages` entry in config.json. Relative paths resolve against the
/// plugin's folder.
type GrammarSpec =
    {
        Name: string
        /// Extensions with or without the dot.
        Extensions: string list
        /// Path to the grammar shared library (`libtree-sitter-x.dylib`).
        Library: string
        /// Entry symbol; defaults to `tree_sitter_<library stem>`.
        Symbol: string option
        /// Directory holding `highlights.scm` and optionally `injections.scm`.
        Queries: string option
    }

/// Keyboard chord a plugin can bind to a command name. MVP supports
/// modifier+character and function keys. Plain `Char` is reserved (basic
/// text input); the host rejects those registrations with a warning.
type KeyChord =
    | Char of char
    | Ctrl of char
    | Alt of char
    | CtrlShift of char
    | F of int
