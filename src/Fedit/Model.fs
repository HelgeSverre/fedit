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
          Icons = IconsOff }

type Model =
    { Workspace: WorkspaceState
      Editors: EditorsState
      Prompt: PromptState
      Panels: PanelsState
      Focus: FocusTarget
      Terminal: Size
      Notification: Notification option
      Config: Config
      UserThemes: Theme list
      QuitArmed: bool
      ShouldQuit: bool }

type Msg =
    | KeyPressed of KeyInput
    | Resize of Size
    | WorkspaceLoaded of Result<FileNode * int, string>
    | FileOpened of path: string * Result<string, string>
    | BufferSaved of bufferId: int * path: string * revision: int * Result<unit, string>
    | ConfigSaved of Result<unit, string>
    | ClipboardCopied of Result<unit, string>
    | ClipboardPasted of Result<string, string>
    | SearchCompleted of bufferId: int * query: string * matches: int list
    | WorkspaceChangedExternally

type Effect =
    | ScanWorkspace of string
    | LoadFile of string
    | SaveBuffer of bufferId: int * path: string * revision: int * contents: string
    | SaveConfig of Config
    | ClipboardCopy of string
    | ClipboardPaste
    | RunSearch of bufferId: int * query: string * haystack: string
