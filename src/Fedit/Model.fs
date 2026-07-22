/// Application state, messages, and effects for the MVU loop.
namespace Fedit

open System
open Fedit.PromptTypes

type EditorsState =
    {
        Buffers: Map<int, BufferState>
        ActiveBufferId: int
        NextBufferId: int
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
      SearchPreview: SearchPreview option }

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
        /// Language servers available to the LSP layer: the built-in
        /// defaults merged with the user's `languageServers` config block
        /// (a user entry with a default's name replaces it entirely).
        LanguageServers: LanguageServerConfig list
        /// Server names the user has switched off. Persisted as
        /// `disabledLanguageServers`, exactly like `disabledPlugins`.
        DisabledLanguageServers: Set<string>
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
          LanguageServers = LanguageServers.defaults
          DisabledLanguageServers = Set.empty
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

type Model =
    {
        Workspace: WorkspaceState
        Editors: EditorsState
        Prompt: PromptState
        Panels: PanelsState
        Focus: FocusTarget
        Terminal: Size
        Notification: Notification option
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
        ShouldQuit: bool
        /// Effective keymap: `Keymap.defaults` overlaid by the user's
        /// `~/.config/fedit/keybinds` file. Carries defaults from `init` so the
        /// editor is fully functional before the async `LoadKeybinds` lands.
        Keymap: Keymap
        /// In-flight multi-key sequence: the chords accumulated so far.
        /// `None` when no sequence is pending. Rendered in the status bar.
        /// The abandon-after-1s deadline lives in the Runtime (it owns the
        /// clock — `update` stays deterministic); the runtime posts
        /// `SequenceTimedOut` when it expires.
        PendingPrefix: Chord list option
        /// Named macro registers. Key is the register char (e.g. 'a'); value is
        /// the chords recorded into it, in press order. In-memory only this
        /// phase — not persisted to the keybinds file.
        Registers: Map<char, Chord list>
        /// `Some r` while recording into register `r`; `None` otherwise. The
        /// record-append hook in `update` keys off this.
        Recording: char option
        /// True while a macro replay is in flight (bracketed by
        /// `MacroReplayStart`/`MacroReplayEnd`). The record-append hook checks
        /// it so injected keys are never re-recorded.
        Replaying: bool
        /// The last register replayed or finished recording, for
        /// "repeat last macro".
        LastMacro: char option
        /// In-progress mouse drag anchor. `None` when no drag is active.
        /// Set on left-button press in the editor, cleared on release.
        MouseDrag: MouseDragState option
    }

/// Why a file is being loaded: a normal open, or a preview into the
/// single reusable preview slot. Travels with the LoadFile effect and
/// returns on FileOpened so intent can never decouple from the result.
type OpenIntent =
    | OpenPermanent
    | OpenPreview

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
    | MousePressed of MouseEvent
    | MouseReleased of MouseEvent
    | MouseDragged of MouseEvent
    | FocusGained
    | FocusLost
    | WorkspaceLoaded of Result<FileNode * Map<string, FileNode> * string list * int, string>
    /// `target` is an optional 0-based cursor position applied once the
    /// buffer exists (plugin `OpenFileAt`): it travels with the LoadFile
    /// effect and returns here so the jump survives the async load.
    | FileOpened of path: string * intent: OpenIntent * target: Position option * Result<string, string>
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
    /// Brackets a macro replay batch so the record-append hook can suppress
    /// recording of injected keys (sets/clears `Model.Replaying`). Enqueued by
    /// the `ReplayKeys` effect interpreter around the injected `KeyPressed`s.
    | MacroReplayStart
    | MacroReplayEnd
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
    /// Replay a recorded macro: re-enqueue these chords as `KeyPressed` msgs
    /// `count` times. Interpreted in `Runtime.startEffect` (it owns the queue),
    /// bracketed by `MacroReplayStart`/`MacroReplayEnd`.
    | ReplayKeys of chords: Chord list * count: int
