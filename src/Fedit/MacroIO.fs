namespace Fedit

open System.IO

/// Line grammar for the plain-text macros file. Pure — the disk half lives
/// in `MacroIO` below, mirroring the `Keymap.parse` / `KeymapIO` split.
///
/// One macro per line: `REGISTER = step step step …`. A step is a single
/// token under the shared quoted-payload grammar (`Keymap.tokenize`):
///   - an action in `Keymap.parseAction` syntax — the same right-hand side
///     keybind lines use, e.g. `insert-text:"TODO: "`, `goto:5`, `undo`
///   - `command:"…"` — a palette command line (`RunCommand` step), decoded
///     with the shared free-text payload grammar
///   - a wholly double-quoted token, decoded then parsed as an action —
///     the escape hatch for the few action syntaxes that carry whitespace
///     outside a quoted payload (`run-plugin:src/name arg`, `set-theme:a b`)
/// `#` comments and blank lines are ignored. Later lines for the same
/// register win. Malformed lines are skipped and reported per line.
[<RequireQualifiedAccess>]
module MacroFile =

    /// Commented grammar primer written at the top of every save, so a
    /// freshly-created (or rewritten) file explains itself. Custom
    /// comments are NOT preserved across the write-through rewrite —
    /// the file's canonical form is exactly this header plus one line
    /// per register.
    let header =
        String.concat
            "\n"
            [ "# fedit macros"
              "#"
              "# One macro per line: REGISTER = step step step ..."
              "# A step is an action token (the same syntax as the right-hand side of a"
              "# keybind line), or command:\"...\" for a palette command line. Quote any"
              "# payload that carries whitespace; \\\" \\\\ \\n \\t \\r escape inside quotes."
              "#"
              "#   a = insert-text:\"TODO: \" move-home"
              "#   b = search-for:\"let x\" delete-forward command:\"open README.md\""
              "#"
              "# '#' comments and blank lines are ignored. Recording a macro rewrites"
              "# this file in canonical form, so custom comments are not preserved."
              "" ]

    /// Parse one step token. `command:` steps decode their payload with the
    /// shared free-text grammar; a wholly-quoted token decodes to an action
    /// syntax first; everything else is an action token as-is.
    let parseStepToken (token: string) : Result<MacroStep, string> =
        if token.StartsWith "command:" then
            Keymap.parseTextPayload "command" (token.Substring "command:".Length)
            |> Result.map RunCommand
        elif token.StartsWith "\"" then
            Keymap.parseTextPayload "step" token
            |> Result.bind Keymap.parseAction
            |> Result.map RunAction
        else
            Keymap.parseAction token |> Result.map RunAction

    /// Render one step as a single token `parseStepToken` reads back.
    /// Actions serialize via `Action.toSyntax`; a syntax that tokenizes to
    /// more than one token (`run-plugin:… arg`, `set-theme:a b`) is wrapped
    /// in the whole-step quoted form. `None` only for the actions with no
    /// parse syntax (`Chain`/`When`/`SaveAs`) — recorded macros never
    /// contain them, so a `None` here can only come from hand-built state.
    let renderStep (step: MacroStep) : string option =
        match step with
        | RunCommand commandLine -> Some("command:" + Action.quoteTextPayload commandLine)
        | RunAction action ->
            Action.toSyntax action
            |> Option.map (fun syntax ->
                match Keymap.tokenize syntax with
                | [ _ ] -> syntax
                | _ -> Action.quoteTextPayload syntax)

    /// Parse one line. `Ok None` = blank/comment. `Error` = malformed
    /// (skipped + reported); the first bad step token fails the whole line
    /// so a half-parsed macro can never load.
    let parseLine (line: string) : Result<(char * MacroStep list) option, string> =
        let trimmed = line.Trim()

        if trimmed = "" || trimmed.StartsWith "#" then
            Ok None
        else
            match trimmed.IndexOf '=' with
            | -1 -> Result.Error "missing '='"
            | eq ->
                let lhs = trimmed.Substring(0, eq).Trim()
                let rhs = trimmed.Substring(eq + 1)

                if lhs.Length <> 1 then
                    Result.Error "register must be a single character, e.g. a = insert-text:\"x\""
                else
                    let register = lhs[0]

                    match Keymap.tokenize rhs with
                    | [] -> Result.Error $"@{register} has no steps — delete the line to clear the register"
                    | tokens ->
                        let parsed =
                            tokens
                            |> List.fold
                                (fun acc token ->
                                    match acc, parseStepToken token with
                                    | Result.Ok steps, Result.Ok step -> Result.Ok(step :: steps)
                                    | Result.Ok _, Result.Error reason -> Result.Error reason
                                    | Result.Error reason, _ -> Result.Error reason)
                                (Result.Ok [])

                        parsed |> Result.map (fun steps -> Some(register, List.rev steps))

    /// Parse a whole file's worth of lines into registers plus a list of
    /// `macros:<n>: <reason>` errors — `Keymap.parse`'s shape, so the two
    /// files report identically. Later lines for the same register win.
    let parse (lines: string seq) : Map<char, MacroStep list> * string list =
        let mutable registers = Map.empty
        let mutable errors = []

        lines
        |> Seq.iteri (fun i line ->
            match parseLine line with
            | Ok None -> ()
            | Ok(Some(register, steps)) -> registers <- Map.add register steps registers
            | Result.Error reason -> errors <- $"macros:{i + 1}: {reason}" :: errors)

        registers, List.rev errors

    /// Render the canonical file: the grammar header, then one line per
    /// register in register order. Registers whose char cannot round-trip
    /// through the line grammar (`#`, `=`, whitespace) and steps with no
    /// parse syntax are skipped — neither can occur in a recorded macro.
    let render (registers: Map<char, MacroStep list>) : string =
        let lines =
            registers
            |> Map.toList
            |> List.choose (fun (register, steps) ->
                if register = '#' || register = '=' || System.Char.IsWhiteSpace register then
                    None
                else
                    match steps |> List.choose renderStep with
                    | [] -> None
                    | tokens ->
                        let joined = String.concat " " tokens
                        Some $"{register} = {joined}")

        match lines with
        | [] -> header
        | _ -> header + "\n" + String.concat "\n" lines + "\n"

/// File location + load/save for the user macros file. Sibling of
/// `KeymapIO`; the `*From`/`*To`/`*At` forms take an explicit path so
/// tests never touch the real config directory.
[<RequireQualifiedAccess>]
module MacroIO =

    let path () =
        Path.Combine(ConfigIO.directory (), "macros")

    /// Read + parse a macros file. A missing file is not an error — the
    /// editor boots with empty registers, like `KeymapIO.load`.
    let loadFrom (filePath: string) : Map<char, MacroStep list> * string list =
        try
            if not (File.Exists filePath) then
                Map.empty, []
            else
                MacroFile.parse (File.ReadAllLines filePath)
        with ex ->
            Map.empty, [ $"macros: {ex.Message}" ]

    let load () = loadFrom (path ())

    /// Write the canonical form atomically (same primitive as config and
    /// buffer saves, so a crash can never leave a half-written file).
    let saveTo (filePath: string) (registers: Map<char, MacroStep list>) =
        File.writeAllTextAtomic filePath (MacroFile.render registers)

    let save (registers: Map<char, MacroStep list>) = saveTo (path ()) registers

    /// Create the file (grammar header + current registers) if missing —
    /// the `:macros` picker's Edit action always has something to open.
    let ensureFileAt (filePath: string) (registers: Map<char, MacroStep list>) =
        if not (File.Exists filePath) then
            saveTo filePath registers

    let ensureFile (registers: Map<char, MacroStep list>) = ensureFileAt (path ()) registers
