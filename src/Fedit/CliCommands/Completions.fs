/// Shell-completion script generator. Walks a `CliCommandDescriptor`
/// tree (built from the real `CliApp<_>` definitions) and emits a
/// shell-native completion script. Each shell gets its own emitter
/// because the formats diverge sharply.
///
/// Depth limit: supports two levels of subcommands (root + one nested
/// subcommand layer), which is what fedit needs today. Going deeper
/// would mean recursively emitting per-subcommand functions and that
/// trade-off can wait until a third level actually shows up.
module Fedit.CliCommands.Completions

// FS3261: nullness warning on Path.GetDirectoryName (returns nullable).
// Matches the convention in Plugins.fs.
#nowarn "3261"

open System
open System.IO
open System.Text
open Fedit

type Shell =
    | Zsh
    | Bash
    | Fish

let parseShell (s: string) : Result<Shell, string> =
    match s.ToLowerInvariant() with
    | "zsh" -> Ok Zsh
    | "bash" -> Ok Bash
    | "fish" -> Ok Fish
    | other -> Result.Error $"unsupported shell '{other}' (supported: zsh, bash, fish)"

let shellName (shell: Shell) =
    match shell with
    | Zsh -> "zsh"
    | Bash -> "bash"
    | Fish -> "fish"

/// Standard install location per shell. `~/.zsh/completions/_fedit`
/// matches the fpath convention sema-lisp documents; bash uses XDG;
/// fish auto-discovers `~/.config/fish/completions/<cmd>.fish`.
let installPath (shell: Shell) : string =
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

    match shell with
    | Zsh -> Path.Combine(home, ".zsh", "completions", "_fedit")
    | Bash -> Path.Combine(home, ".local", "share", "bash-completion", "completions", "fedit")
    | Fish -> Path.Combine(home, ".config", "fish", "completions", "fedit.fish")

// ─────────────────────────────────────────────────────────────────────
// Shared helpers
// ─────────────────────────────────────────────────────────────────────

/// All surface forms (canonical + visible + hidden aliases) under
/// which a subcommand can appear. Completion scripts need to match
/// every form so `fedit plugin install` works the same as
/// `fedit plugins install`.
let private allSurfaceForms (sub: CliCommandDescriptor) =
    sub.Name :: sub.Aliases @ sub.HiddenAliases

/// The dynamic-completion invocation, encoded as a `fedit …` shell
/// command. We always invoke `fedit` from PATH — the user will only
/// see completions after installing the binary anyway, so PATH is
/// the right contract.
let private dynamicCommandString (args: string list) = "fedit " + String.Join(' ', args)

let private childByName (root: CliCommandDescriptor) (name: string) =
    root.Subcommands |> List.tryFind (fun c -> c.Name = name)

// ─────────────────────────────────────────────────────────────────────
// Zsh emitter
// ─────────────────────────────────────────────────────────────────────

let private zshEscape (s: string) =
    // zsh inside `'…'` only needs `'` → `'\''` and `[` `]` escaping
    // for `_values` descriptions. Keep it minimal.
    s.Replace("'", "'\\''").Replace("[", "\\[").Replace("]", "\\]")

let private zshOptionLine (opt: CliOptionDescriptor) =
    let desc = zshEscape opt.Description

    let valueSpec =
        match opt.Value with
        | NoValue -> ""
        | RequiredValue name ->
            let action =
                match opt.Completion with
                | FilePath -> "_files"
                | DirectoryPath -> "_files -/"
                | DynamicCommand args -> $"""($({dynamicCommandString args}))"""
                | Choices values -> $"""({String.Join(' ', values)})"""
                | NoHint -> " "

            $":{name}:{action}"

    let head =
        match opt.Short, opt.Long with
        | Some s, long -> $"'(-{s} --{long})'{{-{s},--{long}}}'[{desc}]{valueSpec}'"
        | None, long -> $"'--{long}[{desc}]{valueSpec}'"

    head

let private zshPositionalAction (pos: CliPositional) =
    match pos.Completion with
    | FilePath -> "_files"
    | DirectoryPath -> "_files -/"
    | DynamicCommand args ->
        let cmd = dynamicCommandString args
        $"""($({cmd}))"""
    | Choices values -> $"""({String.Join(' ', values)})"""
    | NoHint -> " "

let private zshSubcommandValues (subs: CliCommandDescriptor list) =
    subs
    |> List.collect (fun sub ->
        // Render the canonical name + every visible alias as
        // independent _values entries so each appears in the menu.
        (sub.Name :: sub.Aliases) |> List.map (fun name -> name, zshEscape sub.Summary))
    |> List.map (fun (name, desc) -> $"'{name}[{desc}]'")
    |> String.concat " \\\n        "

let private emitZshSubFunction (sb: StringBuilder) (sub: CliCommandDescriptor) =
    let funcName = $"_fedit__{sub.Name}"
    sb.AppendLine($"{funcName}() {{") |> ignore

    if List.isEmpty sub.Subcommands then
        // Leaf: just options + first positional (we don't model
        // multiple positionals on any current subcommand).
        sb.AppendLine("    _arguments -C \\") |> ignore

        for opt in sub.Options do
            sb.AppendLine($"        {zshOptionLine opt} \\") |> ignore

        match sub.Positionals with
        | [] -> sb.AppendLine("        && return 0") |> ignore
        | pos :: _ -> sb.AppendLine($"        '*:{pos.Name}:{zshPositionalAction pos}'") |> ignore
    else
        // Has nested subcommands — branch on the first positional word.
        sb.AppendLine("    local context state line") |> ignore
        sb.AppendLine("    _arguments -C \\") |> ignore

        for opt in sub.Options do
            sb.AppendLine($"        {zshOptionLine opt} \\") |> ignore

        sb.AppendLine("        '1: :->subcmd' \\") |> ignore
        sb.AppendLine("        '*::arg:->args' && return 0") |> ignore
        sb.AppendLine("") |> ignore
        sb.AppendLine("    case \"$state\" in") |> ignore
        sb.AppendLine("        subcmd)") |> ignore
        sb.AppendLine($"            _values 'subcommand' \\") |> ignore

        sb.AppendLine($"                {zshSubcommandValues sub.Subcommands}")
        |> ignore

        sb.AppendLine("            ;;") |> ignore
        sb.AppendLine("        args)") |> ignore
        sb.AppendLine("            case \"${words[1]}\" in") |> ignore

        for child in sub.Subcommands do
            let forms = allSurfaceForms child |> String.concat "|"

            sb.AppendLine($"                {forms})") |> ignore

            if List.isEmpty child.Options && List.isEmpty child.Positionals then
                sb.AppendLine("                    ;;") |> ignore
            else
                sb.AppendLine("                    _arguments \\") |> ignore

                for opt in child.Options do
                    sb.AppendLine($"                        {zshOptionLine opt} \\") |> ignore

                match child.Positionals with
                | [] -> sb.AppendLine("                        ;") |> ignore
                | pos :: _ ->
                    sb.AppendLine($"                        '*:{pos.Name}:{zshPositionalAction pos}'")
                    |> ignore

                sb.AppendLine("                    ;;") |> ignore

        sb.AppendLine("            esac") |> ignore
        sb.AppendLine("            ;;") |> ignore
        sb.AppendLine("    esac") |> ignore

    sb.AppendLine("}") |> ignore
    sb.AppendLine("") |> ignore

let private emitZsh (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()

    sb.AppendLine("#compdef fedit") |> ignore
    sb.AppendLine("# Generated by `fedit completions zsh`.") |> ignore
    sb.AppendLine("") |> ignore
    sb.AppendLine("_fedit() {") |> ignore
    sb.AppendLine("    local context state state_descr line") |> ignore
    sb.AppendLine("    typeset -A opt_args") |> ignore
    sb.AppendLine("") |> ignore
    sb.AppendLine("    _arguments -C \\") |> ignore

    for opt in root.Options do
        sb.AppendLine($"        {zshOptionLine opt} \\") |> ignore

    // Top-level always has subcommands AND optionally a positional.
    sb.AppendLine("        '1: :->cmd' \\") |> ignore
    sb.AppendLine("        '*::arg:->args' && return 0") |> ignore
    sb.AppendLine("") |> ignore
    sb.AppendLine("    case \"$state\" in") |> ignore
    sb.AppendLine("        cmd)") |> ignore
    sb.AppendLine($"            _values 'command' \\") |> ignore

    sb.AppendLine($"                {zshSubcommandValues root.Subcommands}")
    |> ignore

    sb.AppendLine("            ;;") |> ignore
    sb.AppendLine("        args)") |> ignore
    sb.AppendLine("            case \"${words[1]}\" in") |> ignore

    for sub in root.Subcommands do
        let forms = allSurfaceForms sub |> String.concat "|"
        sb.AppendLine($"                {forms}) _fedit__{sub.Name} ;;") |> ignore

    // Fallback: if it's none of the known subcommands, treat as
    // top-level positional (file path).
    match root.Positionals with
    | [] -> ()
    | pos :: _ -> sb.AppendLine($"                *) {zshPositionalAction pos} ;;") |> ignore

    sb.AppendLine("            esac") |> ignore
    sb.AppendLine("            ;;") |> ignore
    sb.AppendLine("    esac") |> ignore
    sb.AppendLine("}") |> ignore
    sb.AppendLine("") |> ignore

    for sub in root.Subcommands do
        emitZshSubFunction sb sub

    // No trailing `_fedit "$@"` — the script is meant to be
    // autoloaded from `$fpath`, where the `#compdef` directive
    // tells zsh which function to call.
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Bash emitter
// ─────────────────────────────────────────────────────────────────────

let private bashFlagList (opts: CliOptionDescriptor list) =
    opts
    |> List.collect (fun o ->
        match o.Short with
        | Some s -> [ $"-{s}"; $"--{o.Long}" ]
        | None -> [ $"--{o.Long}" ])
    |> String.concat " "

let private bashPositionalAction (pos: CliPositional) =
    match pos.Completion with
    | FilePath -> """COMPREPLY=( $(compgen -f -- "$cur") )"""
    | DirectoryPath -> """COMPREPLY=( $(compgen -d -- "$cur") )"""
    | DynamicCommand args ->
        let cmd = dynamicCommandString args
        $"""COMPREPLY=( $(compgen -W "$({cmd} 2>/dev/null)" -- "$cur") )"""
    | Choices values ->
        let list = String.Join(' ', values)
        $"""COMPREPLY=( $(compgen -W "{list}" -- "$cur") )"""
    | NoHint -> ":"

let private valueTakingLongs (opts: CliOptionDescriptor list) =
    opts
    |> List.choose (fun o ->
        match o.Value with
        | RequiredValue _ -> Some(o, $"--{o.Long}")
        | NoValue -> None)

let private emitBash (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()

    sb.AppendLine("# Generated by `fedit completions bash`.") |> ignore
    sb.AppendLine("_fedit() {") |> ignore
    sb.AppendLine("    local cur prev") |> ignore
    sb.AppendLine("    COMPREPLY=()") |> ignore
    sb.AppendLine("    cur=\"${COMP_WORDS[COMP_CWORD]}\"") |> ignore
    sb.AppendLine("    prev=\"${COMP_WORDS[COMP_CWORD-1]}\"") |> ignore
    sb.AppendLine("") |> ignore

    // Value-taking options at any depth: if prev was --foo, complete
    // its value. We list root + every subcommand's value-taking opts
    // here for simplicity.
    let allValueOpts =
        let rec walk (node: CliCommandDescriptor) =
            valueTakingLongs node.Options @ List.collect walk node.Subcommands

        walk root |> List.distinctBy snd

    for opt, flag in allValueOpts do
        sb.AppendLine($"    if [[ \"$prev\" == \"{flag}\" ]]; then") |> ignore

        let action =
            match opt.Completion with
            | FilePath -> """COMPREPLY=( $(compgen -f -- "$cur") )"""
            | DirectoryPath -> """COMPREPLY=( $(compgen -d -- "$cur") )"""
            | DynamicCommand args ->
                let cmd = dynamicCommandString args
                $"""COMPREPLY=( $(compgen -W "$({cmd} 2>/dev/null)" -- "$cur") )"""
            | Choices values ->
                let list = String.Join(' ', values)
                $"""COMPREPLY=( $(compgen -W "{list}" -- "$cur") )"""
            | NoHint -> ":"

        sb.AppendLine($"        {action}") |> ignore
        sb.AppendLine("        return") |> ignore
        sb.AppendLine("    fi") |> ignore

    sb.AppendLine("") |> ignore

    // Find the first non-flag word — that's the top-level subcommand.
    sb.AppendLine("    local cmd=\"\" i") |> ignore
    sb.AppendLine("    for ((i=1; i<COMP_CWORD; i++)); do") |> ignore
    sb.AppendLine("        case \"${COMP_WORDS[i]}\" in") |> ignore
    sb.AppendLine("            -*) ;;") |> ignore
    sb.AppendLine("            *) cmd=\"${COMP_WORDS[i]}\"; break ;;") |> ignore
    sb.AppendLine("        esac") |> ignore
    sb.AppendLine("    done") |> ignore
    sb.AppendLine("") |> ignore
    sb.AppendLine("    case \"$cmd\" in") |> ignore

    for sub in root.Subcommands do
        let forms = allSurfaceForms sub |> String.concat "|"
        sb.AppendLine($"        {forms})") |> ignore
        sb.AppendLine($"            _fedit__{sub.Name}") |> ignore
        sb.AppendLine("            return ;;") |> ignore

    // No subcommand picked yet: complete top-level flags + subcommand names + positional.
    let topNames =
        root.Subcommands
        |> List.collect (fun s -> s.Name :: s.Aliases)
        |> String.concat " "

    let topFlags = bashFlagList root.Options

    sb.AppendLine("        \"\")") |> ignore
    sb.AppendLine("            if [[ \"$cur\" == -* ]]; then") |> ignore

    sb.AppendLine($"                COMPREPLY=( $(compgen -W \"{topFlags}\" -- \"$cur\") )")
    |> ignore

    sb.AppendLine("            else") |> ignore

    sb.AppendLine($"                COMPREPLY=( $(compgen -W \"{topNames}\" -- \"$cur\") )")
    |> ignore

    match root.Positionals with
    | pos :: _ ->
        let action =
            match pos.Completion with
            | FilePath -> "COMPREPLY+=( $(compgen -f -- \"$cur\") )"
            | DirectoryPath -> "COMPREPLY+=( $(compgen -d -- \"$cur\") )"
            | _ -> ""

        if action <> "" then
            sb.AppendLine($"                {action}") |> ignore
    | [] -> ()

    sb.AppendLine("            fi") |> ignore
    sb.AppendLine("            return ;;") |> ignore
    sb.AppendLine("    esac") |> ignore
    sb.AppendLine("}") |> ignore
    sb.AppendLine("") |> ignore

    // One handler per top-level subcommand.
    for sub in root.Subcommands do
        sb.AppendLine($"_fedit__{sub.Name}() {{") |> ignore

        if List.isEmpty sub.Subcommands then
            // Leaf subcommand: complete its own flags + its
            // positional (if any).
            sb.AppendLine("    if [[ \"$cur\" == -* ]]; then") |> ignore
            let flags = bashFlagList sub.Options

            sb.AppendLine($"        COMPREPLY=( $(compgen -W \"{flags}\" -- \"$cur\") )")
            |> ignore

            sb.AppendLine("        return") |> ignore
            sb.AppendLine("    fi") |> ignore

            match sub.Positionals with
            | pos :: _ ->
                sb.AppendLine($"    {bashPositionalAction pos}") |> ignore
                sb.AppendLine("    return") |> ignore
            | [] -> ()
        else
            // Has sub-subcommands. Find the next non-flag word past
            // the matched parent.
            sb.AppendLine("    local sub=\"\" j") |> ignore
            sb.AppendLine("    local seen_parent=0") |> ignore
            sb.AppendLine("    for ((j=1; j<COMP_CWORD; j++)); do") |> ignore

            let parentForms =
                allSurfaceForms sub |> List.map (fun f -> $"\"{f}\"") |> String.concat "|"

            sb.AppendLine($"        case \"${{COMP_WORDS[j]}}\" in") |> ignore
            sb.AppendLine($"            {parentForms}) seen_parent=1 ;;") |> ignore
            sb.AppendLine("            -*) ;;") |> ignore
            sb.AppendLine("            *)") |> ignore
            sb.AppendLine("                if [[ $seen_parent -eq 1 ]]; then") |> ignore
            sb.AppendLine("                    sub=\"${COMP_WORDS[j]}\"; break") |> ignore
            sb.AppendLine("                fi ;;") |> ignore
            sb.AppendLine("        esac") |> ignore
            sb.AppendLine("    done") |> ignore
            sb.AppendLine("") |> ignore
            sb.AppendLine("    case \"$sub\" in") |> ignore

            for child in sub.Subcommands do
                let forms = allSurfaceForms child |> String.concat "|"
                sb.AppendLine($"        {forms})") |> ignore
                sb.AppendLine("            if [[ \"$cur\" == -* ]]; then") |> ignore
                let cflags = bashFlagList child.Options

                sb.AppendLine($"                COMPREPLY=( $(compgen -W \"{cflags}\" -- \"$cur\") )")
                |> ignore

                sb.AppendLine("            else") |> ignore

                match child.Positionals with
                | pos :: _ -> sb.AppendLine($"                {bashPositionalAction pos}") |> ignore
                | [] -> sb.AppendLine("                :") |> ignore

                sb.AppendLine("            fi") |> ignore
                sb.AppendLine("            return ;;") |> ignore

            // No sub-subcommand picked: complete child names + parent flags.
            let childNames =
                sub.Subcommands
                |> List.collect (fun c -> c.Name :: c.Aliases)
                |> String.concat " "

            let parentFlags = bashFlagList sub.Options
            sb.AppendLine("        \"\")") |> ignore
            sb.AppendLine("            if [[ \"$cur\" == -* ]]; then") |> ignore

            sb.AppendLine($"                COMPREPLY=( $(compgen -W \"{parentFlags}\" -- \"$cur\") )")
            |> ignore

            sb.AppendLine("            else") |> ignore

            sb.AppendLine($"                COMPREPLY=( $(compgen -W \"{childNames}\" -- \"$cur\") )")
            |> ignore

            sb.AppendLine("            fi") |> ignore
            sb.AppendLine("            return ;;") |> ignore
            sb.AppendLine("    esac") |> ignore

        sb.AppendLine("}") |> ignore
        sb.AppendLine("") |> ignore

    sb.AppendLine("complete -F _fedit fedit") |> ignore
    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Fish emitter
// ─────────────────────────────────────────────────────────────────────

let private fishEscape (s: string) =
    s.Replace("\\", "\\\\").Replace("'", "\\'")

let private emitFish (root: CliCommandDescriptor) : string =
    let sb = StringBuilder()

    sb.AppendLine("# Generated by `fedit completions fish`.") |> ignore
    sb.AppendLine("") |> ignore
    sb.AppendLine("# Disable default file completion at the root.") |> ignore
    sb.AppendLine("complete -c fedit -f") |> ignore
    sb.AppendLine("") |> ignore

    // Top-level options.
    for opt in root.Options do
        let parts = StringBuilder()
        parts.Append("complete -c fedit") |> ignore

        match opt.Short with
        | Some s -> parts.Append($" -s {s}") |> ignore
        | None -> ()

        parts.Append($" -l {opt.Long}") |> ignore

        match opt.Value with
        | RequiredValue _ ->
            parts.Append(" -r") |> ignore

            match opt.Completion with
            | FilePath -> parts.Append(" -F") |> ignore
            | _ -> ()
        | NoValue -> ()

        parts.Append($" -d '{fishEscape opt.Description}'") |> ignore
        sb.AppendLine(parts.ToString()) |> ignore

    sb.AppendLine("") |> ignore

    // Top-level subcommands (only when no subcommand seen yet).
    for sub in root.Subcommands do
        for form in allSurfaceForms sub do
            let visible = not (List.contains form sub.HiddenAliases)

            if visible then
                sb.AppendLine($"complete -c fedit -n __fish_use_subcommand -a {form} -d '{fishEscape sub.Summary}'")
                |> ignore

    // Top-level positional (file completion) when no subcommand seen.
    match root.Positionals with
    | pos :: _ when pos.Completion = FilePath ->
        sb.AppendLine("complete -c fedit -n __fish_use_subcommand -F") |> ignore
    | _ -> ()

    sb.AppendLine("") |> ignore

    for sub in root.Subcommands do
        let parentMatch =
            let forms = allSurfaceForms sub |> String.concat " "
            $"__fish_seen_subcommand_from {forms}"

        // Parent options.
        for opt in sub.Options do
            let parts = StringBuilder()
            parts.Append($"complete -c fedit -n '{parentMatch}'") |> ignore

            match opt.Short with
            | Some s -> parts.Append($" -s {s}") |> ignore
            | None -> ()

            parts.Append($" -l {opt.Long}") |> ignore

            match opt.Value with
            | RequiredValue _ ->
                parts.Append(" -r") |> ignore

                match opt.Completion with
                | FilePath -> parts.Append(" -F") |> ignore
                | _ -> ()
            | NoValue -> ()

            parts.Append($" -d '{fishEscape opt.Description}'") |> ignore
            sb.AppendLine(parts.ToString()) |> ignore

        if List.isEmpty sub.Subcommands then
            // Leaf subcommand: complete its positional (if any).
            match sub.Positionals with
            | [ pos ] ->
                let extra =
                    match pos.Completion with
                    | FilePath -> Some " -F"
                    | DirectoryPath -> Some " -a '(__fish_complete_directories)'"
                    | DynamicCommand args ->
                        let cmd = dynamicCommandString args
                        Some $" -a '({cmd} 2>/dev/null)'"
                    | Choices values -> Some $" -a '{String.Join(' ', values)}'"
                    | NoHint -> None

                match extra with
                | Some e -> sb.AppendLine($"complete -c fedit -n '{parentMatch}'{e}") |> ignore
                | None -> ()
            | _ -> ()
        else
            // Nested subcommands: gate them on "seen parent, not yet seen any child".
            let childForms =
                sub.Subcommands |> List.collect allSurfaceForms |> String.concat " "

            let childGate = $"{parentMatch}; and not __fish_seen_subcommand_from {childForms}"

            for child in sub.Subcommands do
                for form in allSurfaceForms child do
                    let visible = not (List.contains form child.HiddenAliases)

                    if visible then
                        sb.AppendLine($"complete -c fedit -n '{childGate}' -a {form} -d '{fishEscape child.Summary}'")
                        |> ignore

                // Child's own options + positional, gated on "seen this specific child".
                let childForms' = allSurfaceForms child |> String.concat " "

                let childMatch = $"{parentMatch}; and __fish_seen_subcommand_from {childForms'}"

                for opt in child.Options do
                    let parts = StringBuilder()
                    parts.Append($"complete -c fedit -n '{childMatch}'") |> ignore

                    match opt.Short with
                    | Some s -> parts.Append($" -s {s}") |> ignore
                    | None -> ()

                    parts.Append($" -l {opt.Long}") |> ignore

                    match opt.Value with
                    | RequiredValue _ ->
                        parts.Append(" -r") |> ignore

                        match opt.Completion with
                        | FilePath -> parts.Append(" -F") |> ignore
                        | _ -> ()
                    | NoValue -> ()

                    parts.Append($" -d '{fishEscape opt.Description}'") |> ignore
                    sb.AppendLine(parts.ToString()) |> ignore

                match child.Positionals with
                | [ pos ] ->
                    match pos.Completion with
                    | FilePath -> sb.AppendLine($"complete -c fedit -n '{childMatch}' -F") |> ignore
                    | DirectoryPath ->
                        sb.AppendLine($"complete -c fedit -n '{childMatch}' -a '(__fish_complete_directories)'")
                        |> ignore
                    | DynamicCommand args ->
                        let cmd = dynamicCommandString args

                        sb.AppendLine($"complete -c fedit -n '{childMatch}' -a '({cmd} 2>/dev/null)'")
                        |> ignore
                    | Choices values ->
                        sb.AppendLine($"complete -c fedit -n '{childMatch}' -a '{String.Join(' ', values)}'")
                        |> ignore
                    | NoHint -> ()
                | _ -> ()

        sb.AppendLine("") |> ignore

    sb.ToString()

// ─────────────────────────────────────────────────────────────────────
// Public emit + install
// ─────────────────────────────────────────────────────────────────────

let emit (shell: Shell) (root: CliCommandDescriptor) : string =
    match shell with
    | Zsh -> emitZsh root
    | Bash -> emitBash root
    | Fish -> emitFish root

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
            Description = "Target shell: zsh, bash, or fish"
            Completion = Choices [ "zsh"; "bash"; "fish" ] } ]
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
      Options = completionsApp.Options |> List.map Cli.toOptionDescriptor
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
    match Cli.parse completionsApp.Options argv with
    | Result.Error errors ->
        eprintfn "%s" (Cli.formatErrors completionsApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        printfn "%s" (Cli.formatHelp completionsApp)
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

                        0
                    | Result.Error e ->
                        eprintfn "fedit completions: %s" e
                        1
                else
                    printf "%s" script
                    0
