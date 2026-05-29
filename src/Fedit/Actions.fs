namespace Fedit

/// Boolean predicate over the model, evaluated by `Editor.evalCond`.
/// Closed DU on purpose — this is NOT a VS Code-style open `when` language.
type Cond =
    | SidebarVisible
    | SidebarFocused
    | EditorFocused
    | PromptActive
    | HasSelection
    | BufferDirty
    | Not of Cond
    | AllOf of Cond list
    | AnyOf of Cond list

/// The single named vocabulary of everything a keybinding can trigger.
/// Pure data — no `Model` reference — so it compiles below `Editor`.
/// `Editor.runAction` is the one interpreter.
type Action =
    // motion / selection
    | MoveLeft
    | MoveRight
    | MoveUp
    | MoveDown
    | MoveWordLeft
    | MoveWordRight
    | MoveHome
    | MoveEnd
    | MovePageUp
    | MovePageDown
    | ExtendLeft
    | ExtendRight
    | ExtendUp
    | ExtendDown
    | ExtendHome
    | ExtendEnd
    | SelectAll
    // editing
    | Indent
    | Unindent
    | DeleteWordBack
    | DeleteWordForward
    | Undo
    | Redo
    | Copy
    | Cut
    | Paste
    // commands
    | Save
    | SaveAs of string
    | Quit
    | OpenPalette
    | OpenFilePicker
    | OpenSearch
    | NextBuffer
    | PrevBuffer
    | JumpToBuffer of int
    | SetTheme of string
    | Goto of line: int * col: int option
    | ReloadWorkspace
    | OpenConfig
    | ReloadKeybinds
    | RunPlugin of source: string * name: string * arg: string
    // panel / focus primitives — each a COMPLETE, valid transition
    | RevealSidebar
    | HideSidebar
    | ToggleSidebar
    | FocusSidebar
    | FocusEditor
    // sidebar navigation
    | SidebarUp
    | SidebarDown
    | SidebarPageUp
    | SidebarPageDown
    | SidebarTop
    | SidebarBottom
    | SidebarCollapse
    | SidebarExpand
    | SidebarActivate
    // composition & control flow
    | Chain of Action list
    | When of cond: Cond * thenDo: Action * elseDo: Action
    | NoOp
    // deferred — bind/parse-able later, but no-ops until macros/keymap land
    | RecordMacro of register: char
    | ReplayMacro of register: char * count: int

[<RequireQualifiedAccess>]
module Action =
    /// Map the command verbs that have an exact chord-action equivalent.
    /// `None` for prompt-only / divergent verbs, which executeCommand keeps.
    /// RHS cases are qualified `Action.*` because Action and Command share
    /// several case names.
    let ofCommand (command: Command) : Action option =
        match command with
        | Command.Write -> Some Action.Save
        | Command.WriteAs path -> Some(Action.SaveAs path)
        | Command.Quit -> Some Action.Quit
        | Command.NextBuffer -> Some Action.NextBuffer
        | Command.PreviousBuffer -> Some Action.PrevBuffer
        | Command.ReloadWorkspace -> Some Action.ReloadWorkspace
        | Command.ToggleSidebar -> Some Action.ToggleSidebar
        | Command.FocusTree -> Some Action.FocusSidebar
        | _ -> None
