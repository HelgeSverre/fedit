namespace Fedit

open System

type EditorsState =
    { Buffers: Map<int, BufferState>
      ActiveBufferId: int
      NextBufferId: int }

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
      Text: string
      Cursor: int
      Mode: PromptMode
      Parsed: ParsedCommand
      Completions: CompletionItem list
      SelectedCompletion: int
      History: string list
      HistoryIndex: int option
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
    }

[<RequireQualifiedAccess>]
module Config =
    let defaults theme =
        { Theme = theme
          Recent = []
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
          MouseScrollLines = 3 }

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
        /// Process-wide tree-sitter registry. `None` if the native
        /// `libtree-sitter-fsharp.*` failed to load at startup; in that
        /// case `HighlightStates` stays empty and the renderer skips the
        /// color overlay.
        HighlightRegistry: HighlightRegistry option
        /// Per-buffer parse state, keyed by `BufferState.Id`. Owned: the
        /// runtime disposes every value on shutdown.
        HighlightStates: Map<int, HighlightState>
        QuitArmed: bool
        ShouldQuit: bool
    }

type Msg =
    | KeyPressed of KeyInput
    | Resize of Size
    /// Mouse wheel scrolled by N ticks (signed; negative = up). An ambient
    /// input event like `Resize` — handled in `update`, not a keystroke, so
    /// it stays outside the keybinding / `Action` layer.
    | MouseScrolled of int
    | WorkspaceLoaded of Result<FileNode * int, string>
    | FileOpened of path: string * Result<string, string>
    | BufferSaved of bufferId: int * path: string * revision: int * Result<unit, string>
    | ConfigSaved of Result<unit, string>
    | ClipboardCopied of Result<unit, string>
    | ClipboardPasted of Result<string, string>
    | SearchCompleted of bufferId: int * query: string * matches: int list
    | WorkspaceChangedExternally
    | PluginsScanned of Result<PluginRegistry, string>
    | PluginInstalled of name: string * Result<unit, string>
    | PluginRemoved of name: string * Result<unit, string>
    | PluginBuildFinished of name: string * Result<unit, string>

type Effect =
    | ScanWorkspace of string
    | LoadFile of string
    | SaveBuffer of bufferId: int * path: string * revision: int * contents: string
    | SaveConfig of Config
    | ClipboardCopy of string
    | ClipboardPaste
    | RunSearch of bufferId: int * query: string * haystack: string
    | ScanPlugins
    | InstallPluginFromSource of source: PluginSource
    | RemovePluginDir of name: string
    | BuildPlugin of pluginPath: string
