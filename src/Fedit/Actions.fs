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
    /// Drop the selection, leaving the cursor where it is.
    | ClearSelection
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
    | MoveLinesUp of count: int
    | MoveLinesDown of count: int
    // commands
    | Save
    | SaveAs of string
    /// Quit with the shared dirty-buffer guard: with unsaved changes the
    /// first invocation warns and arms; the second discards and quits.
    | Quit
    /// Quit unconditionally, discarding unsaved changes.
    | ForceQuit
    /// Close the active buffer, with the same two-step dirty confirmation
    /// as `Quit`. Closing the last buffer leaves a fresh scratch buffer.
    | CloseBuffer
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
    /// Reveal the active buffer's file in the sidebar: show the panel,
    /// expand its ancestor directories, select it, and focus the tree.
    | RevealInSidebar
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
    // macros
    | RecordMacro of register: char
    | ReplayMacro of register: char * count: int
    | RepeatLastMacro

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
        | Command.ForceQuit -> Some Action.ForceQuit
        | Command.Close None -> Some Action.CloseBuffer
        | Command.NextBuffer -> Some Action.NextBuffer
        | Command.PreviousBuffer -> Some Action.PrevBuffer
        | Command.ReloadWorkspace -> Some Action.ReloadWorkspace
        | Command.ToggleSidebar -> Some Action.ToggleSidebar
        | Command.FocusTree -> Some Action.FocusSidebar
        | Command.Reveal -> Some Action.RevealInSidebar
        | _ -> None

    /// Stable kebab-case name per action, payload ignored. AOT-safe (a plain
    /// match, no `sprintf "%A"` reflection) and shared by the `:keybind`
    /// introspection, the keybind-help renderer, the chord picker, and the
    /// `fedit keybinds` CLI so all four render an action identically.
    let name (action: Action) : string =
        match action with
        | MoveLeft -> "move-left"
        | MoveRight -> "move-right"
        | MoveUp -> "move-up"
        | MoveDown -> "move-down"
        | MoveWordLeft -> "move-word-left"
        | MoveWordRight -> "move-word-right"
        | MoveHome -> "move-home"
        | MoveEnd -> "move-end"
        | MovePageUp -> "page-up"
        | MovePageDown -> "page-down"
        | ExtendLeft -> "extend-left"
        | ExtendRight -> "extend-right"
        | ExtendUp -> "extend-up"
        | ExtendDown -> "extend-down"
        | ExtendHome -> "extend-home"
        | ExtendEnd -> "extend-end"
        | SelectAll -> "select-all"
        | ClearSelection -> "clear-selection"
        | Indent -> "indent"
        | Unindent -> "unindent"
        | DeleteWordBack -> "delete-word-back"
        | DeleteWordForward -> "delete-word-forward"
        | Undo -> "undo"
        | Redo -> "redo"
        | Copy -> "copy"
        | Cut -> "cut"
        | Paste -> "paste"
        | MoveLinesUp _ -> "move-lines-up"
        | MoveLinesDown _ -> "move-lines-down"
        | Save -> "save"
        | SaveAs _ -> "save-as"
        | Quit -> "quit"
        | ForceQuit -> "force-quit"
        | CloseBuffer -> "close-buffer"
        | OpenPalette -> "command-palette"
        | OpenFilePicker -> "open-file"
        | OpenSearch -> "search"
        | NextBuffer -> "next-buffer"
        | PrevBuffer -> "prev-buffer"
        | JumpToBuffer _ -> "jump-to-buffer"
        | SetTheme _ -> "set-theme"
        | Goto _ -> "goto"
        | ReloadWorkspace -> "reload-workspace"
        | OpenConfig -> "open-config"
        | ReloadKeybinds -> "reload-keybinds"
        | RunPlugin _ -> "run-plugin"
        | RevealSidebar -> "reveal-sidebar"
        | HideSidebar -> "hide-sidebar"
        | ToggleSidebar -> "toggle-sidebar"
        | FocusSidebar -> "focus-sidebar"
        | FocusEditor -> "focus-editor"
        | RevealInSidebar -> "reveal-in-sidebar"
        | SidebarUp -> "sidebar-up"
        | SidebarDown -> "sidebar-down"
        | SidebarPageUp -> "sidebar-page-up"
        | SidebarPageDown -> "sidebar-page-down"
        | SidebarTop -> "sidebar-top"
        | SidebarBottom -> "sidebar-bottom"
        | SidebarCollapse -> "sidebar-collapse"
        | SidebarExpand -> "sidebar-expand"
        | SidebarActivate -> "sidebar-activate"
        | Chain _ -> "chain"
        | When _ -> "when"
        | NoOp -> "no-op"
        | RecordMacro _ -> "record-macro"
        | ReplayMacro _ -> "replay-macro"
        | RepeatLastMacro -> "repeat-last-macro"
