namespace Fedit

/// Which focus a binding applies in. Closed enum (spec §4.3) — there is no
/// deeper tree, so resolution specificity is just "context-match beats Global".
/// RequireQualifiedAccess so the cases don't collide with `FocusTarget`
/// (Editor/Sidebar/Prompt) at the dispatch sites.
[<RequireQualifiedAccess>]
type Context =
    | Global
    | Editor
    | Sidebar
    | Prompt

type Binding =
    { Stroke: KeyStroke // Chord list; length > 1 == a sequence
      Context: Context
      Action: Action option } // None == unbind

/// defaults @ user-delta. Order is load order: later wins (spec §6.2 rule 3).
type Keymap = Binding list

/// Outcome of resolving a keystroke (spec §6.2). `Unbound` is distinct from
/// `NotBound`: the former means "explicitly freed, do nothing, do not fall
/// through"; the latter means "no binding, apply focus fallthrough".
type Resolution =
    | Bound of Action
    | Unbound
    | NotBound

[<RequireQualifiedAccess>]
module Keymap =

    // ── DSL helpers (devs only; mirror the file 1:1, spec §6.6) ───────────
    let chord mods key : Chord = { Mods = Set.ofList mods; Key = key }

    let private single (c: Chord) (action: Action) : Binding =
        { Stroke = [ c ]
          Context = Context.Editor
          Action = Some action }

    let private inCtx ctx (b: Binding) = { b with Context = ctx }

    /// Compiled-in defaults. Reproduces today's dispatch exactly — every chord
    /// the global Ctrl handler / runEditor / runSidebar matched gets one entry.
    /// `Ctrl+Q` is deliberately absent: it dispatches ahead of the keymap in
    /// `Editor.update` so quitting survives a broken keybinds file; the
    /// two-stage dirty guard lives in `runAction Quit`. Guarded by the parity
    /// test.
    let defaults: Keymap =
        [
          // ── global Ctrl chords (fire in every focus → Context.Global) ──
          single (chord [ Ctrl ] (Key.Char 's')) Save |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'p')) OpenPalette |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'o')) OpenFilePicker |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'f')) OpenSearch |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'e')) FocusEditor |> inCtx Context.Global
          single (chord [ Ctrl; Shift ] (Key.Char 'e')) RevealInSidebar
          |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'r')) ReloadWorkspace |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'z')) Undo |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'y')) Redo |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'a')) SelectAll |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'c')) Copy |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'x')) Cut |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'v')) Paste |> inCtx Context.Global
          single (chord [ Ctrl ] (Named PageDown)) Action.NextBuffer
          |> inCtx Context.Global
          single (chord [ Ctrl ] (Named PageUp)) PrevBuffer |> inCtx Context.Global
          single (chord [ Ctrl ] (Key.Char 'w')) CloseBuffer |> inCtx Context.Global

          // ── tri-state sidebar Ctrl+B, split per spec §6.5/§11.1 ──
          //   editor/global/prompt view: reveal+focus when hidden, focus when visible
          single
              (chord [ Ctrl ] (Key.Char 'b'))
              (When(SidebarVisible, FocusSidebar, Chain [ RevealSidebar; FocusSidebar ]))
          |> inCtx Context.Global
          //   already in the sidebar: hide and return to the editor
          single (chord [ Ctrl ] (Key.Char 'b')) (Chain [ HideSidebar; FocusEditor ])
          |> inCtx Context.Sidebar

          // ── editor motions / edits (Context.Editor) ──
          single (chord [] (Named Left)) MoveLeft
          single (chord [] (Named Right)) MoveRight
          single (chord [] (Named Up)) MoveUp
          single (chord [] (Named Down)) MoveDown
          single (chord [] (Named Home)) MoveHome
          single (chord [] (Named End)) MoveEnd
          single (chord [ Shift ] (Named Left)) ExtendLeft
          single (chord [ Shift ] (Named Right)) ExtendRight
          single (chord [ Shift ] (Named Up)) ExtendUp
          single (chord [ Shift ] (Named Down)) ExtendDown
          single (chord [ Shift ] (Named Home)) ExtendHome
          single (chord [ Shift ] (Named End)) ExtendEnd
          single (chord [] (Named PageUp)) MovePageUp
          single (chord [] (Named PageDown)) MovePageDown
          single (chord [] (Named Tab)) Indent
          single (chord [ Shift ] (Named Tab)) Unindent
          single (chord [ Alt ] (Named Left)) MoveWordLeft
          single (chord [ Alt ] (Named Right)) MoveWordRight
          single (chord [ Ctrl ] (Named Left)) MoveWordLeft
          single (chord [ Ctrl ] (Named Right)) MoveWordRight
          single (chord [ Super ] (Named Left)) MoveHome // terminals that report Command+Left
          single (chord [ Super ] (Named Right)) MoveEnd // terminals that report Command+Right
          single (chord [ Super; Shift ] (Named Left)) ExtendHome
          single (chord [ Super; Shift ] (Named Right)) ExtendEnd
          single (chord [ Ctrl ] (Named Backspace)) DeleteWordBack
          single (chord [ Ctrl ] (Named Delete)) DeleteWordForward
          single (chord [ Alt ] (Named Up)) (MoveLinesUp 1)
          single (chord [ Alt ] (Named Down)) (MoveLinesDown 1)
          // Escape dismisses the selection; prefix-cancel runs ahead of the
          // keymap, and notification/QuitArmed clearing are keypress preamble.
          single (chord [] (Named Escape)) ClearSelection

          // ── repeat the last accepted search without reopening the prompt ──
          single (chord [] (Fn 3)) SearchNext
          single (chord [ Shift ] (Fn 3)) SearchPrevious

          // ── macros: modifier chords only (bare Char is reserved for text) ──
          single (chord [ Ctrl; Shift ] (Key.Char 'm')) (RecordMacro 'a')
          single (chord [ Ctrl; Shift ] (Key.Char 'r')) (ReplayMacro('a', 1))
          single (chord [ Ctrl; Shift ] (Key.Char '.')) RepeatLastMacro

          // ── sidebar navigation (Context.Sidebar) ──
          single (chord [] (Named Up)) SidebarUp |> inCtx Context.Sidebar
          single (chord [] (Named Down)) SidebarDown |> inCtx Context.Sidebar
          single (chord [] (Named PageUp)) SidebarPageUp |> inCtx Context.Sidebar
          single (chord [] (Named PageDown)) SidebarPageDown |> inCtx Context.Sidebar
          single (chord [] (Named Home)) SidebarTop |> inCtx Context.Sidebar
          single (chord [] (Named End)) SidebarBottom |> inCtx Context.Sidebar
          single (chord [] (Named Left)) SidebarCollapse |> inCtx Context.Sidebar
          single (chord [] (Named Right)) SidebarExpand |> inCtx Context.Sidebar
          single (chord [] (Named Enter)) SidebarActivate |> inCtx Context.Sidebar
          single (chord [] (Named Escape)) FocusEditor |> inCtx Context.Sidebar

          // ── buffer jumps: Ctrl+1..9 (Context.Global) ──
          yield!
              [ for n in 1..9 ->
                    single (chord [ Ctrl ] (Key.Char(char (int '0' + n)))) (JumpToBuffer n)
                    |> inCtx Context.Global ] ]

    // ── resolution (spec §6.2) ────────────────────────────────────────────

    /// Resolve a full keystroke in a context:
    ///   1. keep bindings whose Stroke equals the input
    ///   2. context-match beats Global (specificity)
    ///   3. within a tier, LAST match wins (load order; user delta is appended)
    ///   4. a matched Action = None (unbind) actively frees the stroke
    ///      (returns Unbound — the caller must NOT fall back to Global/text)
    ///   5. no match → NotBound — the caller applies focus fallthrough
    let resolve (ctx: Context) (stroke: KeyStroke) (keymap: Keymap) : Resolution =
        let matching = keymap |> List.filter (fun b -> b.Stroke = stroke)
        let inContext = matching |> List.filter (fun b -> b.Context = ctx)
        let globals = matching |> List.filter (fun b -> b.Context = Context.Global)
        // Specificity: prefer the active-context tier (even when its last entry
        // is an unbind — that suppresses the Global fallback); else fall back.
        let tier = if List.isEmpty inContext then globals else inContext

        match List.tryLast tier with
        | Some b ->
            match b.Action with
            | Some a -> Bound a
            | None -> Unbound
        | None -> NotBound

    let private isProperPrefix (short: KeyStroke) (long: KeyStroke) =
        short.Length < long.Length && List.take short.Length long = short

    /// True when `stroke` is a proper prefix of some bound sequence reachable
    /// in this context (the active context or Global). Drives the pending-prefix
    /// engine: an in-flight stroke that extends a bound sequence is held.
    let isSequencePrefix (ctx: Context) (stroke: KeyStroke) (keymap: Keymap) : bool =
        keymap
        |> List.exists (fun b ->
            (b.Context = ctx || b.Context = Context.Global)
            && b.Action.IsSome
            && isProperPrefix stroke b.Stroke)

    /// A standalone (length-1) binding that is also a proper prefix of a bound
    /// sequence in the same context cannot coexist (it would silently shadow
    /// the sequence). Returns the offending standalone bindings with a reason;
    /// the caller drops them and keeps the longer sequence.
    let prefixConflicts (keymap: Keymap) : (Binding * string) list =
        [ for b in keymap do
              if b.Stroke.Length = 1 && b.Action.IsSome then
                  let shadowed =
                      keymap
                      |> List.exists (fun o -> o.Context = b.Context && isProperPrefix b.Stroke o.Stroke)

                  if shadowed then
                      yield b, $"'{Chord.renderStroke b.Stroke}' is a prefix of a bound sequence in {b.Context}" ]

    /// keystroke ↔ action index, built at load. Keyed on Action (structural
    /// equality — no Action case carries a closure). Used by `:keybind` and the
    /// prompt to show bound keys.
    let index (keymap: Keymap) : Map<Action, KeyStroke list> =
        keymap
        |> List.choose (fun b -> b.Action |> Option.map (fun a -> a, b.Stroke))
        |> List.fold
            (fun acc (a, s) ->
                let existing = acc |> Map.tryFind a |> Option.defaultValue []
                Map.add a (existing @ [ s ]) acc)
            Map.empty

    /// Render the effective keymap as a readable, grouped document — the body
    /// of the `:keybind` buffer. Deduped to the effective binding per
    /// (context, stroke) so later-wins overrides and unbinds are reflected,
    /// grouped by context, columns aligned, sorted by stroke. Line endings are
    /// always "\n" so the buffer splits cleanly on every platform.
    let renderDoc (keymap: Keymap) : string =
        let ctxName =
            function
            | Context.Global -> "global"
            | Context.Editor -> "editor"
            | Context.Sidebar -> "sidebar"
            | Context.Prompt -> "prompt"

        // defaults @ user-delta: later bindings win, and a final action of None
        // means the stroke was explicitly unbound.
        let effective =
            keymap
            |> List.fold (fun acc b -> Map.add (b.Context, b.Stroke) b.Action acc) Map.empty

        let rowsFor ctx =
            effective
            |> Map.toList
            |> List.choose (fun ((c, stroke), action) ->
                if c = ctx then
                    action |> Option.map (fun a -> Chord.renderStroke stroke, Action.name a)
                else
                    None)
            |> List.sortBy fst

        let sb = System.Text.StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        line "# fedit keybindings"
        line ""
        line "Effective bindings: built-in defaults overlaid by ~/.config/fedit/keybinds."
        line "Edit that file, then run `:keybind reload`."
        line ""

        for ctx in [ Context.Global; Context.Editor; Context.Sidebar; Context.Prompt ] do
            match rowsFor ctx with
            | [] -> ()
            | rows ->
                line ("## " + ctxName ctx)
                let width = rows |> List.map (fst >> String.length) |> List.max

                for (stroke, action) in rows do
                    line ("  " + stroke.PadRight width + "  " + action)

                line ""

        sb.ToString().TrimEnd('\n') + "\n"

    // ── line-format parser (spec §6.6 + the run-plugin grammar) ───────────

    let private (|ContextWord|_|) (s: string) =
        match s.ToLowerInvariant() with
        | "global" -> Some Context.Global
        | "editor" -> Some Context.Editor
        | "sidebar" -> Some Context.Sidebar
        | "prompt" -> Some Context.Prompt
        | _ -> None

    let private parseInt (s: string) =
        match System.Int32.TryParse(s.Trim()) with
        | true, n -> Some n
        | _ -> None

    /// Map a kebab-case action name (+ the raw remainder after the first ':')
    /// to an Action. `run-plugin` is special-cased: its arg is itself
    /// structured `<source>/<name> [plugin-arg]` (split once on '/', then once
    /// on whitespace; an embedded '/' in the plugin-arg is preserved).
    let private parseAction (name: string) (arg: string) : Result<Action, string> =
        match name with
        | "save" -> Ok Save
        | "quit" -> Ok Quit
        | "force-quit" -> Ok ForceQuit
        | "close-buffer" -> Ok CloseBuffer
        | "command-palette"
        | "open-palette" -> Ok OpenPalette
        | "open-file" -> Ok OpenFilePicker
        | "search" -> Ok OpenSearch
        | "search-next" -> Ok SearchNext
        | "search-previous" -> Ok SearchPrevious
        | "undo" -> Ok Undo
        | "redo" -> Ok Redo
        | "copy" -> Ok Copy
        | "cut" -> Ok Cut
        | "paste" -> Ok Paste
        | "select-all" -> Ok SelectAll
        | "clear-selection" -> Ok ClearSelection
        | "move-left" -> Ok MoveLeft
        | "move-right" -> Ok MoveRight
        | "move-up" -> Ok MoveUp
        | "move-down" -> Ok MoveDown
        | "move-word-left" -> Ok MoveWordLeft
        | "move-word-right" -> Ok MoveWordRight
        | "move-home" -> Ok MoveHome
        | "move-end" -> Ok MoveEnd
        | "page-up" -> Ok MovePageUp
        | "page-down" -> Ok MovePageDown
        | "extend-left" -> Ok ExtendLeft
        | "extend-right" -> Ok ExtendRight
        | "extend-up" -> Ok ExtendUp
        | "extend-down" -> Ok ExtendDown
        | "extend-home" -> Ok ExtendHome
        | "extend-end" -> Ok ExtendEnd
        | "indent" -> Ok Indent
        | "unindent" -> Ok Unindent
        | "delete-word-back" -> Ok DeleteWordBack
        | "delete-word-forward" -> Ok DeleteWordForward
        | "move-lines-up"
        | "move-lines-down" as actionName ->
            let count = if arg.Trim() = "" then Some 1 else parseInt arg

            match count with
            | Some n when n > 0 ->
                if actionName = "move-lines-up" then
                    Ok(MoveLinesUp n)
                else
                    Ok(MoveLinesDown n)
            | _ -> Result.Error $"{actionName} count must be at least 1"
        | "next-buffer" -> Ok Action.NextBuffer
        | "prev-buffer" -> Ok PrevBuffer
        | "jump-to-buffer" ->
            match parseInt arg with
            | Some n when n >= 1 && n <= 9 -> Ok(JumpToBuffer n)
            | _ -> Result.Error "jump-to-buffer needs a buffer number 1..9"
        | "set-theme" when arg.Trim() <> "" -> Ok(SetTheme(arg.Trim()))
        | "set-theme" -> Result.Error "set-theme needs a theme name"
        | "goto" ->
            match arg.Split(':') with
            | [| l |] ->
                match parseInt l with
                | Some n -> Ok(Goto(n, None))
                | None -> Result.Error "goto needs a line number"
            | [| l; c |] ->
                match parseInt l, parseInt c with
                | Some line, Some col -> Ok(Goto(line, Some col))
                | _ -> Result.Error "goto needs LINE or LINE:COL numbers"
            | _ -> Result.Error "goto: bad argument"
        | "reload-workspace" -> Ok ReloadWorkspace
        | "reload-keybinds" -> Ok ReloadKeybinds
        | "open-config" -> Ok OpenConfig
        | "toggle-sidebar" -> Ok ToggleSidebar
        | "reveal-sidebar" -> Ok RevealSidebar
        | "hide-sidebar" -> Ok HideSidebar
        | "focus-editor" -> Ok FocusEditor
        | "focus-sidebar" -> Ok FocusSidebar
        | "reveal-in-sidebar" -> Ok RevealInSidebar
        | "sidebar-up" -> Ok SidebarUp
        | "sidebar-down" -> Ok SidebarDown
        | "sidebar-page-up" -> Ok SidebarPageUp
        | "sidebar-page-down" -> Ok SidebarPageDown
        | "sidebar-top" -> Ok SidebarTop
        | "sidebar-bottom" -> Ok SidebarBottom
        | "sidebar-collapse" -> Ok SidebarCollapse
        | "sidebar-expand" -> Ok SidebarExpand
        | "sidebar-activate" -> Ok SidebarActivate
        | "run-plugin" ->
            let refAndArg = arg.TrimStart()

            match refAndArg.IndexOf('/') with
            | -1 -> Result.Error "run-plugin needs <source>/<name>"
            | slash ->
                let source = refAndArg.Substring(0, slash)
                let rest = refAndArg.Substring(slash + 1)

                let name, pluginArg =
                    match rest.IndexOfAny([| ' '; '\t' |]) with
                    | -1 -> rest, ""
                    | ws -> rest.Substring(0, ws), rest.Substring(ws + 1).Trim()

                if source = "" || name = "" then
                    Result.Error "run-plugin needs <source>/<name>"
                else
                    Ok(RunPlugin(source, name, pluginArg))
        | "record-macro" when arg.Trim().Length = 1 -> Ok(RecordMacro(arg.Trim().[0]))
        | "replay-macro" ->
            match arg.Split(':') with
            | [| r |] when r.Trim().Length = 1 -> Ok(ReplayMacro(r.Trim().[0], 1))
            | [| r; count |] when r.Trim().Length = 1 ->
                match parseInt count with
                | Some n -> Ok(ReplayMacro(r.Trim().[0], n))
                | None -> Result.Error "replay-macro count must be a number"
            | _ -> Result.Error "replay-macro needs a single-character register"
        | "repeat-last-macro" -> Ok RepeatLastMacro
        | "no-op" -> Ok NoOp
        | other -> Result.Error $"unknown action '{other}'"

    /// Parse one line. `Ok None` = blank/comment. `Ok (Some b)` = a binding
    /// (`b.Action = None` means unbind). `Error` = malformed (skipped + reported).
    let parseLine (line: string) : Result<Binding option, string> =
        let trimmed = line.Trim()

        if trimmed = "" || trimmed.StartsWith "#" then
            Ok None
        else
            match trimmed.IndexOf('=') with
            | -1 -> Result.Error "missing '='"
            | eq ->
                let lhs = trimmed.Substring(0, eq).Trim()
                let rhs = trimmed.Substring(eq + 1).Trim()

                let tokens =
                    lhs.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList

                let ctx, chordTokens =
                    match tokens with
                    | (ContextWord c) :: rest when not (List.isEmpty rest) -> c, rest
                    | _ -> Context.Editor, tokens // default context (spec §6.6)

                if List.isEmpty chordTokens then
                    Result.Error "no stroke"
                else
                    let chords = chordTokens |> List.map Chord.parse

                    if chords |> List.exists Option.isNone then
                        Result.Error "unparseable chord in stroke"
                    else
                        let stroke = chords |> List.map Option.get

                        if rhs = "" then
                            Ok(
                                Some
                                    { Stroke = stroke
                                      Context = ctx
                                      Action = None }
                            ) // unbind
                        else
                            let name, arg =
                                match rhs.IndexOf(':') with
                                | -1 -> rhs, ""
                                | colon -> rhs.Substring(0, colon), rhs.Substring(colon + 1)

                            parseAction (name.Trim()) arg
                            |> Result.map (fun a ->
                                Some
                                    { Stroke = stroke
                                      Context = ctx
                                      Action = Some a })

    /// Parse a whole file's worth of lines into `defaults @ valid` plus a list
    /// of `keybinds:<n>: <reason>` errors. Prefix-conflicting standalones are
    /// dropped (keeping the sequence) and reported. Pure — the file read lives
    /// in `KeymapIO.load`.
    let parse (lines: string seq) : Keymap * string list =
        let mutable bindings = []
        let mutable errors = []

        lines
        |> Seq.iteri (fun i line ->
            match parseLine line with
            | Ok None -> ()
            | Ok(Some b) -> bindings <- b :: bindings
            | Result.Error reason -> errors <- $"keybinds:{i + 1}: {reason}" :: errors)

        let merged = defaults @ List.rev bindings
        let conflicts = prefixConflicts merged
        let conflictErrs = conflicts |> List.map snd

        let cleaned =
            merged
            |> List.filter (fun b -> not (conflicts |> List.exists (fun (c, _) -> System.Object.ReferenceEquals(c, b))))

        cleaned, List.rev errors @ conflictErrs
