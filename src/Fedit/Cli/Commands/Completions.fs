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

let shellName (shell: Shell) =
    match shell with
    | Zsh -> "zsh"
    | Bash -> "bash"
    | Fish -> "fish"
    | Pwsh -> "pwsh"
    | Nushell -> "nu"
    | Elvish -> "elvish"
    | Xonsh -> "xonsh"
    | Yash -> "yash"
    | Murex -> "murex"

/// Standard install location per shell. `~/.zsh/completions/_fedit`
/// matches the fpath convention; bash uses XDG; fish auto-discovers
/// `~/.config/fish/completions/<cmd>.fish`. PowerShell, Nushell, Elvish
/// and xonsh have no drop-in autoload dir — the file is sourced/used
/// from the shell's rc (see `run`).
let installPath (shell: Shell) : string =
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

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
    |> List.collect (fun s -> visibleForms s |> List.map (fun n -> sprintf "'%s[%s]'" n (zshEscape s.Summary)))
    |> String.concat " "

let rec private emitZshNode (sb: StringBuilder) (funcName: string) (node: CliCommandDescriptor) =
    let add (s: string) = sb.AppendLine s |> ignore
    add (sprintf "%s() {" funcName)

    if List.isEmpty node.Subcommands then
        // Leaf: options + first positional (no current node has more
        // than one positional).
        add "    _arguments -C \\"

        for opt in node.Options do
            add (sprintf "        %s \\" (zshOptionLine opt))

        match node.Positionals with
        | [] -> add "        && return 0"
        | pos :: _ -> add (sprintf "        '*:%s:%s'" pos.Name (zshAction pos.Completion))
    else
        // Branch on the first remaining word, then dispatch to the
        // matching child function (recursively defined below).
        add "    local context state line"
        add "    _arguments -C \\"

        for opt in node.Options do
            add (sprintf "        %s \\" (zshOptionLine opt))

        add "        '1: :->cmd' \\"
        add "        '*::arg:->args' && return 0"
        add ""
        add "    case \"$state\" in"
        add "        cmd)"
        add (sprintf "            _values 'command' %s" (zshSubcommandValues node.Subcommands))
        add "            ;;"
        add "        args)"
        add "            case \"${words[1]}\" in"

        for child in node.Subcommands do
            let forms = allSurfaceForms child |> String.concat "|"
            add (sprintf "                %s) %s__%s ;;" forms funcName child.Name)

        match node.Positionals with
        | pos :: _ -> add (sprintf "                *) %s ;;" (zshAction pos.Completion))
        | [] -> ()

        add "            esac"
        add "            ;;"
        add "    esac"

    add "}"
    add ""

    for child in node.Subcommands do
        emitZshNode sb (sprintf "%s__%s" funcName child.Name) child

let private emitZsh (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()
    sb.AppendLine "#compdef fedit" |> ignore
    sb.AppendLine "# Generated by `fedit completions zsh`." |> ignore
    sb.AppendLine "" |> ignore
    // The `#compdef` directive tells zsh which function to call, so the
    // script is autoloaded from `$fpath` without a trailing `_fedit "$@"`.
    emitZshNode sb "_fedit" root
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Bash emitter — flat transition table + one case arm per node.
// ─────────────────────────────────────────────────────────────────────

let private bashAssign (op: string) (kind: CliCompletionKind) =
    match kind with
    | FilePath -> sprintf """COMPREPLY%s( $(compgen -f -- "$cur") )""" op
    | DirectoryPath -> sprintf """COMPREPLY%s( $(compgen -d -- "$cur") )""" op
    | DynamicCommand args ->
        sprintf """COMPREPLY%s( $(compgen -W "$(%s 2>/dev/null)" -- "$cur") )""" op (dynamicCommandString args)
    | Choices values -> sprintf """COMPREPLY%s( $(compgen -W "%s" -- "$cur") )""" op (String.Join(' ', values))
    | NoHint -> ":"

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
        add (sprintf "    if [[ \"$prev\" == \"%s\" ]]; then" flag)
        add (sprintf "        %s" (bashAssign "=" opt.Completion))
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
        add (sprintf "        case \"$p\" in %s) continue ;; esac" flags)

    add "        case \"$path|$w\" in"

    for path, node in nodes do
        for child in node.Subcommands do
            let arms =
                allSurfaceForms child
                |> List.map (fun f -> sprintf "\"%s|%s\"" (pathKey path) f)
                |> String.concat "|"

            add (sprintf "            %s) path=\"%s\" ;;" arms (pathKey (path @ [ child.Name ])))

    add "            *) break ;;"
    add "        esac"
    add "    done"
    add ""
    add "    case \"$path\" in"

    for path, node in nodes do
        add (sprintf "        \"%s\")" (pathKey path))
        add "            if [[ \"$cur\" == -* ]]; then"
        add (sprintf "                COMPREPLY=( $(compgen -W \"%s\" -- \"$cur\") )" (bashFlagList node.Options))
        add "            else"

        let names = node.Subcommands |> List.collect visibleForms |> String.concat " "

        if names <> "" then
            add (sprintf "                COMPREPLY=( $(compgen -W \"%s\" -- \"$cur\") )" names)

        match node.Positionals with
        | pos :: _ ->
            // Append to the subcommand list when both are valid (e.g. the
            // root takes a file path or a subcommand); otherwise assign.
            let op = if names <> "" then "+=" else "="
            let action = bashAssign op pos.Completion

            if not (names <> "" && action = ":") then
                add (sprintf "                %s" action)
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
        | _ -> ()
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
                add (sprintf "        '%s' = '%s'" (pathKey path + "|" + form) (pathKey (path @ [ child.Name ])))

    add "    }"

    add (sprintf "    $valueFlags = @(%s)" (values |> List.map (sprintf "'%s'") |> String.concat ", "))
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
        let key =
            if List.isEmpty path then
                "''"
            else
                sprintf "'%s'" (pathKey path)

        add (sprintf "        %s {" key)

        for child in node.Subcommands do
            for form in visibleForms child do
                add (
                    sprintf
                        "            $static.Add([CompletionResult]::new('%s', '%s', [CompletionResultType]::ParameterValue, '%s'))"
                        form
                        form
                        (pwshEscape child.Summary)
                )

        for opt in node.Options do
            match opt.Short with
            | Some s ->
                add (
                    sprintf
                        "            $static.Add([CompletionResult]::new('-%c', '-%c', [CompletionResultType]::ParameterName, '%s'))"
                        s
                        s
                        (pwshEscape opt.Description)
                )
            | None -> ()

            add (
                sprintf
                    "            $static.Add([CompletionResult]::new('--%s', '--%s', [CompletionResultType]::ParameterName, '%s'))"
                    opt.Long
                    opt.Long
                    (pwshEscape opt.Description)
            )

        match node.Positionals with
        | pos :: _ ->
            match pos.Completion with
            | Choices vals ->
                for v in vals do
                    add (
                        sprintf
                            "            $static.Add([CompletionResult]::new('%s', '%s', [CompletionResultType]::ParameterValue, '%s'))"
                            v
                            v
                            v
                    )
            | FilePath
            | DirectoryPath ->
                add
                    "            $extra.AddRange([System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete))"
            | DynamicCommand args ->
                add (sprintf "            foreach ($n in @(& %s 2>$null)) {" (dynamicCommandString args))

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

    sprintf "nu-complete fedit%s %s" p posName

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
            | Choices vals -> add (sprintf "def \"%s\" [] { [%s] }" slug (String.Join(' ', vals)))
            | DynamicCommand args -> add (sprintf "def \"%s\" [] { ^%s | lines }" slug (dynamicCommandString args))
            | _ -> ()
        | [] -> ()

    add ""

    // One `export extern` per surface path (canonical + aliases) so the
    // singular `plugin` alias completes like `plugins`.
    let rec externs (prefixes: string list) (canonPath: string list) (node: CliCommandDescriptor) =
        for p in prefixes do
            add (sprintf "export extern \"%s\" [" p)

            match node.Positionals with
            | pos :: _ ->
                let shape =
                    match pos.Completion with
                    | Choices _
                    | DynamicCommand _ -> sprintf "string@\"%s\"" (nuCompleterSlug canonPath pos.Name)
                    | FilePath
                    | DirectoryPath -> "path"
                    | NoHint -> "string"

                add (sprintf "  %s?: %s  # %s" pos.Name shape pos.Description)
            | [] -> ()

            for opt in node.Options do
                let namePart =
                    match opt.Short with
                    | Some s -> sprintf "--%s (-%c)" opt.Long s
                    | None -> sprintf "--%s" opt.Long

                let valuePart =
                    match opt.Value with
                    | RequiredValue _ -> ": " + nuShape opt.Completion
                    | NoValue -> ""

                add (sprintf "  %s%s  # %s" namePart valuePart opt.Description)

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
                add (sprintf "  &'%s'='%s'" (pathKey path + "|" + form) (pathKey (path @ [ child.Name ])))

    add "]"
    add (sprintf "var fedit-value-flags = [%s]" (String.Join(' ', values)))
    add ""

    // Flags offered when the current word starts with `-`, keyed by path.
    add "var fedit-flags = ["

    for path, node in nodes do
        add (sprintf "  &'%s'=[%s]" (pathKey path) (bashFlagList node.Options))

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
            add (sprintf "  &'%s'={|cur|" (pathKey path))

            let subForms = node.Subcommands |> List.collect visibleForms |> String.concat " "

            if subForms <> "" then
                add (sprintf "    put %s" subForms)

            match node.Positionals with
            | pos :: _ ->
                match pos.Completion with
                | FilePath
                | DirectoryPath -> add "    edit:complete-filename $cur"
                | Choices vals -> add (sprintf "    put %s" (String.Join(' ', vals)))
                | DynamicCommand args ->
                    add (
                        sprintf
                            "    for l [(str:split \"\\n\" (%s 2>/dev/null | slurp))] { if (not-eq $l '') { put $l } }"
                            (dynamicCommandString args)
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
                    sprintf "    %s: %s," (pyStr (pathKey path + "|" + form)) (pyStr (pathKey (path @ [ child.Name ])))
                )

    add "}"
    add (sprintf "_FEDIT_VALUE_FLAGS = {%s}" (values |> List.map pyStr |> String.concat ", "))
    add ""

    // Per-path subcommand and flag tables.
    add "_FEDIT_SUBS = {"

    for path, node in nodes do
        let entries =
            node.Subcommands
            |> List.collect (fun c ->
                visibleForms c
                |> List.map (fun f -> sprintf "(%s, %s)" (pyStr f) (pyStr c.Summary)))
            |> String.concat ", "

        add (sprintf "    %s: [%s]," (pyStr (pathKey path)) entries)

    add "}"
    add "_FEDIT_FLAGS = {"

    for path, node in nodes do
        let entries =
            node.Options
            |> List.collect (fun o ->
                let pair n =
                    sprintf "(%s, %s)" (pyStr n) (pyStr o.Description)

                match o.Short with
                | Some s -> [ pair (sprintf "-%c" s); pair (sprintf "--%s" o.Long) ]
                | None -> [ pair (sprintf "--%s" o.Long) ])
            |> String.concat ", "

        add (sprintf "    %s: [%s]," (pyStr (pathKey path)) entries)

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
            | Some k -> add (sprintf "    %s: %s," (pyStr (pathKey path)) (pyStr k))
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
    add "    if out:"
    add "        return out"
    add "    kind = _FEDIT_POS.get(path)"
    add "    if kind == 'file':"
    add "        return None"
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
        let pat = values |> List.map (sprintf "'%s'") |> String.concat "|"
        add (sprintf "        case $prev in (%s) prev=$w; i=$((i+1)); continue;; esac" pat)

    add "        case \"$path|$w\" in"

    for path, node in nodes do
        for child in node.Subcommands do
            let arms =
                allSurfaceForms child
                |> List.map (fun f -> sprintf "'%s|%s'" (pathKey path) f)
                |> String.concat "|"

            add (sprintf "            (%s) path='%s';;" arms (pathKey (path @ [ child.Name ])))

    add "            (*) break;;"
    add "        esac"
    add "        prev=$w; i=$((i+1))"
    add "    done"
    add "    case $path in"

    for path, node in nodes do
        add (sprintf "        ('%s')" (pathKey path))
        add "            case $TARGETWORD in"
        add (sprintf "                (-*) complete -- %s;;" (bashFlagList node.Options))

        let subs = node.Subcommands |> List.collect visibleForms |> String.concat " "

        let posCmd =
            match node.Positionals with
            | pos :: _ ->
                match pos.Completion with
                | FilePath -> "complete -f"
                | DirectoryPath -> "complete -d"
                | Choices vals -> "complete -- " + String.Join(' ', vals)
                | DynamicCommand args -> sprintf "complete -- $(%s 2>/dev/null)" (dynamicCommandString args)
                | NoHint -> ":"
            | [] -> ":"

        let elseCmd =
            match subs, posCmd with
            | "", c -> c
            | s, ":" -> "complete -- " + s
            | s, c -> "complete -- " + s + "; " + c

        add (sprintf "                (*) %s;;" elseCmd)
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
        |> List.collect (fun c ->
            allSurfaceForms c
            |> List.map (fun f -> sprintf "%s: %s" (murexStr f) (murexNode c)))
        |> String.concat ", "

    let fvPart =
        if flagValues = "" then
            ""
        else
            sprintf ", \"FlagValues\": { %s }" flagValues

    let posPart =
        match node.Positionals with
        | pos :: _ ->
            match pos.Completion with
            | FilePath
            | DirectoryPath -> ", \"IncFiles\": true"
            | DynamicCommand args -> sprintf ", \"Dynamic\": %s" (murexStr ("^" + dynamicCommandString args))
            | _ -> ""
        | [] -> ""

    sprintf "[{ \"Flags\": [%s]%s%s }]" flags fvPart posPart

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
        eprintfn "%s" (Parser.formatErrors completionsApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        printfn "%s" (Parser.formatHelp completionsApp)
        0
    | Result.Ok items ->
        match firstPositional items with
        | None ->
            eprintfn "fedit completions: missing <shell> argument"
            eprintfn "Run 'fedit completions --help' for usage."
            2
        | Some shellArg ->
            match parseShell shellArg with
            | Result.Error e ->
                eprintfn "fedit completions: %s" e
                2
            | Ok shell ->
                let script = emit shell root

                if wantsInstall items then
                    match install shell script with
                    | Ok path ->
                        printfn "installed: %s" path

                        match shell with
                        | Zsh ->
                            printfn "  Add to ~/.zshrc before compinit:"
                            printfn "    fpath=(~/.zsh/completions $fpath)"
                        | Bash -> printfn "  Restart your shell or run: source ~/.bashrc"
                        | Fish -> printfn "  Picked up automatically on next shell session."
                        | Pwsh ->
                            printfn "  Dot-source it from your $PROFILE:"
                            printfn "    . %s" path
                        | Nushell ->
                            printfn "  Source it from your config.nu:"
                            printfn "    source %s" path
                        | Elvish ->
                            printfn "  Use it from your ~/.config/elvish/rc.elv:"
                            printfn "    use fedit-completions"
                        | Xonsh ->
                            printfn "  Source it from your ~/.config/xonsh/rc.xsh:"
                            printfn "    source %s" path
                        | Yash ->
                            printfn "  Ensure the dir is on $YASH_LOADPATH (add to ~/.yashrc):"
                            printfn "    YASH_LOADPATH=~/.local/share/yash${YASH_LOADPATH:+:$YASH_LOADPATH}"
                        | Murex ->
                            printfn "  Source it from your ~/.murex_profile:"
                            printfn "    source %s" path

                        0
                    | Result.Error e ->
                        eprintfn "fedit completions: %s" e
                        1
                else
                    printf "%s" script
                    0
