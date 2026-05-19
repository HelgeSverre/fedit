namespace Fedit

open System

type EditorsState =
    { Buffers: Map<int, BufferState>
      ActiveBufferId: int
      NextBufferId: int }

type SearchState =
    { Query: string
      Matches: int list
      Current: int }

type CommandBarState =
    { Active: bool
      Text: string
      Cursor: int
      Parsed: ParsedCommand
      Completions: CompletionItem list
      SelectedCompletion: int
      History: string list
      HistoryIndex: int option
      PreviewTheme: Theme option }

type PanelsState =
    { SidebarVisible: bool
      SidebarWidth: int
      DockHeight: int }

type Config =
    { Theme: Theme
      Recent: string list
      CompletionLimit: int
      SidebarIndent: int
      SidebarWidth: int
      DockHeight: int
      WordMotion: WordMotionLanding }

[<RequireQualifiedAccess>]
module Config =
    let defaults theme =
        { Theme = theme
          Recent = []
          CompletionLimit = 8
          SidebarIndent = 2
          SidebarWidth = 30
          DockHeight = 5
          WordMotion = WordEnd }

type Model =
    { Workspace: WorkspaceState
      Editors: EditorsState
      CommandBar: CommandBarState
      Panels: PanelsState
      Focus: FocusTarget
      Terminal: Size
      Notification: Notification option
      Config: Config
      UserThemes: Theme list
      Search: SearchState option
      ShowHelp: bool
      QuitArmed: bool
      ShouldQuit: bool }

type Msg =
    | KeyPressed of KeyInput
    | Resize of Size
    | WorkspaceLoaded of Result<FileNode * int, string>
    | FileOpened of path: string * Result<string, string>
    | BufferSaved of bufferId: int * path: string * Result<unit, string>
    | ConfigSaved of Result<unit, string>
    | ClipboardCopied of Result<unit, string>
    | ClipboardPasted of Result<string, string>
    | WorkspaceChangedExternally

type Effect =
    | ScanWorkspace of string
    | LoadFile of string
    | SaveBuffer of bufferId: int * path: string * contents: string
    | SaveConfig of Config
    | ClipboardCopy of string
    | ClipboardPaste
