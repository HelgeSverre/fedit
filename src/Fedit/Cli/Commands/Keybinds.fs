/// `fedit keybinds [--json]` — dump the compiled-in default keymap.
///
/// `--json` emits a JSON array the website's keybindings grid consumes;
/// bare prints a human-readable aligned table. Serializes
/// `Keymap.defaults` ONLY — never the user `~/.config/fedit/keybinds`
/// overlay. The action names are the inverse of `Keymap.parseAction`;
/// descriptions and categories are authored prose (the F# DSL carries
/// neither).
///
/// `--json` additionally appends every `Action` with no default binding
/// (`bound: false`, empty stroke/context) so the website can list the
/// unbound actions a user might want to map. The bare table and the
/// in-editor `:keybind` buffer stay bound-only.
module Fedit.Cli.Commands.Keybinds

open System.Text
open Fedit
open Fedit.Cli

/// Stable kebab name per `Action` case — the inverse of `parseAction` in
/// `Keymap.fs`. Exhaustive so a new Action forces a compile-time choice.
let actionName (action: Action) : string = Action.name action

/// `(category, description)` one-liner per action for the website grid.
/// Lead with a verb; no emoji, no marketing words (brand/voice.md). The
/// match is exhaustive, so a new Action case forces a compile-time update
/// here rather than slipping through silently.
let actionMeta (action: Action) : string * string =
    match action with
    | MoveLeft -> "motion", "Move the cursor left one column"
    | MoveRight -> "motion", "Move the cursor right one column"
    | MoveUp -> "motion", "Move the cursor up one line"
    | MoveDown -> "motion", "Move the cursor down one line"
    | MoveWordLeft -> "motion", "Move the cursor left one word"
    | MoveWordRight -> "motion", "Move the cursor right one word"
    | MoveHome -> "motion", "Move the cursor to the start of the line"
    | MoveEnd -> "motion", "Move the cursor to the end of the line"
    | MovePageUp -> "motion", "Scroll up one page"
    | MovePageDown -> "motion", "Scroll down one page"
    | ExtendLeft -> "selection", "Extend the selection left one column"
    | ExtendRight -> "selection", "Extend the selection right one column"
    | ExtendUp -> "selection", "Extend the selection up one line"
    | ExtendDown -> "selection", "Extend the selection down one line"
    | ExtendHome -> "selection", "Extend the selection to the start of the line"
    | ExtendEnd -> "selection", "Extend the selection to the end of the line"
    | SelectAll -> "selection", "Select the whole buffer"
    | Indent -> "edit", "Indent the current line or selection"
    | Unindent -> "edit", "Unindent the current line or selection"
    | DeleteWordBack -> "edit", "Delete the word before the cursor"
    | DeleteWordForward -> "edit", "Delete the word after the cursor"
    | Undo -> "edit", "Undo the last change"
    | Redo -> "edit", "Redo the last undone change"
    | Copy -> "clipboard", "Copy the selection to the clipboard"
    | Cut -> "clipboard", "Cut the selection to the clipboard"
    | Paste -> "clipboard", "Paste from the clipboard"
    | MoveLinesUp _ -> "edit", "Move the current line or selection up"
    | MoveLinesDown _ -> "edit", "Move the current line or selection down"
    | Save -> "file", "Save the active buffer"
    | SaveAs _ -> "file", "Save the active buffer to a new path"
    | Quit -> "file", "Quit the editor"
    | ForceQuit -> "file", "Quit the editor, discarding unsaved changes"
    | CloseBuffer -> "buffer", "Close the active buffer"
    | OpenPalette -> "prompt", "Open the command palette"
    | OpenFilePicker -> "prompt", "Open the file picker"
    | OpenSearch -> "prompt", "Open in-buffer search"
    | NextBuffer -> "buffer", "Switch to the next buffer"
    | PrevBuffer -> "buffer", "Switch to the previous buffer"
    | JumpToBuffer _ -> "buffer", "Jump to a buffer by number"
    | SetTheme _ -> "view", "Switch the active theme"
    | Goto _ -> "motion", "Jump to a line and column"
    | ReloadWorkspace -> "workspace", "Reload the workspace from disk"
    | OpenConfig -> "config", "Open the config directory"
    | ReloadKeybinds -> "config", "Reload the user keybinds file"
    | RunPlugin _ -> "plugin", "Run a plugin command"
    | RevealSidebar -> "panel", "Reveal the sidebar"
    | HideSidebar -> "panel", "Hide the sidebar"
    | ToggleSidebar -> "panel", "Toggle the sidebar"
    | FocusSidebar -> "panel", "Move focus to the sidebar"
    | FocusEditor -> "panel", "Move focus to the editor"
    | RevealInSidebar -> "tree", "Reveal the active file in the sidebar"
    | SidebarUp -> "tree", "Move the tree selection up"
    | SidebarDown -> "tree", "Move the tree selection down"
    | SidebarPageUp -> "tree", "Move the tree selection up one page"
    | SidebarPageDown -> "tree", "Move the tree selection down one page"
    | SidebarTop -> "tree", "Move the tree selection to the top"
    | SidebarBottom -> "tree", "Move the tree selection to the bottom"
    | SidebarCollapse -> "tree", "Collapse the selected tree node"
    | SidebarExpand -> "tree", "Expand the selected tree node"
    | SidebarActivate -> "tree", "Open the selected tree node"
    | Chain _ -> "other", "Run several actions in sequence"
    | When _ -> "other", "Run an action conditionally"
    | NoOp -> "other", "Do nothing"
    | RecordMacro _ -> "edit", "Record a macro into a register"
    | ReplayMacro _ -> "edit", "Replay a macro from a register"
    | RepeatLastMacro -> "edit", "Replay the last macro"

/// One representative value per `Action` case — the enumeration the website
/// uses to list unbound actions. Payload-carrying cases get sentinel args;
/// `actionName`/`actionMeta` ignore payloads. This is the ONE list not
/// checked for completeness by the compiler — keep it in sync with `Action`
/// (the `allActions covers every Action case` test backstops omissions).
let allActions: Action list =
    [ MoveLeft
      MoveRight
      MoveUp
      MoveDown
      MoveWordLeft
      MoveWordRight
      MoveHome
      MoveEnd
      MovePageUp
      MovePageDown
      ExtendLeft
      ExtendRight
      ExtendUp
      ExtendDown
      ExtendHome
      ExtendEnd
      SelectAll
      Indent
      Unindent
      DeleteWordBack
      DeleteWordForward
      Undo
      Redo
      Copy
      Cut
      Paste
      MoveLinesUp 1
      MoveLinesDown 1
      Save
      SaveAs ""
      Quit
      ForceQuit
      CloseBuffer
      OpenPalette
      OpenFilePicker
      OpenSearch
      NextBuffer
      PrevBuffer
      JumpToBuffer 0
      SetTheme ""
      Goto(0, None)
      ReloadWorkspace
      OpenConfig
      ReloadKeybinds
      RunPlugin("", "", "")
      RevealSidebar
      HideSidebar
      ToggleSidebar
      FocusSidebar
      FocusEditor
      RevealInSidebar
      SidebarUp
      SidebarDown
      SidebarPageUp
      SidebarPageDown
      SidebarTop
      SidebarBottom
      SidebarCollapse
      SidebarExpand
      SidebarActivate
      Chain []
      When(HasSelection, NoOp, NoOp)
      NoOp
      RecordMacro ' '
      ReplayMacro(' ', 1)
      RepeatLastMacro ]

let private contextName (ctx: Context) : string =
    match ctx with
    | Context.Global -> "global"
    | Context.Editor -> "editor"
    | Context.Sidebar -> "sidebar"
    | Context.Prompt -> "prompt"

let private jsonEscape (s: string) : string =
    let sb = StringBuilder()

    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | other -> sb.Append other |> ignore

    sb.ToString()

/// Serialize bindings, then every unbound `Action`, to a JSON array.
/// `Action = None` (unbinds) are skipped — they carry no name. Each row
/// carries a `bound` flag; unbound actions get an empty stroke/context so
/// the website can render and filter them. Ends with a newline.
let toJson (keymap: Keymap) : string =
    let field name value =
        "\"" + name + "\": \"" + jsonEscape value + "\""

    let renderRow stroke action context category description bound =
        let pairs =
            [ field "stroke" stroke
              field "action" action
              field "context" context
              field "category" category
              field "description" description
              "\"bound\": " + (if bound then "true" else "false") ]

        "  { " + String.concat ", " pairs + " }"

    let boundRows =
        keymap
        |> List.choose (fun b ->
            b.Action
            |> Option.map (fun a ->
                let category, description = actionMeta a

                renderRow
                    (Chord.renderStroke b.Stroke)
                    (actionName a)
                    (contextName b.Context)
                    category
                    description
                    true))

    let boundNames =
        keymap |> List.choose (fun b -> b.Action) |> List.map actionName |> Set.ofList

    let unboundRows =
        allActions
        |> List.filter (fun a -> not (boundNames.Contains(actionName a)))
        |> List.map (fun a ->
            let category, description = actionMeta a
            renderRow "" (actionName a) "" category description false)

    "[\n" + String.concat ",\n" (boundRows @ unboundRows) + "\n]\n"

/// Human-readable aligned table: context, stroke, action per line.
let private renderTable (keymap: Keymap) : string =
    let rows =
        keymap
        |> List.choose (fun b ->
            b.Action
            |> Option.map (fun a -> contextName b.Context, Chord.renderStroke b.Stroke, a))

    let ctxWidth =
        rows
        |> List.map (fun (c, _, _) -> c.Length)
        |> (fun ls -> if List.isEmpty ls then 0 else List.max ls)

    let strokeWidth =
        rows
        |> List.map (fun (_, s, _) -> s.Length)
        |> (fun ls -> if List.isEmpty ls then 0 else List.max ls)

    let sb = StringBuilder()

    for ctx, stroke, action in rows do
        sb.AppendLine(
            ctx.PadRight ctxWidth
            + "  "
            + stroke.PadRight strokeWidth
            + "  "
            + actionName action
        )
        |> ignore

    sb.ToString()

type private KeybindsOpt =
    | KeybindsHelp
    | KeybindsJson

let private keybindsApp: CliApp<KeybindsOpt> =
    { Name = "fedit keybinds"
      Summary = "Print the default keybindings"
      Positionals = []
      Options =
        [ { Short = Some 'h'
            Long = "help"
            Value = NoValue
            Description = "Show this help and exit"
            Option = KeybindsHelp
            Completion = NoHint }
          { Short = None
            Long = "json"
            Value = NoValue
            Description = "Emit the keybindings as a JSON array"
            Option = KeybindsJson
            Completion = NoHint } ]
      Subcommands = [] }

/// Descriptor for the `keybinds` subcommand. Exported so the top-level
/// descriptor in `Program.fs` can nest it.
let descriptor: CliCommandDescriptor =
    { Name = "keybinds"
      Aliases = []
      HiddenAliases = []
      Summary = keybindsApp.Summary
      Positionals = keybindsApp.Positionals
      Options = keybindsApp.Options |> List.map Parser.toOptionDescriptor
      Subcommands = [] }

let private wantsHelp items =
    items
    |> List.exists (function
        | Option(KeybindsHelp, _) -> true
        | _ -> false)

let private wantsJson items =
    items
    |> List.exists (function
        | Option(KeybindsJson, _) -> true
        | _ -> false)

let run (argv: string[]) : int =
    match Parser.parse keybindsApp.Options argv with
    | Result.Error errors ->
        System.Console.Error.WriteLine(Parser.formatErrors keybindsApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        System.Console.Out.WriteLine(Parser.formatHelp keybindsApp)
        0
    | Result.Ok items ->
        if wantsJson items then
            System.Console.Out.Write(toJson Keymap.defaults)
        else
            System.Console.Out.Write(renderTable Keymap.defaults)

        0
