namespace Fedit

open System
open System.IO


/// How the user referenced a buffer when invoking `:buffers` or `@`.
/// Parsed at the prompt layer so `executeCommand` doesn't reinterpret a
/// string against three different lookups.
type BufferRef =
    | ById of int
    | ByName of string

type Command =
    | Open of string
    | Write
    | WriteAs of string
    /// Quit with the dirty-buffer guard (warn + arm, quit on repeat).
    | Quit
    /// `quit force` — quit unconditionally, discarding unsaved changes.
    | ForceQuit
    /// `close [id-or-name]` — close a buffer; `None` closes the active one.
    | Close of BufferRef option
    | ToggleSidebar
    | FocusTree
    | FocusEditor
    /// Reveal the active buffer's file in the sidebar (expand ancestors,
    /// select it, focus the tree).
    | Reveal
    | ReloadWorkspace
    | NextBuffer
    | PreviousBuffer
    | Theme of string
    | Recent of string
    | SwitchBuffer of BufferRef
    | Goto of line: int * column: int option
    /// Built-in plugin manager command. `plugin list|enable|disable|install|remove|reload|validate`.
    | Plugin of verb: string * argument: string
    /// Open the plugin manager dock.
    | Plugins
    /// Open the macro manager dock.
    | Macros
    /// Open the user's config file (`~/.config/fedit/config.json`) in a
    /// buffer. Creates the file with the running config if absent.
    | OpenConfig
    /// Invocation of a plugin-registered command. `source` is the owning
    /// plugin name (for diagnostics); `commandName` matches the
    /// `PluginCommand.Name` the plugin registered. `argument` is any text
    /// after the command name on the prompt.
    | PluginInvoke of source: string * commandName: string * argument: string
    /// `:syntax on|off|toggle` — flips Config.SyntaxHighlightingEnabled
    /// and persists. Other verbs surface as Invalid at the prompt.
    | Syntax of verb: string
    /// `:keybind [reload | <stroke>]` — list effective keybindings, reload the
    /// user file, or show what a stroke resolves to in each context.
    | Keybind of argument: string

type ParsedCommand =
    | Empty
    | Ready of Command
    | Pending of string
    | Invalid of string

type CommandContext =
    {
        RootPath: string
        Files: string list
        Recent: string list
        /// `(id, name, filePath, dirty)` per open buffer.
        Buffers: (int * string * string option * bool) list
        Themes: Theme list
        CompletionLimit: int
    }

[<RequireQualifiedAccess>]
module Commands =
    type Spec =
        {
            Name: string
            Usage: string
            Summary: string
            /// Skip this spec when building completion menus and help
            /// listings. Still resolves through `parseWith` so users can
            /// type it (and future macro/scripting layers can reach it).
            /// Use for verbs that have a stronger keyboard shortcut and
            /// don't read naturally as typed commands (focus changes,
            /// panel toggles, etc.).
            Hidden: bool
            Constructor: string -> ParsedCommand
        }

    let private simple command argument =
        if String.IsNullOrWhiteSpace argument then
            Ready command
        else
            Invalid "This command does not take arguments."

    // todo: could probably be named better
    let specs =
        [ { Name = "open"
            Usage = "open <path>"
            Summary = "Open a file from the workspace."
            Hidden = false
            Constructor =
              fun argument ->
                  if String.IsNullOrWhiteSpace argument then
                      Pending "Path is required."
                  else
                      Ready(Open(argument.Trim())) }
          { Name = "write"
            Usage = "write"
            Summary = "Save the active buffer."
            Hidden = false
            Constructor = simple Write }
          { Name = "writeas"
            Usage = "writeas <path>"
            Summary = "Save the active buffer to a new path."
            Hidden = false
            Constructor =
              fun argument ->
                  if String.IsNullOrWhiteSpace argument then
                      Pending "Target path is required."
                  else
                      Ready(WriteAs(argument.Trim())) }
          { Name = "quit"
            Usage = "quit [force]"
            Summary = "Exit fedit. `quit force` discards unsaved changes."
            Hidden = false
            Constructor =
              fun argument ->
                  match argument.Trim().ToLowerInvariant() with
                  | "" -> Ready Quit
                  | "force" -> Ready ForceQuit
                  | other -> Invalid $"Unknown quit argument '{other}'." }
          { Name = "config"
            Usage = "config"
            Summary = "Open the fedit config file (~/.config/fedit/config.json) in a buffer."
            Hidden = false
            Constructor = simple OpenConfig }
          // Focus / panel toggles are keyboard-first (Ctrl+B, Ctrl+E):
          // hidden from completion so the palette isn't cluttered with
          // verbs nobody types. Still parseable for muscle-memory and
          // any future scripting layer.
          { Name = "sidebar"
            Usage = "sidebar"
            Summary = "Toggle the sidebar."
            Hidden = true
            Constructor = simple ToggleSidebar }
          { Name = "tree"
            Usage = "tree"
            Summary = "Focus the file tree."
            Hidden = true
            Constructor = simple FocusTree }
          { Name = "editor"
            Usage = "editor"
            Summary = "Focus the editor."
            Hidden = true
            Constructor = simple FocusEditor }
          { Name = "reveal"
            Usage = "reveal"
            Summary = "Reveal the active file in the sidebar."
            Hidden = false
            Constructor = simple Reveal }
          { Name = "reload"
            Usage = "reload"
            Summary = "Reload the workspace tree."
            Hidden = false
            Constructor = simple ReloadWorkspace }
          { Name = "next"
            Usage = "next"
            Summary = "Activate the next open buffer."
            Hidden = false
            Constructor = simple NextBuffer }
          { Name = "prev"
            Usage = "prev"
            Summary = "Activate the previous open buffer."
            Hidden = false
            Constructor = simple PreviousBuffer }
          { Name = "theme"
            Usage = "theme <name>"
            Summary = "Switch accent color. Bundled palettes plus any user themes from ~/.config/fedit/themes/*.json."
            Hidden = false
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Theme name required."
                  else
                      Ready(Theme trimmed) }
          { Name = "recent"
            Usage = "recent <path>"
            Summary = "Open a recently used file."
            Hidden = false
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Recent file required."
                  else
                      Ready(Recent trimmed) }
          { Name = "buffers"
            Usage = "buffers <id-or-name>"
            Summary = "Switch to an open buffer."
            Hidden = false
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Buffer id or name required."
                  else
                      let bufferRef =
                          match Int32.TryParse trimmed with
                          | true, id -> ById id
                          | _ -> ByName trimmed

                      Ready(SwitchBuffer bufferRef) }
          { Name = "close"
            Usage = "close [id-or-name]"
            Summary = "Close a buffer (the active one by default)."
            Hidden = false
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Ready(Close None)
                  else
                      let bufferRef =
                          match Int32.TryParse trimmed with
                          | true, id -> ById id
                          | _ -> ByName trimmed

                      Ready(Close(Some bufferRef)) }
          { Name = "syntax"
            Usage = "syntax <on|off|toggle>"
            Summary = "Toggle syntax highlighting."
            Hidden = false
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim().ToLowerInvariant()

                  match trimmed with
                  | "" -> Pending "Specify on, off, or toggle."
                  | "on"
                  | "off"
                  | "toggle" -> Ready(Syntax trimmed)
                  | other -> Invalid $"Unknown syntax verb '{other}'." }
          { Name = "plugin"
            Usage = "plugin <list|enable|disable|install|remove|reload|validate> [arg]"
            Summary = "Manage installed plugins."
            Hidden = false
            Constructor =
              fun argument ->
                  let trimmed = argument.Trim()

                  if String.IsNullOrWhiteSpace trimmed then
                      Pending "Plugin verb required (list, enable, disable, install, remove, reload, validate)."
                  else
                      let firstSpace = trimmed.IndexOf ' '

                      let verb, rest =
                          if firstSpace < 0 then
                              trimmed, ""
                          else
                              trimmed[.. firstSpace - 1], trimmed[firstSpace + 1 ..].Trim()

                      let known =
                          Set.ofList [ "list"; "enable"; "disable"; "install"; "remove"; "reload"; "validate" ]

                      let verbLower = verb.ToLowerInvariant()

                      if not (known.Contains verbLower) then
                          Invalid $"Unknown plugin verb '{verb}'."
                      else
                          let needsArg = Set.ofList [ "enable"; "disable"; "install"; "remove"; "validate" ]

                          if needsArg.Contains verbLower && String.IsNullOrWhiteSpace rest then
                              Pending $"plugin {verbLower} requires an argument."
                          else
                              Ready(Plugin(verbLower, rest)) }
          { Name = "plugins"
            Usage = "plugins"
            Summary = "Open the plugin manager."
            Hidden = false
            Constructor = simple Plugins }
          { Name = "macros"
            Usage = "macros"
            Summary = "Open the macro manager."
            Hidden = false
            Constructor = simple Macros }
          { Name = "keybind"
            Usage = "keybind [reload | <stroke>]"
            Summary = "List effective keybindings, reload the file, or show what a stroke is bound to."
            Hidden = false
            Constructor = fun argument -> Ready(Keybind(argument.Trim())) } ]

    /// Specs synthesized from currently-loaded plugin commands. Each tuple
    /// is `(commandName, summary, sourcePluginName)`. Plugin specs sit
    /// alongside built-ins for completion / parse, but the built-in name
    /// wins on collision (filtered out here).
    let pluginSpecs (pluginCommands: (string * string * string) list) : Spec list =
        let builtinNames = specs |> List.map (fun s -> s.Name) |> Set.ofList

        pluginCommands
        |> List.filter (fun (name, _, _) -> not (builtinNames.Contains name))
        |> List.map (fun (name, summary, source) ->
            { Name = name
              Usage = name
              Summary = $"[{source}] {summary}"
              Hidden = false
              Constructor = fun argument -> Ready(PluginInvoke(source, name, argument.Trim())) })

    /// All specs — built-ins first, then plugin commands.
    let allSpecs (pluginCommands: (string * string * string) list) : Spec list = specs @ pluginSpecs pluginCommands

    /// Parse a `:LINE` or `:LINE:COL` argument (the text after the `:` prefix).
    /// Used by the prompt's Goto mode.
    let parseGoto (argument: string) =
        let trimmed = argument.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            Pending "Line number required."
        else
            let parts = trimmed.Split ':'

            let tryPositive (s: string) =
                match Int32.TryParse s with
                | true, n when n > 0 -> Some n
                | _ -> None

            match parts with
            | [| lineText |] ->
                match tryPositive lineText with
                | Some line -> Ready(Goto(line, None))
                | None -> Invalid $"'{trimmed}' is not a valid line number."
            | [| lineText; colText |] ->
                match tryPositive lineText, tryPositive colText with
                | Some line, Some col -> Ready(Goto(line, Some col))
                | _ -> Invalid $"'{trimmed}' must be <line>:<column> with positive numbers."
            | _ -> Invalid $"'{trimmed}' has too many ':' separators."

    /// Parse using an explicit spec list. Use this when plugin commands are
    /// in play so they participate in name lookup and "incomplete" hints.
    let parseWith (availableSpecs: Spec list) (input: string) =
        let trimmed = input.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            Empty
        else
            let firstSpace = trimmed.IndexOf ' '

            let name, argument =
                if firstSpace < 0 then
                    trimmed, ""
                else
                    trimmed[.. firstSpace - 1], trimmed[firstSpace + 1 ..]

            match availableSpecs |> List.tryFind (fun spec -> spec.Name = name.ToLowerInvariant()) with
            | Some spec -> spec.Constructor argument
            | None when
                availableSpecs
                |> List.exists (fun spec -> spec.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                ->
                Pending "Command is incomplete."
            | None -> Invalid $"Unknown command '{name}'."

    /// Built-ins-only parser. Plugin-aware callers should use `parseWith`.
    let parse (input: string) = parseWith specs input

    /// Case-insensitive substring match on the file name or the full path —
    /// the Ctrl+O file picker's matcher, shared with the `open`/`writeas`/
    /// `recent` argument completions so every file-completion surface
    /// matches the same way. An empty query matches everything.
    let matchesFileQuery (query: string) (path: string) : bool =
        if String.IsNullOrEmpty query then
            true
        else
            let fileName = Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

            fileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0

    /// Completion using an explicit spec list. The plugin-aware variant.
    let completionsWith (availableSpecs: Spec list) context (input: string) =
        let trimmed = input.TrimStart()

        // Hidden specs stay parseable but never surface in the menu.
        let visibleSpecs = availableSpecs |> List.filter (fun s -> not s.Hidden)

        if String.IsNullOrWhiteSpace trimmed then
            visibleSpecs
            |> List.map (fun spec ->
                { Label = spec.Name
                  ApplyText = spec.Name
                  Detail = spec.Summary
                  Kind = Command })
        else
            let firstSpace = trimmed.IndexOf ' '

            if firstSpace < 0 then
                visibleSpecs
                |> List.filter (fun spec -> spec.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                |> List.map (fun spec ->
                    { Label = spec.Name
                      ApplyText = spec.Name
                      Detail = spec.Summary
                      Kind = Command })
            else
                let commandName = trimmed[.. firstSpace - 1].ToLowerInvariant()
                let argument = trimmed[firstSpace + 1 ..].TrimStart()

                // Shared by `buffers` and `close`: one item per matching open
                // buffer, dirty ones marked with the status bar's ` [+]`.
                let bufferItems (verb: string) =
                    context.Buffers
                    |> List.filter (fun (id, name, _, _) ->
                        name.StartsWith(argument, StringComparison.OrdinalIgnoreCase)
                        || (string id).StartsWith argument)
                    |> List.truncate context.CompletionLimit
                    |> List.map (fun (id, name, filePath, dirty) ->
                        let detail = filePath |> Option.defaultValue "scratch"
                        let marker = if dirty then " [+]" else ""

                        { Label = $"{id}  {name}{marker}"
                          ApplyText = $"{verb} {id}"
                          Detail = detail
                          Kind = PathItem })

                match commandName with
                | "open"
                | "writeas" ->
                    context.Files
                    |> List.filter (matchesFileQuery argument)
                    |> List.truncate context.CompletionLimit
                    |> List.map (fun filePath ->
                        { Label = filePath
                          ApplyText = $"{commandName} {filePath}"
                          Detail = "workspace file"
                          Kind = PathItem })
                | "theme" ->
                    context.Themes
                    |> List.filter (fun theme -> theme.Name.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun theme ->
                        { Label = theme.Name
                          ApplyText = $"theme {theme.Name}"
                          Detail = theme.Description
                          Kind = PathItem })
                | "recent" ->
                    context.Recent
                    |> List.filter (matchesFileQuery argument)
                    |> List.truncate context.CompletionLimit
                    |> List.map (fun filePath ->
                        let label =
                            Path.GetFileName filePath |> Option.ofObj |> Option.defaultValue filePath

                        { Label = label
                          ApplyText = $"recent {filePath}"
                          Detail = filePath
                          Kind = PathItem })
                | "buffers" -> bufferItems "buffers"
                | "close" -> bufferItems "close"
                | "quit" ->
                    [ "force" ]
                    |> List.filter (fun verb -> verb.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun verb ->
                        { Label = verb
                          ApplyText = $"quit {verb}"
                          Detail = "discard unsaved changes and exit"
                          Kind = Command })
                | "plugin" ->
                    [ "list"; "enable"; "disable"; "install"; "remove"; "reload"; "validate" ]
                    |> List.filter (fun verb -> verb.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun verb ->
                        { Label = verb
                          ApplyText = $"plugin {verb}"
                          Detail = "plugin manager verb"
                          Kind = Command })
                | "syntax" ->
                    [ "on"; "off"; "toggle" ]
                    |> List.filter (fun verb -> verb.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun verb ->
                        { Label = verb
                          ApplyText = $"syntax {verb}"
                          Detail = "syntax highlighting toggle"
                          Kind = Command })
                | _ -> []

    /// Built-ins-only completion. Plugin-aware callers should use `completionsWith`.
    let completions context (input: string) = completionsWith specs context input

    let helpLines () =
        specs
        |> List.filter (fun spec -> not spec.Hidden)
        |> List.map (fun spec -> $"{spec.Usage}  {spec.Summary}")
