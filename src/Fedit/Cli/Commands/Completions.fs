/// Shell-completion script generator. Walks a `CliCommandDescriptor`
/// tree (built from the real `CliApp<_>` definitions) and emits a
/// shell-native completion script. Each shell gets its own emitter
/// because the formats diverge sharply.
///
/// The walk is fully recursive: zsh emits one function per command
/// node; bash and PowerShell flatten the tree into a transition table
/// plus one `case`/`switch` arm per node; fish composes nested
/// `__fish_seen_subcommand_from` conditions. Adding a third (or Nth)
/// subcommand level needs no emitter change.
module Fedit.Cli.Commands.Completions

// FS3261: nullness warning on Path.GetDirectoryName (returns nullable).
// Matches the convention in Plugins.fs.
#nowarn "3261"

open System
open System.IO
open System.Text
open Fedit
open Fedit.Cli

type Shell =
    | Zsh
    | Bash
    | Fish
    | Pwsh
    | Nushell
    | Elvish
    | Xonsh
    | Yash
    | Murex

let parseShell (s: string) : Result<Shell, string> =
    match s.ToLowerInvariant() with
    | "zsh" -> Ok Zsh
    | "bash" -> Ok Bash
    | "fish" -> Ok Fish
    | "pwsh"
    | "powershell" -> Ok Pwsh
    | "nu"
    | "nushell" -> Ok Nushell
    | "elv"
    | "elvish" -> Ok Elvish
    | "xonsh" -> Ok Xonsh
    | "yash" -> Ok Yash
    | "murex" -> Ok Murex
    | other ->
        Result.Error $"unsupported shell '{other}' (supported: zsh, bash, fish, pwsh, nu, elvish, xonsh, yash, murex)"

/// Standard install location per shell. `~/.zsh/completions/_fedit`
/// matches the fpath convention; bash uses XDG; fish auto-discovers
/// `~/.config/fish/completions/<cmd>.fish`. PowerShell, Nushell, Elvish
/// and xonsh have no drop-in autoload dir — the file is sourced/used
/// from the shell's rc (see `run`).
let installPath (shell: Shell) : string =
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

    // Canonical `/` — these are Unix-shell config locations; .NET writes to
    // `/`-paths fine on Windows, and it keeps the path stable across platforms.
    Paths.norm (
        match shell with
        | Zsh -> Path.Combine(home, ".zsh", "completions", "_fedit")
        | Bash -> Path.Combine(home, ".local", "share", "bash-completion", "completions", "fedit")
        | Fish -> Path.Combine(home, ".config", "fish", "completions", "fedit.fish")
        | Pwsh -> Path.Combine(home, ".config", "powershell", "completions", "fedit.ps1")
        | Nushell -> Path.Combine(home, ".config", "nushell", "completions", "fedit.nu")
        | Elvish -> Path.Combine(home, ".config", "elvish", "lib", "fedit-completions.elv")
        | Xonsh -> Path.Combine(home, ".config", "xonsh", "completions", "fedit.xsh")
        // yash autoloads `completion/<cmd>` by filename from $YASH_LOADPATH.
        | Yash -> Path.Combine(home, ".local", "share", "yash", "completion", "fedit")
        | Murex -> Path.Combine(home, ".config", "fedit", "completions", "fedit.mx")
    )

// ─────────────────────────────────────────────────────────────────────
// Shared helpers
// ─────────────────────────────────────────────────────────────────────

/// Forms shown in completion menus: canonical name + visible aliases.
let private visibleForms (node: CliCommandDescriptor) = node.Name :: node.Aliases

/// All forms a subcommand answers to, including hidden aliases. Used for
/// dispatch/matching so `fedit plugin install` works like `fedit plugins
/// install`.
let private allSurfaceForms (node: CliCommandDescriptor) =
    node.Name :: node.Aliases @ node.HiddenAliases

/// The dynamic-completion invocation, as a `fedit …` command. We always
/// invoke `fedit` from PATH — completions only exist after the binary is
/// installed, so PATH is the right contract.
let private dynamicCommandString (args: string list) = "fedit " + String.Join(' ', args)

/// Flatten the descriptor tree into `(canonicalPath, node)` pairs, root
/// first (path `[]`), then a depth-first walk of subcommands. The path
/// is the sequence of canonical subcommand names from the root.
let rec private flatten (path: string list) (node: CliCommandDescriptor) =
    (path, node)
    :: (node.Subcommands |> List.collect (fun c -> flatten (path @ [ c.Name ]) c))

/// Space-joined canonical path; `[]` (root) becomes the empty string.
let private pathKey (path: string list) = String.concat " " path

/// Value-taking long flags across the whole tree, de-duplicated. Used by
/// bash/pwsh to (a) complete an option's value and (b) skip that value
/// when resolving the command path.
let private valueLongs (root: CliCommandDescriptor) =
    flatten [] root
    |> List.collect (fun (_, n) ->
        n.Options
        |> List.choose (fun o ->
            match o.Value with
            | RequiredValue _ -> Some(o, $"--{o.Long}")
            | NoValue -> None))
    |> List.distinctBy snd

let private bashFlagList (opts: CliOptionDescriptor list) =
    opts
    |> List.collect (fun o ->
        match o.Short with
        | Some s -> [ $"-{s}"; $"--{o.Long}" ]
        | None -> [ $"--{o.Long}" ])
    |> String.concat " "

// ─────────────────────────────────────────────────────────────────────
// Zsh emitter — one function per command node, recursive dispatch.
// ─────────────────────────────────────────────────────────────────────

let private zshEscape (s: string) =
    // zsh inside `'…'` only needs `'` → `'\''` and `[` `]` escaping for
    // `_values` descriptions. Keep it minimal.
    s.Replace("'", "'\\''").Replace("[", "\\[").Replace("]", "\\]")

let private zshAction (kind: CliCompletionKind) =
    match kind with
    | FilePath -> "_files"
    | DirectoryPath -> "_files -/"
    | DynamicCommand args -> $"($({dynamicCommandString args}))"
    | Choices values -> $"({String.Join(' ', values)})"
    | NoHint -> " "

let private zshOptionLine (opt: CliOptionDescriptor) =
    let desc = zshEscape opt.Description

    let valueSpec =
        match opt.Value with
        | NoValue -> ""
        | RequiredValue name -> $":{name}:{zshAction opt.Completion}"

    match opt.Short, opt.Long with
    | Some s, long -> $"'(-{s} --{long})'{{-{s},--{long}}}'[{desc}]{valueSpec}'"
    | None, long -> $"'--{long}[{desc}]{valueSpec}'"

let private zshSubcommandValues (subs: CliCommandDescriptor list) =
    subs
    |> List.collect (fun s -> visibleForms s |> List.map (fun n -> "'" + n + "[" + zshEscape s.Summary + "]'"))
    |> String.concat " "

let rec private emitZshNode (sb: StringBuilder) (funcName: string) (node: CliCommandDescriptor) =
    let add (s: string) = sb.AppendLine s |> ignore
    add (funcName + "() {")

    if List.isEmpty node.Subcommands then
        // Leaf: options + first positional (no current node has more
        // than one positional).
        add "    _arguments -C \\"

        for opt in node.Options do
            add ("        " + zshOptionLine opt + " \\")

        match node.Positionals with
        | [] -> add "        && return 0"
        | pos :: _ -> add ("        '*:" + pos.Name + ":" + zshAction pos.Completion + "'")
    else
        // Branch on the first remaining word, then dispatch to the
        // matching child function (recursively defined below).
        add "    local context state line"
        add "    _arguments -C \\"

        for opt in node.Options do
            add ("        " + zshOptionLine opt + " \\")

        add "        '1: :->cmd' \\"
        add "        '*::arg:->args' && return 0"
        add ""
        add "    case \"$state\" in"
        add "        cmd)"
        add ("            _values 'command' " + zshSubcommandValues node.Subcommands)

        // A branch node can also take a file/dir positional in word 1
        // (the root does: `fedit <path>`), so the cmd state must offer
        // files alongside the subcommand menu. Branch nodes without a
        // file positional (e.g. `plugins`) keep the menu only.
        match node.Positionals with
        | pos :: _ ->
            match pos.Completion with
            | FilePath
            | DirectoryPath -> add ("            " + zshAction pos.Completion)
            | _ -> ()
        | [] -> ()

        add "            ;;"
        add "        args)"
        add "            case \"${words[1]}\" in"

        for child in node.Subcommands do
            let forms = allSurfaceForms child |> String.concat "|"
            add ("                " + forms + ") " + funcName + "__" + child.Name + " ;;")

        match node.Positionals with
        | pos :: _ -> add ("                *) " + zshAction pos.Completion + " ;;")
        | [] -> ()

        add "            esac"
        add "            ;;"
        add "    esac"

    add "}"
    add ""

    for child in node.Subcommands do
        emitZshNode sb (funcName + "__" + child.Name) child

let private emitZsh (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    sb.AppendLine "#compdef fedit" |> ignore
    sb.AppendLine "# Generated by `fedit completions zsh`." |> ignore
    sb.AppendLine "" |> ignore
    emitZshNode sb "_fedit" root
    // The file holds several function definitions, so zsh's autoload
    // only *defines* them on first TAB — it never calls the completer
    // and the first completion attempt yields nothing. Call it when
    // running as the completion function (funcstack[1] is `_fedit`
    // during autoload-invoke); a plain `source` leaves funcstack
    // empty, so completion builtins are never run outside completion.
    sb.AppendLine "" |> ignore
    sb.AppendLine "if [ \"$funcstack[1]\" = \"_fedit\" ]; then" |> ignore
    sb.AppendLine "    _fedit \"$@\"" |> ignore
    sb.AppendLine "fi" |> ignore
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Bash emitter — flat transition table + one case arm per node.
// ─────────────────────────────────────────────────────────────────────

/// Statements that populate COMPREPLY for a completion kind. File and
/// directory kinds read candidates line-by-line so filenames with
/// spaces survive word-splitting, and enable `-o filenames` so
/// directories keep a trailing `/` instead of getting a space. The
/// compopt call is guarded: the script is also sourced by OSH, and the
/// probe scripts invoke `_fedit` outside a real completion session,
/// where compopt errors. The read loop appends, which is safe for both
/// `op` spellings — COMPREPLY is reset at the top of `_fedit`.
let private bashAssign (op: string) (kind: CliCompletionKind) =
    let fileLines (gen: string) =
        [ "type compopt >/dev/null 2>&1 && compopt -o filenames 2>/dev/null || true"
          "while IFS= read -r f; do COMPREPLY+=(\"$f\"); done < <(compgen "
          + gen
          + " -- \"$cur\")" ]

    match kind with
    | FilePath -> fileLines "-f"
    | DirectoryPath -> fileLines "-d"
    | DynamicCommand args ->
        [ "COMPREPLY"
          + op
          + "( $(compgen -W \"$("
          + dynamicCommandString args
          + " 2>/dev/null)\" -- \"$cur\") )" ]
    | Choices values ->
        [ "COMPREPLY"
          + op
          + "( $(compgen -W \""
          + String.Join(' ', values)
          + "\" -- \"$cur\") )" ]
    | NoHint -> [ ":" ]

let private emitBash (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    let add (s: string) = sb.AppendLine s |> ignore
    let nodes = flatten [] root
    let values = valueLongs root

    add "# Generated by `fedit completions bash`."
    add "_fedit() {"
    add "    local cur prev"
    add "    COMPREPLY=()"
    add "    cur=\"${COMP_WORDS[COMP_CWORD]}\""
    add "    prev=\"${COMP_WORDS[COMP_CWORD-1]}\""
    add ""

    // Complete the value of a value-taking option if it was just typed.
    for opt, flag in values do
        add ("    if [[ \"$prev\" == \"" + flag + "\" ]]; then")

        for line in bashAssign "=" opt.Completion do
            add ("        " + line)

        add "        return"
        add "    fi"

    if not (List.isEmpty values) then
        add ""

    // Resolve the canonical command path, skipping flags and the values
    // they consume.
    add "    local path=\"\" i w p"
    add "    for ((i=1; i<COMP_CWORD; i++)); do"
    add "        w=\"${COMP_WORDS[i]}\""
    add "        p=\"${COMP_WORDS[i-1]}\""
    add "        case \"$w\" in -*) continue ;; esac"

    if not (List.isEmpty values) then
        let flags = values |> List.map snd |> String.concat "|"
        add ("        case \"$p\" in " + flags + ") continue ;; esac")

    add "        case \"$path|$w\" in"

    for path, node in nodes do
        for child in node.Subcommands do
            let arms =
                allSurfaceForms child
                |> List.map (fun f -> "\"" + pathKey path + "|" + f + "\"")
                |> String.concat "|"

            add ("            " + arms + ") path=\"" + pathKey (path @ [ child.Name ]) + "\" ;;")

    add "            *) break ;;"
    add "        esac"
    add "    done"
    add ""
    add "    case \"$path\" in"

    for path, node in nodes do
        add ("        \"" + pathKey path + "\")")
        add "            if [[ \"$cur\" == -* ]]; then"

        add (
            "                COMPREPLY=( $(compgen -W \""
            + bashFlagList node.Options
            + "\" -- \"$cur\") )"
        )

        add "            else"

        let names = node.Subcommands |> List.collect visibleForms |> String.concat " "

        if names <> "" then
            add ("                COMPREPLY=( $(compgen -W \"" + names + "\" -- \"$cur\") )")

        match node.Positionals with
        | pos :: _ ->
            // Append to the subcommand list when both are valid (e.g. the
            // root takes a file path or a subcommand); otherwise assign.
            let op = if names <> "" then "+=" else "="
            let actions = bashAssign op pos.Completion

            if not (names <> "" && actions = [ ":" ]) then
                for line in actions do
                    add ("                " + line)
        | [] ->
            if names = "" then
                add "                :"

        add "            fi"
        add "            ;;"

    add "    esac"
    add "}"
    add "complete -F _fedit fedit"
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Fish emitter — recursive `__fish_seen_subcommand_from` conditions.
// ─────────────────────────────────────────────────────────────────────

let private fishEscape (s: string) =
    s.Replace("\\", "\\\\").Replace("'", "\\'")

/// Quote a fish `-n` predicate only when it contains spaces (a compound
/// `cond; and cond`); bare builtins like `__fish_use_subcommand` stay
/// unquoted to match fish's own conventions.
let private fishCond (c: string) = if c.Contains ' ' then $"'{c}'" else c

let private fishOption (sb: StringBuilder) (cond: string option) (opt: CliOptionDescriptor) =
    let parts = StringBuilder()
    parts.Append "complete -c fedit" |> ignore

    match cond with
    | Some c -> parts.Append($" -n {fishCond c}") |> ignore
    | None -> ()

    match opt.Short with
    | Some s -> parts.Append($" -s {s}") |> ignore
    | None -> ()

    parts.Append($" -l {opt.Long}") |> ignore

    match opt.Value with
    | RequiredValue _ ->
        parts.Append " -r" |> ignore

        match opt.Completion with
        | FilePath -> parts.Append " -F" |> ignore
        | DirectoryPath -> parts.Append " -a '(__fish_complete_directories)'" |> ignore
        | DynamicCommand args -> parts.Append $" -a '({dynamicCommandString args} 2>/dev/null)'" |> ignore
        | Choices values -> parts.Append $" -a '{String.Join(' ', values)}'" |> ignore
        | NoHint -> ()
    | NoValue -> ()

    parts.Append($" -d '{fishEscape opt.Description}'") |> ignore
    sb.AppendLine(parts.ToString()) |> ignore

let private fishPositional (sb: StringBuilder) (cond: string) (kind: CliCompletionKind) =
    let add (s: string) = sb.AppendLine s |> ignore
    let n = fishCond cond

    match kind with
    | FilePath -> add $"complete -c fedit -n {n} -F"
    | DirectoryPath -> add $"complete -c fedit -n {n} -a '(__fish_complete_directories)'"
    | DynamicCommand args -> add $"complete -c fedit -n {n} -a '({dynamicCommandString args} 2>/dev/null)'"
    | Choices values -> add $"complete -c fedit -n {n} -a '{String.Join(' ', values)}'"
    | NoHint -> ()

/// `ctx` is the condition that holds when we are inside this node's
/// context: `None` at the root (children gated on `__fish_use_subcommand`),
/// `Some cond` for any nested node.
let rec private emitFishNode (sb: StringBuilder) (ctx: string option) (node: CliCommandDescriptor) =
    let add (s: string) = sb.AppendLine s |> ignore

    for opt in node.Options do
        fishOption sb ctx opt

    if List.isEmpty node.Subcommands then
        match node.Positionals, ctx with
        | (pos :: _), Some c -> fishPositional sb c pos.Completion
        | (pos :: _), None -> fishPositional sb "__fish_use_subcommand" pos.Completion
        | [], _ -> ()
    else
        let childForms =
            node.Subcommands |> List.collect allSurfaceForms |> String.concat " "

        let menuGate =
            match ctx with
            | None -> "__fish_use_subcommand"
            | Some c -> $"{c}; and not __fish_seen_subcommand_from {childForms}"

        for child in node.Subcommands do
            for form in visibleForms child do
                add $"complete -c fedit -n {fishCond menuGate} -a {form} -d '{fishEscape child.Summary}'"

        match node.Positionals with
        | pos :: _ ->
            let posGate =
                match ctx with
                | None -> "__fish_use_subcommand"
                | Some c -> c

            fishPositional sb posGate pos.Completion
        | [] -> ()

        for child in node.Subcommands do
            let forms = allSurfaceForms child |> String.concat " "

            let childCtx =
                match ctx with
                | None -> $"__fish_seen_subcommand_from {forms}"
                | Some c -> $"{c}; and __fish_seen_subcommand_from {forms}"

            emitFishNode sb (Some childCtx) child

let private emitFish (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    sb.AppendLine "# Generated by `fedit completions fish`." |> ignore
    sb.AppendLine "" |> ignore
    sb.AppendLine "# Disable default file completion at the root." |> ignore
    sb.AppendLine "complete -c fedit -f" |> ignore
    sb.AppendLine "" |> ignore
    emitFishNode sb None root
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// PowerShell emitter — Register-ArgumentCompleter, transition table.
// ─────────────────────────────────────────────────────────────────────

let private pwshEscape (s: string) = s.Replace("'", "''")

let private emitPwsh (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    let add (s: string) = sb.AppendLine s |> ignore
    let nodes = flatten [] root
    let values = valueLongs root |> List.map snd

    add "# Generated by `fedit completions pwsh`."
    add "using namespace System.Management.Automation"
    add ""
    add "Register-ArgumentCompleter -Native -CommandName fedit -ScriptBlock {"
    add "    param($wordToComplete, $commandAst, $cursorPosition)"
    add ""

    // Transition table: "$path|word" -> deeper canonical path.
    add "    $transitions = @{"

    for path, node in nodes do
        for child in node.Subcommands do
            for form in allSurfaceForms child do
                add (
                    "        '"
                    + (pathKey path + "|" + form)
                    + "' = '"
                    + pathKey (path @ [ child.Name ])
                    + "'"
                )

    add "    }"

    add (
        "    $valueFlags = @("
        + (values |> List.map (fun s -> "'" + s + "'") |> String.concat ", ")
        + ")"
    )

    add ""
    add "    $elements = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { \"$_\" })"
    add "    $path = ''"
    add "    for ($i = 0; $i -lt $elements.Count; $i++) {"
    add "        $w = $elements[$i]"
    add "        if ($w -eq $wordToComplete) { continue }"
    add "        if ($w.StartsWith('-')) { continue }"
    add "        if ($i -gt 0 -and ($valueFlags -contains $elements[$i - 1])) { continue }"
    add "        $key = \"$path|$w\""
    add "        if ($transitions.ContainsKey($key)) { $path = $transitions[$key] } else { break }"
    add "    }"
    add ""
    add "    $static = [System.Collections.Generic.List[CompletionResult]]::new()"
    add "    $extra = [System.Collections.Generic.List[CompletionResult]]::new()"
    add ""
    add "    switch ($path) {"

    for path, node in nodes do
        let key = if List.isEmpty path then "''" else "'" + pathKey path + "'"

        add ("        " + key + " {")

        for child in node.Subcommands do
            for form in visibleForms child do
                add (
                    "            $static.Add([CompletionResult]::new('"
                    + form
                    + "', '"
                    + form
                    + "', [CompletionResultType]::ParameterValue, '"
                    + pwshEscape child.Summary
                    + "'))"
                )

        for opt in node.Options do
            match opt.Short with
            | Some s ->
                add (
                    "            $static.Add([CompletionResult]::new('-"
                    + string s
                    + "', '-"
                    + string s
                    + "', [CompletionResultType]::ParameterName, '"
                    + pwshEscape opt.Description
                    + "'))"
                )
            | None -> ()

            add (
                "            $static.Add([CompletionResult]::new('--"
                + opt.Long
                + "', '--"
                + opt.Long
                + "', [CompletionResultType]::ParameterName, '"
                + pwshEscape opt.Description
                + "'))"
            )

        match node.Positionals with
        | pos :: _ ->
            match pos.Completion with
            | Choices vals ->
                for v in vals do
                    add (
                        "            $static.Add([CompletionResult]::new('"
                        + v
                        + "', '"
                        + v
                        + "', [CompletionResultType]::ParameterValue, '"
                        + v
                        + "'))"
                    )
            | FilePath ->
                add
                    "            $extra.AddRange([System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete))"
            | DirectoryPath ->
                // CompleteFilename has no directories-only mode; keep the
                // containers (directories) and drop plain files.
                add
                    "            foreach ($r in [System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete)) {"

                add
                    "                if ($r.ResultType -eq [CompletionResultType]::ProviderContainer) { $extra.Add($r) }"

                add "            }"
            | DynamicCommand args ->
                add ("            foreach ($n in @(& " + dynamicCommandString args + " 2>$null)) {")

                add
                    "                $static.Add([CompletionResult]::new($n, $n, [CompletionResultType]::ParameterValue, $n))"

                add "            }"
            | NoHint -> ()
        | [] -> ()

        add "        }"

    add "    }"
    add ""
    add "    $out = @($static | Where-Object { $_.CompletionText -like \"$wordToComplete*\" })"
    add "    $out + @($extra)"
    add "}"
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Nushell emitter — declarative `export extern` + `nu-complete` defs.
// ─────────────────────────────────────────────────────────────────────

let private nuShape (kind: CliCompletionKind) =
    match kind with
    | FilePath
    | DirectoryPath -> "path"
    | _ -> "string"

/// Stable completer-command name for a node's positional, keyed by the
/// canonical path so alias-path externs can share one definition.
let private nuCompleterSlug (canonPath: string list) (posName: string) =
    let p =
        if List.isEmpty canonPath then
            ""
        else
            " " + pathKey canonPath

    "nu-complete fedit" + p + " " + posName

let private emitNushell (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    let add (s: string) = sb.AppendLine s |> ignore
    add "# Generated by `fedit completions nu`."
    add ""

    // Completer commands for Choices/Dynamic positionals (canonical nodes).
    for path, node in flatten [] root do
        match node.Positionals with
        | pos :: _ ->
            let slug = nuCompleterSlug path pos.Name

            match pos.Completion with
            | Choices vals -> add ("def \"" + slug + "\" [] { [" + String.Join(' ', vals) + "] }")
            | DynamicCommand args -> add ("def \"" + slug + "\" [] { ^" + dynamicCommandString args + " | lines }")
            | _ -> ()
        | [] -> ()

    add ""

    // One `export extern` per surface path (canonical + aliases) so the
    // singular `plugin` alias completes like `plugins`.
    let rec externs (prefixes: string list) (canonPath: string list) (node: CliCommandDescriptor) =
        for p in prefixes do
            add ("export extern \"" + p + "\" [")

            match node.Positionals with
            | pos :: _ ->
                let shape =
                    match pos.Completion with
                    | Choices _
                    | DynamicCommand _ -> "string@\"" + nuCompleterSlug canonPath pos.Name + "\""
                    | FilePath
                    | DirectoryPath -> "path"
                    | NoHint -> "string"

                add ("  " + pos.Name + "?: " + shape + "  # " + pos.Description)
            | [] -> ()

            for opt in node.Options do
                let namePart =
                    match opt.Short with
                    | Some s -> "--" + opt.Long + " (-" + string s + ")"
                    | None -> "--" + opt.Long

                let valuePart =
                    match opt.Value with
                    | RequiredValue _ -> ": " + nuShape opt.Completion
                    | NoValue -> ""

                add ("  " + namePart + valuePart + "  # " + opt.Description)

            add "]"
            add ""

        for c in node.Subcommands do
            let childPrefixes =
                [ for p in prefixes do
                      for f in allSurfaceForms c -> p + " " + f ]

            externs childPrefixes (canonPath @ [ c.Name ]) c

    externs [ "fedit" ] [] root
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Elvish emitter — arg-completer closure + per-path closure maps.
// (Elvish has no switch and treats a line-leading `elif` as a command,
//  so dispatch goes through maps keyed by the resolved path.)
// ─────────────────────────────────────────────────────────────────────

let private emitElvish (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    let add (s: string) = sb.AppendLine s |> ignore
    let nodes = flatten [] root
    let values = valueLongs root |> List.map snd

    add "# Generated by `fedit completions elvish`."
    add "use str"
    add ""
    add "var fedit-transitions = ["

    for path, node in nodes do
        for child in node.Subcommands do
            for form in allSurfaceForms child do
                add (
                    "  &'"
                    + (pathKey path + "|" + form)
                    + "'='"
                    + pathKey (path @ [ child.Name ])
                    + "'"
                )

    add "]"
    add ("var fedit-value-flags = [" + String.Join(' ', values) + "]")
    add ""

    // Flags offered when the current word starts with `-`, keyed by path.
    add "var fedit-flags = ["

    for path, node in nodes do
        add ("  &'" + pathKey path + "'=[" + bashFlagList node.Options + "]")

    add "]"
    add ""

    // Argument completers (subcommands + positional), keyed by path. Only
    // paths that complete something get an entry.
    add "var fedit-args = ["

    for path, node in nodes do
        let hasPositional =
            match node.Positionals with
            | (pos :: _) when pos.Completion <> NoHint -> true
            | _ -> false

        if not (List.isEmpty node.Subcommands) || hasPositional then
            add ("  &'" + pathKey path + "'={|cur|")

            let subForms = node.Subcommands |> List.collect visibleForms |> String.concat " "

            if subForms <> "" then
                add ("    put " + subForms)

            match node.Positionals with
            | pos :: _ ->
                match pos.Completion with
                | FilePath
                | DirectoryPath -> add "    edit:complete-filename $cur"
                | Choices vals -> add ("    put " + String.Join(' ', vals))
                | DynamicCommand args ->
                    add (
                        "    for l [(str:split \"\\n\" ("
                        + dynamicCommandString args
                        + " 2>/dev/null | slurp))] { if (not-eq $l '') { put $l } }"
                    )
                | NoHint -> ()
            | [] -> ()

            add "  }"

    add "]"
    add ""
    add "set edit:completion:arg-completer[fedit] = {|@args|"
    add "  var words = $args[1..]"
    add "  var cur = ''"
    add "  if (> (count $words) 0) { set cur = $words[-1] }"
    add "  var path = ''"
    add "  var prev = ''"
    add "  var upto = (- (count $words) 1)"
    add "  if (> $upto 0) {"
    add "    for w $words[0..$upto] {"
    add "      if (str:has-prefix $w '-') { set prev = $w; continue }"
    add "      if (has-value $fedit-value-flags $prev) { set prev = $w; continue }"
    add "      var key = $path'|'$w"
    add "      if (has-key $fedit-transitions $key) { set path = $fedit-transitions[$key] } else { break }"
    add "      set prev = $w"
    add "    }"
    add "  }"
    add "  if (str:has-prefix $cur '-') {"
    add "    if (has-key $fedit-flags $path) { all $fedit-flags[$path] }"
    add "  } else {"
    add "    if (has-key $fedit-args $path) { $fedit-args[$path] $cur }"
    add "  }"
    add "}"
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Xonsh emitter — a contextual command completer registered for `fedit`.
// ─────────────────────────────────────────────────────────────────────

let private pyStr (s: string) =
    "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'"

let private emitXonsh (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    let add (s: string) = sb.AppendLine s |> ignore
    let nodes = flatten [] root
    let values = valueLongs root |> List.map snd

    add "# Generated by `fedit completions xonsh`."
    add "import subprocess"
    add "from xonsh.completers.tools import contextual_command_completer_for, RichCompletion"
    add "from xonsh.completers.completer import add_one_completer"
    add ""

    add "_FEDIT_TRANSITIONS = {"

    for path, node in nodes do
        for child in node.Subcommands do
            for form in allSurfaceForms child do
                add (
                    "    "
                    + pyStr (pathKey path + "|" + form)
                    + ": "
                    + pyStr (pathKey (path @ [ child.Name ]))
                    + ","
                )

    add "}"

    add (
        "_FEDIT_VALUE_FLAGS = {"
        + (values |> List.map pyStr |> String.concat ", ")
        + "}"
    )

    add ""

    // Per-path subcommand and flag tables.
    add "_FEDIT_SUBS = {"

    for path, node in nodes do
        let entries =
            node.Subcommands
            |> List.collect (fun c ->
                visibleForms c
                |> List.map (fun f -> "(" + pyStr f + ", " + pyStr c.Summary + ")"))
            |> String.concat ", "

        add ("    " + pyStr (pathKey path) + ": [" + entries + "],")

    add "}"
    add "_FEDIT_FLAGS = {"

    for path, node in nodes do
        let entries =
            node.Options
            |> List.collect (fun o ->
                let pair n =
                    "(" + pyStr n + ", " + pyStr o.Description + ")"

                match o.Short with
                | Some s -> [ pair ("-" + string s); pair ("--" + o.Long) ]
                | None -> [ pair ("--" + o.Long) ])
            |> String.concat ", "

        add ("    " + pyStr (pathKey path) + ": [" + entries + "],")

    add "}"
    // Positional kind per path: 'file' | 'choices:<vals>' | 'dynamic:<cmd>'.
    add "_FEDIT_POS = {"

    for path, node in nodes do
        match node.Positionals with
        | pos :: _ ->
            let kind =
                match pos.Completion with
                | FilePath
                | DirectoryPath -> Some "file"
                | Choices vals -> Some("choices:" + String.Join(' ', vals))
                | DynamicCommand args -> Some("dynamic:" + dynamicCommandString args)
                | NoHint -> None

            match kind with
            | Some k -> add ("    " + pyStr (pathKey path) + ": " + pyStr k + ",")
            | None -> ()
        | [] -> ()

    add "}"
    add ""
    add "@contextual_command_completer_for('fedit')"
    add "def _fedit_completer(command):"
    add "    words = [a.value for a in command.args]"
    add "    prefix = command.prefix"
    add "    path = ''"
    add "    prev = ''"
    add "    for w in words[1:command.arg_index]:"
    add "        if w.startswith('-'):"
    add "            prev = w"
    add "            continue"
    add "        if prev in _FEDIT_VALUE_FLAGS:"
    add "            prev = w"
    add "            continue"
    add "        key = path + '|' + w"
    add "        if key in _FEDIT_TRANSITIONS:"
    add "            path = _FEDIT_TRANSITIONS[key]"
    add "        else:"
    add "            break"
    add "        prev = w"
    add "    out = set()"
    add "    if prefix.startswith('-'):"
    add "        for name, desc in _FEDIT_FLAGS.get(path, []):"
    add "            if name.startswith(prefix):"
    add "                out.add(RichCompletion(name, description=desc))"
    add "        return out"
    add "    for name, desc in _FEDIT_SUBS.get(path, []):"
    add "        if name.startswith(prefix):"
    add "            out.add(RichCompletion(name, description=desc, append_space=True))"
    add "    kind = _FEDIT_POS.get(path)"
    add "    if kind == 'file':"
    add "        # Merge path completions with any subcommand matches instead"
    add "        # of short-circuiting: `fedit path/to/fi<TAB>` must offer"
    add "        # files even while subcommand names still match the prefix."
    add "        try:"
    add "            from xonsh.completers.path import contextual_complete_path as complete_path"
    add "            res = complete_path(command)"
    add "            paths, lprefix = res if isinstance(res, tuple) else (res, len(prefix))"
    add "            for p in paths:"
    add "                if isinstance(p, RichCompletion):"
    add "                    out.add(p)"
    add "                else:"
    add "                    out.add(RichCompletion(str(p), prefix_len=lprefix))"
    add "        except Exception:"
    add "            pass"
    add "        return out or None"
    add "    if out:"
    add "        return out"
    add "    if kind and kind.startswith('choices:'):"
    add "        for v in kind[len('choices:'):].split():"
    add "            if v.startswith(prefix):"
    add "                out.add(RichCompletion(v, append_space=True))"
    add "    elif kind and kind.startswith('dynamic:'):"
    add "        try:"
    add "            res = subprocess.run(kind[len('dynamic:'):].split(), capture_output=True, text=True)"
    add "            for line in res.stdout.split('\\n'):"
    add "                if line and line.startswith(prefix):"
    add "                    out.add(RichCompletion(line, append_space=True))"
    add "        except Exception:"
    add "            pass"
    add "    return out or None"
    add ""
    add "add_one_completer('fedit', _fedit_completer, 'start')"
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Yash emitter — a completion/fedit function, transition table dispatch.
// ─────────────────────────────────────────────────────────────────────

let private emitYash (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    let add (s: string) = sb.AppendLine s |> ignore
    let nodes = flatten [] root
    let values = valueLongs root |> List.map snd

    add "# Generated by `fedit completions yash`."
    add "function completion/fedit {"
    // yash's $WORDS excludes the word being completed (that's $TARGETWORD),
    // so every element WORDS[2..#] is a finished word to walk — bound is -le.
    add "    typeset path='' i=2 w prev=''"
    add "    while [ \"$i\" -le \"${WORDS[#]}\" ]; do"
    add "        w=${WORDS[i]}"
    add "        case $w in (-*) prev=$w; i=$((i+1)); continue;; esac"

    if not (List.isEmpty values) then
        let pat = values |> List.map (fun s -> "'" + s + "'") |> String.concat "|"
        add ("        case $prev in (" + pat + ") prev=$w; i=$((i+1)); continue;; esac")

    add "        case \"$path|$w\" in"

    for path, node in nodes do
        for child in node.Subcommands do
            let arms =
                allSurfaceForms child
                |> List.map (fun f -> "'" + pathKey path + "|" + f + "'")
                |> String.concat "|"

            add ("            (" + arms + ") path='" + pathKey (path @ [ child.Name ]) + "';;")

    add "            (*) break;;"
    add "        esac"
    add "        prev=$w; i=$((i+1))"
    add "    done"
    add "    case $path in"

    for path, node in nodes do
        add ("        ('" + pathKey path + "')")
        add "            case $TARGETWORD in"
        add ("                (-*) complete -- " + bashFlagList node.Options + ";;")

        let subs = node.Subcommands |> List.collect visibleForms |> String.concat " "

        let posCmd =
            match node.Positionals with
            | pos :: _ ->
                match pos.Completion with
                | FilePath -> "complete -f"
                | DirectoryPath -> "complete -d"
                | Choices vals -> "complete -- " + String.Join(' ', vals)
                | DynamicCommand args -> "complete -- $(" + dynamicCommandString args + " 2>/dev/null)"
                | NoHint -> ":"
            | [] -> ":"

        let elseCmd =
            match subs, posCmd with
            | "", c -> c
            | s, ":" -> "complete -- " + s
            | s, c -> "complete -- " + s + "; " + c

        add ("                (*) " + elseCmd + ";;")
        add "            esac;;"

    add "    esac"
    add "}"
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Murex emitter — a single declarative `autocomplete set` JSON schema.
// ─────────────────────────────────────────────────────────────────────

let private murexStr (s: string) =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// Recursive murex autocomplete schema for a node: one positional index
/// whose `Flags` are the visible children (+ choice values + the node's
/// own flags) and whose `FlagValues` recurse into each child (canonical,
/// visible, and hidden surface forms alike).
let rec private murexNode (node: CliCommandDescriptor) : string =
    let flagCands =
        (node.Subcommands |> List.collect visibleForms)
        @ (match node.Positionals with
           | (pos :: _) ->
               match pos.Completion with
               | Choices vals -> vals
               | _ -> []
           | [] -> [])
        @ (node.Options
           |> List.collect (fun o ->
               match o.Short with
               | Some s -> [ $"-{s}"; $"--{o.Long}" ]
               | None -> [ $"--{o.Long}" ]))

    let flags = flagCands |> List.map murexStr |> String.concat ", "

    let flagValues =
        node.Subcommands
        |> List.collect (fun c -> allSurfaceForms c |> List.map (fun f -> murexStr f + ": " + murexNode c))
        |> String.concat ", "

    let fvPart =
        if flagValues = "" then
            ""
        else
            ", \"FlagValues\": { " + flagValues + " }"

    let posPart =
        match node.Positionals with
        | pos :: _ ->
            match pos.Completion with
            | FilePath -> ", \"IncFiles\": true"
            | DirectoryPath -> ", \"IncDirs\": true"
            | DynamicCommand args -> ", \"Dynamic\": " + murexStr ("^" + dynamicCommandString args)
            | _ -> ""
        | [] -> ""

    "[{ \"Flags\": [" + flags + "]" + fvPart + posPart + " }]"

let private emitMurex (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    sb.AppendLine "# Generated by `fedit completions murex`." |> ignore
    sb.AppendLine("autocomplete set fedit %" + murexNode root) |> ignore
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Public emit + install
// ─────────────────────────────────────────────────────────────────────

let emit (shell: Shell) (root: CliCommandDescriptor) : string =
    match shell with
    | Zsh -> emitZsh root
    | Bash -> emitBash root
    | Fish -> emitFish root
    | Pwsh -> emitPwsh root
    | Nushell -> emitNushell root
    | Elvish -> emitElvish root
    | Xonsh -> emitXonsh root
    | Yash -> emitYash root
    | Murex -> emitMurex root

/// Write the script to the standard location for the shell.
/// Creates parent dirs as needed; overwrites any existing file.
let install (shell: Shell) (script: string) : Result<string, string> =
    try
        let path = installPath shell
        let dir = Path.GetDirectoryName path
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(path, script)
        Ok path
    with ex ->
        Result.Error ex.Message

// ─────────────────────────────────────────────────────────────────────
// CLI handler: `fedit completions <shell> [--install]`
// ─────────────────────────────────────────────────────────────────────

type private CompletionsOpt =
    | CompletionsHelp
    | CompletionsInstall

let private completionsApp: CliApp<CompletionsOpt> =
    { Name = "fedit completions"
      Summary = "Generate shell completion scripts"
      Positionals =
        [ { Name = "shell"
            Description = "Target shell: zsh, bash, fish, pwsh, nu, elvish, xonsh, yash, or murex"
            Completion = Choices [ "zsh"; "bash"; "fish"; "pwsh"; "nu"; "elvish"; "xonsh"; "yash"; "murex" ] } ]
      Options =
        [ { Short = Some 'h'
            Long = "help"
            Value = NoValue
            Description = "Show this help and exit"
            Option = CompletionsHelp
            Completion = NoHint }
          { Short = None
            Long = "install"
            Value = NoValue
            Description = "Write the script to the shell's standard location"
            Option = CompletionsInstall
            Completion = NoHint } ]
      Subcommands = [] }

/// Descriptor for the `completions` subcommand itself. Exported so
/// the top-level descriptor in `Program.fs` can nest it.
let descriptor: CliCommandDescriptor =
    { Name = "completions"
      Aliases = []
      HiddenAliases = []
      Summary = completionsApp.Summary
      Positionals = completionsApp.Positionals
      Options = completionsApp.Options |> List.map Parser.toOptionDescriptor
      Subcommands = [] }

let private wantsHelp items =
    items
    |> List.exists (function
        | Option(CompletionsHelp, _) -> true
        | _ -> false)

let private wantsInstall items =
    items
    |> List.exists (function
        | Option(CompletionsInstall, _) -> true
        | _ -> false)

let private firstPositional items =
    items
    |> List.tryPick (function
        | Argument s -> Some s
        | _ -> None)

/// `root` is the top-level fedit descriptor — assembled by the
/// caller because Program.fs is the only place that sees the full
/// tree (root + plugins + completions).
let run (root: CliCommandDescriptor) (argv: string[]) : int =
    match Parser.parse completionsApp.Options argv with
    | Result.Error errors ->
        System.Console.Error.WriteLine(Parser.formatErrors completionsApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        System.Console.Out.WriteLine(Parser.formatHelp completionsApp)
        0
    | Result.Ok items ->
        match firstPositional items with
        | None ->
            System.Console.Error.WriteLine "fedit completions: missing <shell> argument"
            System.Console.Error.WriteLine "Run 'fedit completions --help' for usage."
            2
        | Some shellArg ->
            match parseShell shellArg with
            | Result.Error e ->
                System.Console.Error.WriteLine("fedit completions: " + e)
                2
            | Ok shell ->
                let script = emit shell root

                if wantsInstall items then
                    match install shell script with
                    | Ok path ->
                        System.Console.Out.WriteLine("installed: " + path)

                        match shell with
                        | Zsh ->
                            System.Console.Out.WriteLine "  Add to ~/.zshrc before compinit:"
                            System.Console.Out.WriteLine "    fpath=(~/.zsh/completions $fpath)"
                        | Bash -> System.Console.Out.WriteLine "  Restart your shell or run: source ~/.bashrc"
                        | Fish -> System.Console.Out.WriteLine "  Picked up automatically on next shell session."
                        | Pwsh ->
                            System.Console.Out.WriteLine "  Dot-source it from your $PROFILE:"
                            System.Console.Out.WriteLine("    . " + path)
                        | Nushell ->
                            System.Console.Out.WriteLine "  Source it from your config.nu:"
                            System.Console.Out.WriteLine("    source " + path)
                        | Elvish ->
                            System.Console.Out.WriteLine "  Use it from your ~/.config/elvish/rc.elv:"
                            System.Console.Out.WriteLine "    use fedit-completions"
                        | Xonsh ->
                            System.Console.Out.WriteLine "  Source it from your ~/.config/xonsh/rc.xsh:"
                            System.Console.Out.WriteLine("    source " + path)
                        | Yash ->
                            System.Console.Out.WriteLine "  Ensure the dir is on $YASH_LOADPATH (add to ~/.yashrc):"

                            System.Console.Out.WriteLine
                                "    YASH_LOADPATH=~/.local/share/yash${YASH_LOADPATH:+:$YASH_LOADPATH}"
                        | Murex ->
                            System.Console.Out.WriteLine "  Source it from your ~/.murex_profile:"
                            System.Console.Out.WriteLine("    source " + path)

                        0
                    | Result.Error e ->
                        System.Console.Error.WriteLine("fedit completions: " + e)
                        1
                else
                    System.Console.Out.Write script
                    0
