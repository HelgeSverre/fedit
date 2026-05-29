module Fedit.Tests.CompletionsTests

open Fedit.Cli
open Fedit.Cli.Commands
open Xunit
open FsUnit.Xunit

// A small synthetic tree exercising every CliCompletionKind, so emitter
// regressions surface as a single failed assertion regardless of which
// real subcommand changes around it.
let private leaf (name: string) (pos: CliPositional list) : CliCommandDescriptor =
    { Name = name
      Aliases = []
      HiddenAliases = []
      Summary = $"the {name} leaf"
      Positionals = pos
      Options =
        [ { Short = Some 'h'
            Long = "help"
            Value = NoValue
            Description = "help"
            Completion = NoHint } ]
      Subcommands = [] }

let private root: CliCommandDescriptor =
    { Name = "fedit"
      Aliases = []
      HiddenAliases = []
      Summary = "test tree"
      Positionals =
        [ { Name = "path"
            Description = "ws"
            Completion = FilePath } ]
      Options =
        [ { Short = Some 'V'
            Long = "version"
            Value = NoValue
            Description = "version"
            Completion = NoHint }
          { Short = None
            Long = "log"
            Value = RequiredValue "path"
            Description = "logfile"
            Completion = FilePath } ]
      Subcommands =
        [ { Name = "plugins"
            Aliases = []
            HiddenAliases = [ "plugin" ]
            Summary = "manage"
            Positionals = []
            Options = []
            Subcommands =
              [ leaf
                    "install"
                    [ { Name = "source"
                        Description = "src"
                        Completion = FilePath } ]
                leaf
                    "remove"
                    [ { Name = "name"
                        Description = "n"
                        Completion = DynamicCommand [ "plugins"; "list"; "--names" ] } ]
                leaf
                    "validate"
                    [ { Name = "p"
                        Description = "p"
                        Completion = DirectoryPath } ] ] }
          { Name = "completions"
            Aliases = []
            HiddenAliases = []
            Summary = "shell"
            Positionals =
              [ { Name = "shell"
                  Description = "s"
                  Completion = Choices [ "zsh"; "bash"; "fish" ] } ]
            Options = []
            Subcommands = [] } ] }

// ─────────────────────────────────────────────────────────────────────
// parseShell
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``parseShell accepts zsh bash fish (case-insensitive)`` () =
    // Pattern-match because FsUnit's `should equal` on Result<DU, _> can
    // mis-compare even structurally-equal values.
    match Completions.parseShell "zsh" with
    | Ok Completions.Zsh -> ()
    | other -> failwithf "zsh: %A" other

    match Completions.parseShell "BASH" with
    | Ok Completions.Bash -> ()
    | other -> failwithf "BASH: %A" other

    match Completions.parseShell "Fish" with
    | Ok Completions.Fish -> ()
    | other -> failwithf "Fish: %A" other

    match Completions.parseShell "pwsh" with
    | Ok Completions.Pwsh -> ()
    | other -> failwithf "pwsh: %A" other

    match Completions.parseShell "PowerShell" with
    | Ok Completions.Pwsh -> ()
    | other -> failwithf "PowerShell: %A" other

    match Completions.parseShell "nu" with
    | Ok Completions.Nushell -> ()
    | other -> failwithf "nu: %A" other

    match Completions.parseShell "elvish" with
    | Ok Completions.Elvish -> ()
    | other -> failwithf "elvish: %A" other

    match Completions.parseShell "xonsh" with
    | Ok Completions.Xonsh -> ()
    | other -> failwithf "xonsh: %A" other

    match Completions.parseShell "yash" with
    | Ok Completions.Yash -> ()
    | other -> failwithf "yash: %A" other

    match Completions.parseShell "murex" with
    | Ok Completions.Murex -> ()
    | other -> failwithf "murex: %A" other

[<Fact>]
let ``parseShell rejects unknown shell with a message`` () =
    match Completions.parseShell "tcsh" with
    | Result.Error msg -> msg |> should haveSubstring "tcsh"
    | Ok _ -> failwith "expected Error"

// ─────────────────────────────────────────────────────────────────────
// Zsh
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``zsh script starts with #compdef and defines _fedit`` () =
    let s = Completions.emit Completions.Zsh root
    s |> should haveSubstring "#compdef fedit"
    s |> should haveSubstring "_fedit() {"

[<Fact>]
let ``zsh emits one sub-function per top-level subcommand`` () =
    let s = Completions.emit Completions.Zsh root
    s |> should haveSubstring "_fedit__plugins() {"
    s |> should haveSubstring "_fedit__completions() {"

[<Fact>]
let ``zsh routes hidden aliases through the same dispatch`` () =
    // `plugin` must trigger _fedit__plugins so the singular form works.
    let s = Completions.emit Completions.Zsh root
    s |> should haveSubstring "plugins|plugin) _fedit__plugins"

[<Fact>]
let ``zsh uses command substitution not arithmetic for DynamicCommand`` () =
    // Regression guard for the $((...)) → $(...) fix. Double-paren in
    // zsh is arithmetic and silently produces 0 candidates.
    let s = Completions.emit Completions.Zsh root
    s |> should haveSubstring "$(fedit plugins list --names)"
    s |> should not' (haveSubstring "$((fedit")

[<Fact>]
let ``zsh emits literal choice list for Choices kind`` () =
    let s = Completions.emit Completions.Zsh root
    s |> should haveSubstring "(zsh bash fish)"

[<Fact>]
let ``zsh emits _files -/ for directory positionals`` () =
    let s = Completions.emit Completions.Zsh root
    s |> should haveSubstring "_files -/"

[<Fact>]
let ``zsh does not call _fedit at end (fpath-autoload contract)`` () =
    // Calling the function at the bottom breaks `source`ing and is
    // not how fpath-installed completions work.
    let s = Completions.emit Completions.Zsh root
    let trimmed = s.TrimEnd('\n', '\r')
    trimmed.EndsWith("_fedit \"$@\"") |> should equal false

// ─────────────────────────────────────────────────────────────────────
// Bash
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``bash script defines _fedit and registers via complete -F`` () =
    let s = Completions.emit Completions.Bash root
    s |> should haveSubstring "_fedit() {"
    s |> should haveSubstring "complete -F _fedit fedit"

[<Fact>]
let ``bash handles --log value completion`` () =
    let s = Completions.emit Completions.Bash root
    s |> should haveSubstring """if [[ "$prev" == "--log" ]]; then"""

[<Fact>]
let ``bash uses compgen with command substitution for DynamicCommand`` () =
    let s = Completions.emit Completions.Bash root

    let expected =
        "compgen -W \"$(fedit plugins list --names 2>/dev/null)\" -- \"$cur\""

    s |> should haveSubstring expected

[<Fact>]
let ``bash emits literal choice list for Choices kind`` () =
    let s = Completions.emit Completions.Bash root
    let expected = "compgen -W \"zsh bash fish\" -- \"$cur\""
    s |> should haveSubstring expected

[<Fact>]
let ``bash normalizes hidden aliases to the canonical path`` () =
    // The transition table maps the singular `plugin` to the canonical
    // `plugins` path so deeper dispatch is alias-agnostic.
    let s = Completions.emit Completions.Bash root
    s |> should haveSubstring "\"|plugin\") path=\"plugins\""

// ─────────────────────────────────────────────────────────────────────
// Fish
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``fish disables default file completion at the root`` () =
    let s = Completions.emit Completions.Fish root
    s |> should haveSubstring "complete -c fedit -f"

[<Fact>]
let ``fish lists top-level subcommands via __fish_use_subcommand`` () =
    let s = Completions.emit Completions.Fish root
    s |> should haveSubstring "-n __fish_use_subcommand -a plugins"
    s |> should haveSubstring "-n __fish_use_subcommand -a completions"

[<Fact>]
let ``fish omits hidden aliases from completion menus`` () =
    let s = Completions.emit Completions.Fish root
    // The singular `plugin` is hidden — it must not appear as a
    // top-level `-a plugin` entry.
    s |> should not' (haveSubstring "-a plugin -d")

[<Fact>]
let ``fish uses command substitution for DynamicCommand`` () =
    let s = Completions.emit Completions.Fish root
    s |> should haveSubstring "(fedit plugins list --names 2>/dev/null)"

[<Fact>]
let ``fish emits choice list for Choices kind`` () =
    let s = Completions.emit Completions.Fish root
    s |> should haveSubstring "'zsh bash fish'"

[<Fact>]
let ``fish uses __fish_complete_directories for DirectoryPath`` () =
    let s = Completions.emit Completions.Fish root
    s |> should haveSubstring "(__fish_complete_directories)"

// ─────────────────────────────────────────────────────────────────────
// PowerShell
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``pwsh registers a native argument completer`` () =
    let s = Completions.emit Completions.Pwsh root

    s
    |> should haveSubstring "Register-ArgumentCompleter -Native -CommandName fedit"

[<Fact>]
let ``pwsh transition table normalizes hidden aliases`` () =
    let s = Completions.emit Completions.Pwsh root
    s |> should haveSubstring "'|plugins' = 'plugins'"
    s |> should haveSubstring "'|plugin' = 'plugins'"

[<Fact>]
let ``pwsh invokes the binary for DynamicCommand`` () =
    let s = Completions.emit Completions.Pwsh root
    s |> should haveSubstring "& fedit plugins list --names 2>$null"

[<Fact>]
let ``pwsh emits CompletionResult entries for Choices kind`` () =
    let s = Completions.emit Completions.Pwsh root
    s |> should haveSubstring "[CompletionResult]::new('zsh', 'zsh'"

[<Fact>]
let ``pwsh defers to filename completion for FilePath`` () =
    let s = Completions.emit Completions.Pwsh root
    s |> should haveSubstring "CompleteFilename($wordToComplete)"

// ─────────────────────────────────────────────────────────────────────
// Nushell
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``nu emits an extern per command including the root`` () =
    let s = Completions.emit Completions.Nushell root
    s |> should haveSubstring "export extern \"fedit\" ["
    s |> should haveSubstring "export extern \"fedit completions\" ["
    s |> should haveSubstring "export extern \"fedit plugins install\" ["

[<Fact>]
let ``nu emits aliased externs for hidden aliases`` () =
    // The singular `plugin` gets its own extern tree so it completes too.
    let s = Completions.emit Completions.Nushell root
    s |> should haveSubstring "export extern \"fedit plugin install\" ["

[<Fact>]
let ``nu defines a lines-based completer for DynamicCommand`` () =
    let s = Completions.emit Completions.Nushell root
    s |> should haveSubstring "^fedit plugins list --names | lines"

[<Fact>]
let ``nu defines a list completer for Choices kind`` () =
    let s = Completions.emit Completions.Nushell root
    s |> should haveSubstring "[zsh bash fish]"

// ─────────────────────────────────────────────────────────────────────
// Elvish
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``elvish registers an arg-completer for fedit`` () =
    let s = Completions.emit Completions.Elvish root
    s |> should haveSubstring "set edit:completion:arg-completer[fedit] = {|@args|"

[<Fact>]
let ``elvish transition map normalizes hidden aliases`` () =
    let s = Completions.emit Completions.Elvish root
    s |> should haveSubstring "&'|plugin'='plugins'"

[<Fact>]
let ``elvish expands flag lists via all (not map explosion)`` () =
    // Regression guard: `$@map[key]` explodes the whole map; `all` indexes first.
    let s = Completions.emit Completions.Elvish root
    s |> should haveSubstring "all $fedit-flags[$path]"

[<Fact>]
let ``elvish slurps and splits DynamicCommand output`` () =
    let s = Completions.emit Completions.Elvish root

    s
    |> should haveSubstring "str:split \"\\n\" (fedit plugins list --names 2>/dev/null | slurp)"

// ─────────────────────────────────────────────────────────────────────
// Xonsh
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``xonsh registers a contextual completer for fedit`` () =
    let s = Completions.emit Completions.Xonsh root
    s |> should haveSubstring "@contextual_command_completer_for('fedit')"

    s
    |> should haveSubstring "add_one_completer('fedit', _fedit_completer, 'start')"

[<Fact>]
let ``xonsh transition table normalizes hidden aliases`` () =
    let s = Completions.emit Completions.Xonsh root
    s |> should haveSubstring "'|plugin': 'plugins'"

[<Fact>]
let ``xonsh shells out via subprocess for DynamicCommand`` () =
    let s = Completions.emit Completions.Xonsh root
    s |> should haveSubstring "subprocess.run(kind[len('dynamic:'):].split()"
    s |> should haveSubstring "'dynamic:fedit plugins list --names'"

// ─────────────────────────────────────────────────────────────────────
// Yash
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``yash defines a completion/fedit function`` () =
    let s = Completions.emit Completions.Yash root
    s |> should haveSubstring "function completion/fedit {"

[<Fact>]
let ``yash walks all WORDS via -le (target word is separate)`` () =
    // Regression guard: yash's $WORDS excludes the target word, so the
    // walk bound must be -le, not -lt, or the last word is never matched.
    let s = Completions.emit Completions.Yash root
    s |> should haveSubstring "while [ \"$i\" -le \"${WORDS[#]}\" ]"

[<Fact>]
let ``yash transition arm normalizes hidden aliases`` () =
    let s = Completions.emit Completions.Yash root
    s |> should haveSubstring "('|plugins'|'|plugin') path='plugins'"

[<Fact>]
let ``yash uses complete -d for directory and a subshell for DynamicCommand`` () =
    let s = Completions.emit Completions.Yash root
    s |> should haveSubstring "complete -d"

    s
    |> should haveSubstring "complete -- $(fedit plugins list --names 2>/dev/null)"

// ─────────────────────────────────────────────────────────────────────
// Murex
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``murex emits a single autocomplete set schema`` () =
    let s = Completions.emit Completions.Murex root
    s |> should haveSubstring "autocomplete set fedit %[{ \"Flags\":"

[<Fact>]
let ``murex nests hidden aliases under FlagValues`` () =
    let s = Completions.emit Completions.Murex root
    s |> should haveSubstring "\"plugin\":"

[<Fact>]
let ``murex marks file positionals and dynamic completers`` () =
    let s = Completions.emit Completions.Murex root
    s |> should haveSubstring "\"IncFiles\": true"
    s |> should haveSubstring "\"Dynamic\": \"^fedit plugins list --names\""

// ─────────────────────────────────────────────────────────────────────
// installPath
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``installPath puts zsh under fpath-style ~/.zsh/completions`` () =
    Completions.installPath Completions.Zsh
    |> should haveSubstring "/.zsh/completions/_fedit"

[<Fact>]
let ``installPath puts bash under XDG bash-completion`` () =
    Completions.installPath Completions.Bash
    |> should haveSubstring "/.local/share/bash-completion/completions/fedit"

[<Fact>]
let ``installPath puts fish under ~/.config/fish/completions`` () =
    Completions.installPath Completions.Fish
    |> should haveSubstring "/.config/fish/completions/fedit.fish"

[<Fact>]
let ``installPath puts pwsh under ~/.config/powershell/completions`` () =
    Completions.installPath Completions.Pwsh
    |> should haveSubstring "/.config/powershell/completions/fedit.ps1"

[<Fact>]
let ``installPath puts nu under ~/.config/nushell/completions`` () =
    Completions.installPath Completions.Nushell
    |> should haveSubstring "/.config/nushell/completions/fedit.nu"

[<Fact>]
let ``installPath puts elvish under ~/.config/elvish/lib`` () =
    Completions.installPath Completions.Elvish
    |> should haveSubstring "/.config/elvish/lib/fedit-completions.elv"

[<Fact>]
let ``installPath puts xonsh under ~/.config/xonsh/completions`` () =
    Completions.installPath Completions.Xonsh
    |> should haveSubstring "/.config/xonsh/completions/fedit.xsh"

[<Fact>]
let ``installPath puts yash on the loadpath as completion/fedit`` () =
    Completions.installPath Completions.Yash
    |> should haveSubstring "/.local/share/yash/completion/fedit"

[<Fact>]
let ``installPath puts murex under ~/.config/fedit/completions`` () =
    Completions.installPath Completions.Murex
    |> should haveSubstring "/.config/fedit/completions/fedit.mx"
