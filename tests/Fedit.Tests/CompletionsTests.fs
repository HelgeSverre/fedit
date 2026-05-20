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
let ``bash dispatches hidden aliases to the canonical handler`` () =
    let s = Completions.emit Completions.Bash root
    s |> should haveSubstring "plugins|plugin)"

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
