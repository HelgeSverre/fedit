/// Application state, messages, and effects for the MVU loop.
namespace Fedit

open System
open Fedit.PromptTypes

type EditorsState =
    {
        Buffers: Map<int, BufferState>
        ActiveBufferId: int
        NextBufferId: int
        /// Previously active buffer ids, most recent first — deduped and
        /// pruned to live buffers by the `recordBufferActivation`
        /// chokepoint in `Editor.update`. Close-buffer falls back to the
        /// head of this list when the active buffer goes away.
        ActivationHistory: int list
        /// Single VSCode-style preview slot. `Some id` while unpromoted;
        /// editing or explicit open clears it (promotes to permanent).
        /// A new preview reuses the same buffer id, replacing its content.
        PreviewBufferId: int option
        /// Plugin line-activation registry: bufferId → (source, command).
        /// When present, Enter or left-click on a line runs the registered
        /// plugin command instead of inserting/anchoring. Set via
        /// `SetBufferActivation`; cleared when the preview slot reuses
        /// the buffer id.
        BufferActivations: Map<int, string * string>
    }

type PromptMode =
    /// Empty Text, or any first character that isn't a recognised prefix.
    | FilePicker
    /// Text starts with ':' — named commands AND `:LINE[:COL]` cursor jump.
    /// The argument's first character decides: digit → goto, else → command.
    | Command
    /// Text starts with '/' — incremental search in the active buffer.
    | Search
    /// Text starts with '@' — buffer picker.
    | Buffers

type SearchPreview = { Matches: int list; Current: int }

/// Where the cursor and viewport were when the search session began.
/// Captured once when the prompt enters Search mode; Escape (cancel)
/// restores it, Enter (accept) discards it. Cleared whenever the prompt
/// leaves Search mode.
type SearchOrigin =
    { BufferId: int
      Cursor: Position
      ViewportTop: int
      ViewportLeft: int }

type PromptState =
    { Active: bool
      Session: PromptSessionKind
      Text: string
      Cursor: int
      Mode: PromptMode
      Parsed: ParsedCommand
      Completions: CompletionItem list
      SelectedCompletion: int
      SelectedItemId: string option
      History: string list
      HistoryIndex: int option
      PendingConfirmation: PromptPendingConfirmation option
      SearchPreview: SearchPreview option
      SearchOrigin: SearchOrigin option }

type PanelsState =
    { SidebarVisible: bool
      SidebarWidth: int
      DockHeight: int }

/// What the mouse wheel scrolls. See `Config.ScrollMode`.
type ScrollMode =
    /// Wheel moves the cursor line; the viewport follows (legacy behaviour).
    | ScrollLine
    /// Wheel moves the viewport; the cursor is dragged only to honour scrolloff.
    | ScrollViewport

type Config =
    {
        Theme: Theme
        Recent: string list
        DisabledPlugins: Set<string>
        CompletionLimit: int
        SidebarIndent: int
        SidebarWidth: int
        DockHeight: int
        WordMotion: WordMotionLanding
        /// Lines kept on screen between PageUp/PageDown jumps in the editor.
        /// Matches Zed / VSCode / token-editor default of 2 (jump by
        /// `viewportHeight - PageOverlap`). Set to 0 for full-screen jumps.
        PageOverlap: int
        /// Entries jumped on PageUp/PageDown in the file-tree sidebar.
        TreePageJump: int
        /// Spaces inserted by `Tab` and removed by `Shift+Tab`. Default 4.
        TabWidth: int
        /// File-tree icon style. `IconsOff` (default) keeps the ASCII
        /// markers; `IconsNerd` swaps in PUA glyphs which require the user
        /// to have a Nerd Font configured in their terminal.
        Icons: IconMode
        /// Format string for the status bar. Tokens: `[MODE]`,
        /// `[LINE]`, `[COLUMN]`, `[LINE_ENDING]`, `[BUFFER]`, `[DIRTY]`,
        /// `[NOTIFICATION]`, `[CURRENT_FILE]` / `[CURRENT_FILE:short]` /
        /// `[CURRENT_FILE:full]`. `<EXPAND>` absorbs remaining width.
        /// Unknown tokens render literally so typos are visible.
        StatusFormat: string
        /// Toggle syntax highlighting on/off. Persisted to config.json
        /// under `syntaxHighlighting`. Defaults to true; flipping to
        /// false drops all per-buffer parse state and bypasses the
        /// renderer's color-overlay pass.
        SyntaxHighlightingEnabled: bool
        /// What the mouse wheel does. `ScrollViewport` (default) scrolls the
        /// view and drags the cursor only to honour `ScrollOff`; `ScrollLine`
        /// keeps the legacy behaviour where the wheel moves the cursor line.
        ScrollMode: ScrollMode
        /// Lines kept between the cursor and the top/bottom edge (vim/helix
        /// `scrolloff`). Applies to all cursor movement. Default 5 (helix).
        ScrollOff: int
        /// Lines moved per mouse-wheel tick. Default 3 (matches nvim's
        /// `mousescroll` ver:3).
        MouseScrollLines: int
        /// Reveal (expand ancestors + select) a file in the sidebar when it is
        /// opened. Persisted as `autoReveal`. Manual `:reveal` /
        /// reveal-in-sidebar always works regardless.
        AutoReveal: bool
    }

[<RequireQualifiedAccess>]
module Config =
    let defaults theme =
        { Theme = theme
          Recent = []
          DisabledPlugins = Set.empty
          CompletionLimit = 8
          SidebarIndent = 2
          SidebarWidth = 30
          DockHeight = 8
          WordMotion = WordEnd
          PageOverlap = 2
          TreePageJump = 10
          TabWidth = 4
          Icons = IconsOff
          StatusFormat =
            "[MODE]  [CURRENT_FILE:short][DIRTY] <EXPAND> [NOTIFICATION]  [LINE]:[COLUMN]  [LINE_ENDING]  [BUFFER]"
          SyntaxHighlightingEnabled = true
          ScrollMode = ScrollViewport
          ScrollOff = 5
          MouseScrollLines = 3
          AutoReveal = true }

/// Tracks an in-progress mouse drag for click-to-select.
type MouseDragState =
    { AnchorBufferId: int
      AnchorPosition: Position }

/// One recorded macro step. Capture is semantic: actions and palette
/// command lines, never raw chords — replay re-executes outcomes, so
/// prompt/picker navigation can never end up inside a macro.
type MacroStep =
    /// Execute an editor action exactly as a keybinding would.
    /// (Qualified: `open System` above pulls in `System.Action`.)
    | RunAction of Fedit.Action
    /// Execute a palette command line (the text after `:`), parsed with
    /// the same grammar the prompt uses. The prompt itself never opens.
    | RunCommand of commandLine: string

[<RequireQualifiedAccess>]
module MacroStep =
    /// Render a step for the macro picker and replay diagnostics: the
    /// action's payload-preserving parse syntax (falling back to its
    /// display name for the few unserializable cases), or the raw palette
    /// line with its `:` prefix restored.
    let label (step: MacroStep) : string =
        match step with
        | RunAction action -> Action.toSyntax action |> Option.defaultValue (Action.name action)
        | RunCommand commandLine -> ":" + commandLine

/// The async effect families a replayed macro step can be waiting on. A
/// fenced step's completion message clears its fence, and the replay
/// queue pumps only when no fences remain — so replayed steps can never
/// outrun the async results they depend on.
type ReplayFence =
    /// `LoadFile` / `EnsureConfigFile` — cleared by `FileOpened` /
    /// `ConfigFileReady` (the latter may chain into a fresh `LoadFile`).
    | FileFence
    /// `RunSearch` — cleared by `SearchCompleted`.
    | SearchFence
    /// `ClipboardPaste` — cleared by `ClipboardPasted`.
    | PasteFence
    /// `RunPluginCommand` — cleared by `PluginActionsReady`.
    | PluginFence
    /// `ScanPlugins` — cleared by `PluginsScanned`.
    | PluginScanFence
    /// `ScanWorkspace` — cleared by `WorkspaceLoaded`.
    | WorkspaceFence

/// An entry in the replay queue: a step to run, or the closing bracket of
/// a spliced nested replay. The bracket pops its register from the
/// active-expansion set, so replaying the same register twice in sequence
/// is legal while a true cycle (a register splicing itself while its own
/// expansion is still open) is refused.
type ReplayQueueItem =
    | ReplayStep of MacroStep
    | CloseExpansion of register: char

/// In-flight macro replay, driven one queue item per `ReplayStepReady`
/// message (posted back through the runtime queue by the `ReplayPump`
/// effect) so live input and effect completions interleave fairly.
type ReplayState =
    {
        /// Items still to run in the current iteration, front first.
        Queue: ReplayQueueItem list
        /// The register's full program; reloaded into `Queue` between
        /// iterations.
        Steps: MacroStep list
        /// Iterations left, counting the one in flight. 1 = last.
        RemainingIterations: int
        /// The register being replayed (diagnostics + cycle-guard root).
        Register: char
        /// Fences still open for the last executed step; the queue pumps
        /// only when this empties. See `ReplayFence`.
        PendingFences: Set<ReplayFence>
        /// The fenced step, for the timeout diagnostic.
        WaitingStep: MacroStep option
        /// Registers whose splice is still open in `Queue` (cycle guard).
        ActiveExpansions: Set<char>
        /// Open splice count, capped so runaway nesting cancels cleanly.
        ExpansionDepth: int
    }

type Model =
    {
        Workspace: WorkspaceState
        Editors: EditorsState
        Prompt: PromptState
        Panels: PanelsState
        Focus: FocusTarget
        Terminal: Size
        Notification: Notification option
        /// Ring of the last `Notification.logLimit` notifications shown,
        /// newest first. Appended only by the `notify` chokepoint in
        /// `Editor` — every surfaced message goes through it, so the
        /// `:messages` picker can never miss one.
        NotificationLog: Notification list
        Config: Config
        UserThemes: Theme list
        Plugins: PluginRegistry
        /// Per-buffer syntax spans, keyed by `BufferState.Id`. Pure data —
        /// produced by the `ParseHighlight` effect interpreter (which owns
        /// the native tree-sitter objects); stale completions are dropped
        /// by edit tick in `update`. Empty when the grammar registry failed
        /// to load — the renderer just skips the color overlay.
        HighlightStates: Map<int, HighlightSpan array>
        QuitArmed: bool
        /// `Some bufferId` after close-buffer warned about unsaved changes
        /// in that buffer; the next close of the same buffer discards.
        /// Same two-step confirmation pattern as `QuitArmed`; both disarm
        /// via the `disarmStaleConfirmations` chokepoint in `Editor.update`.
        CloseArmed: int option
        ShouldQuit: bool
        /// Effective keymap: `Keymap.defaults` overlaid by the user's
        /// `~/.config/fedit/keybinds` file. Carries defaults from `init` so the
        /// editor is fully functional before the async `LoadKeybinds` lands.
        Keymap: Keymap
        /// In-flight multi-key sequence: the chords accumulated so far.
        /// `None` when no sequence is pending. Rendered in the status bar
        /// and as the which-key dock panel. The abandon deadline (3 s —
        /// reading time for that panel) lives in the Runtime (it owns the
        /// clock — `update` stays deterministic); the runtime posts
        /// `SequenceTimedOut` when it expires.
        PendingPrefix: Chord list option
        /// Named macro registers: register char → recorded steps in
        /// execution order. Written only when a recording stops with at
        /// least one captured step. Persisted to `~/.config/fedit/macros`
        /// (see `MacroIO`): loaded at startup like the keybinds file, and
        /// written through whenever a recording commits or a register is
        /// cleared. `LastMacro` is deliberately not persisted.
        Registers: Map<char, MacroStep list>
        /// `Some r` while recording into register `r`; `None` otherwise.
        /// The semantic-capture chokepoints key off this.
        Recording: char option
        /// Steps captured since recording started, in order. Committed to
        /// the register on a non-empty stop, discarded on an empty stop —
        /// so a double-toggle can never wipe a register.
        RecordingSteps: MacroStep list
        /// In-flight macro replay; `None` when idle. See `ReplayState`.
        Replay: ReplayState option
        /// The last register replayed or finished recording, for
        /// "repeat last macro".
        LastMacro: char option
        /// In-progress mouse drag anchor. `None` when no drag is active.
        /// Set on left-button press in the editor, cleared on release.
        MouseDrag: MouseDragState option
        /// The last search query accepted with Enter in the search prompt.
        /// `search-next` / `search-previous` (F3 / Shift+F3) repeat it from
        /// the cursor without reopening the prompt.
        LastSearchQuery: string option
    }

/// Why a file is being loaded: a normal open, or a preview into the
/// single reusable preview slot. Travels with the LoadFile effect and
/// returns on FileOpened so intent can never decouple from the result.
type OpenIntent =
    | OpenPermanent
    | OpenPreview

/// Why a `LoadFile` read failed. Classified by the interpreter so the
/// editor can distinguish "the path simply isn't there" — a permanent
/// `:open` treats that as creating a new file — from real I/O errors
/// (permissions, the path is a directory), which stay errors.
type FileOpenError =
    /// The file, or a directory in its path, does not exist.
    | FileNotFound
    /// Any other I/O failure, carrying the exception message.
    | FileOpenFailed of message: string

type Msg =
    | KeyPressed of Chord
    /// The pending multi-key sequence prefix timed out; clear it.
    | SequenceTimedOut
    | Resize of Size
    /// Mouse wheel scrolled by N ticks (signed; negative = up) at a screen
    /// position. An ambient input event like `Resize` — handled in `update`,
    /// not a keystroke, so it stays outside the keybinding / `Action` layer.
    /// The position routes the scroll to the surface under the pointer
    /// (sidebar vs editor).
    | MouseScrolled of ticks: int * position: Position
    /// `clickCount` is synthesized by the Runtime (the double-click window
    /// is a wall-clock decision, so it lives beside `prefixDeadline`): rapid
    /// presses on the same cell count 1, 2, 3, … `update` maps 2 to
    /// word-selection and 3 to line-selection; anything else places the
    /// cursor.
    | MousePressed of event: MouseEvent * clickCount: int
    | MouseReleased of MouseEvent
    | MouseDragged of MouseEvent
    | FocusGained
    | FocusLost
    | WorkspaceLoaded of Result<FileNode * Map<string, FileNode> * string list * int, string>
    /// `target` is an optional 0-based cursor position applied once the
    /// buffer exists (plugin `OpenFileAt`): it travels with the LoadFile
    /// effect and returns here so the jump survives the async load.
    | FileOpened of path: string * intent: OpenIntent * target: Position option * Result<string, FileOpenError>
    | BufferSaved of bufferId: int * path: string * revision: int * Result<unit, string>
    | ConfigSaved of Result<unit, string>
    /// The config file is on disk (written if missing): Ok carries its path
    /// for the follow-up open; Error carries the write failure.
    | ConfigFileReady of Result<string, string>
    | ClipboardCopied of Result<unit, string>
    | ClipboardPasted of Result<string, string>
    /// A bracketed-paste payload arrived from the terminal (DECSET 2004).
    /// Unlike `ClipboardPasted` it is not the result of an effect — the
    /// terminal pushes it when the user pastes natively.
    | PastedText of string
    | SearchCompleted of bufferId: int * query: string * matches: int list
    | WorkspaceChangedExternally
    /// Run the next queued macro replay item. Posted by the `ReplayPump`
    /// effect interpreter — round-tripping through the queue keeps replay
    /// steps fair with live input and effect completions.
    | ReplayStepReady
    /// A fenced replay step's async result never arrived (runtime wall
    /// clock, ~5 s): cancel the replay with an error naming the step.
    | ReplayFenceTimeout
    /// Spans for `bufferId` as of `editTick`. Stale ticks are dropped —
    /// a newer `ParseHighlight` is already in flight for the newer text.
    | HighlightParsed of bufferId: int * editTick: int * spans: HighlightSpan array
    | PluginsScanned of Result<PluginRegistry, string>
    /// A plugin command finished in the host: its PluginAction list to apply,
    /// or an error to surface. Posted by the `RunPluginCommand` interpreter.
    | PluginActionsReady of source: string * Result<Fedit.PluginApi.PluginAction list, string>
    | PluginInstalled of name: string * Result<unit, string>
    | PluginRemoved of name: string * Result<unit, string>
    | PluginBuildFinished of name: string * Result<unit, string>
    /// `:plugin validate` finished: Ok and Error both carry the report text.
    | PluginValidated of Result<string, string>
    /// The user keybinds file was (re)loaded: the effective keymap plus any
    /// parse/conflict errors to surface as a notification.
    | KeybindsLoaded of Keymap * string list
    /// The macros file was (re)loaded: the registers plus any per-line
    /// parse errors (each naming its line number). `announce` is true for
    /// a reload after saving the file — that surfaces a "Macros reloaded"
    /// notification; the silent startup load doesn't.
    | MacrosLoaded of registers: Map<char, MacroStep list> * errors: string list * announce: bool
    /// The write-through macro save finished: Ok is silent, Error surfaces
    /// as a warning (the in-memory registers stay valid either way).
    | MacrosSaved of Result<unit, string>
    /// The macros file is on disk (written with the commented grammar
    /// header if missing): Ok carries its path for the follow-up open;
    /// Error carries the write failure. Mirrors `ConfigFileReady`.
    | MacrosFileReady of Result<string, string>

type Effect =
    | ScanWorkspace of string
    | LoadFile of path: string * intent: OpenIntent * target: Position option
    | SaveBuffer of bufferId: int * path: string * revision: int * contents: string
    | SaveConfig of Config
    /// Write the default config file if it doesn't exist yet, posting
    /// `ConfigFileReady` with the path so the editor can open it.
    | EnsureConfigFile of Config
    | ClipboardCopy of string
    | ClipboardPaste
    /// Carries the (immutable, cheap-to-share) piece table rather than the
    /// rendered text: the interpreter does the `toString` on a pool thread,
    /// so a search keystroke costs the pure update loop nothing.
    | RunSearch of bufferId: int * query: string * document: PieceTable
    /// Parse syntax spans off the UI thread. Carries the piece table
    /// (immutable, cheap to share); the interpreter materializes the text,
    /// parses, and posts `HighlightParsed` tagged with `editTick`.
    | ParseHighlight of bufferId: int * language: string * document: PieceTable * editTick: int
    | ScanPlugins of disabledPlugins: Set<string>
    /// Invoke a plugin command in the out-of-process host. Carries the
    /// read-only context snapshot; the host runs the command's Run closure and
    /// the interpreter posts `PluginActionsReady`.
    | RunPluginCommand of source: string * command: string * context: Fedit.PluginApi.PluginContext
    | InstallPluginFromSource of source: PluginSource
    | RemovePluginDir of name: string
    | BuildPlugin of pluginPath: string
    /// Check a plugin folder's manifest (existence + parse) and post the
    /// report as `PluginValidated`.
    | ValidatePlugin of path: string
    | LoadKeybinds
    /// Read + parse the macros file, posting `MacrosLoaded` (with the
    /// same `announce` flag). Mirrors `LoadKeybinds`.
    | LoadMacros of announce: bool
    /// Write the registers to the macros file in canonical form. Emitted
    /// whenever a recording commits or a register is cleared; the
    /// interpreter serializes these writes (config-save pattern) so quick
    /// successive saves cannot interleave on disk.
    | SaveMacros of registers: Map<char, MacroStep list>
    /// Write the macros file if it doesn't exist yet (commented grammar
    /// header + the current registers), posting `MacrosFileReady` with the
    /// path so the editor can open it. Mirrors `EnsureConfigFile`.
    | EnsureMacrosFile of registers: Map<char, MacroStep list>
    /// Post `ReplayStepReady` back through the runtime queue. Pure queue
    /// manipulation — no I/O — but routed as an effect so replay stepping
    /// interleaves with pending input instead of recursing inside `update`.
    | ReplayPump
