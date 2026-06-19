/// CLI surface for `fedit plugins <subcommand>`. Mirrors the in-editor
/// `:plugin <verb>` commands by calling the same `Plugins.*` helpers
/// directly — no Effects, no TUI loop. Each handler returns an exit
/// code (0 success, 1 failure, 2 bad usage).
module Fedit.Cli.Commands.Plugins

open System
open System.IO
open Fedit
open Fedit.Cli

let private pluginsRoot () =
    Path.Combine(ConfigIO.directory (), "plugins")

// ─────────────────────────────────────────────────────────────────────
// Per-subcommand option types
// ─────────────────────────────────────────────────────────────────────

type private CommonOpt = | Help

type private ListOpt =
    | ListHelp
    | ListBuild
    | ListNames
    | ListPlain

let private helpSpec =
    { Short = Some 'h'
      Long = "help"
      Value = NoValue
      Description = "Show this help and exit"
      Option = Help
      Completion = NoHint }

let private installApp: CliApp<CommonOpt> =
    { Name = "fedit plugins install"
      Summary = "Install a plugin from a folder, git URL, or .zip"
      Positionals =
        [ { Name = "source"
            Description = "Folder path, https/git URL, or path to a .zip"
            Completion = FilePath } ]
      Options = [ helpSpec ]
      Subcommands = [] }

let private removeApp: CliApp<CommonOpt> =
    { Name = "fedit plugins remove"
      Summary = "Uninstall a plugin by name"
      Positionals =
        [ { Name = "name"
            Description = "Installed plugin name (as it appears in plugin.json)"
            // Tab-completes installed plugin names via a recursive
            // call to `fedit plugins list --names`.
            Completion = DynamicCommand [ "plugins"; "list"; "--names" ] } ]
      Options = [ helpSpec ]
      Subcommands = [] }

let private listApp: CliApp<ListOpt> =
    { Name = "fedit plugins list"
      Summary = "List installed plugins"
      Positionals = []
      Options =
        [ { Short = Some 'h'
            Long = "help"
            Value = NoValue
            Description = "Show this help and exit"
            Option = ListHelp
            Completion = NoHint }
          { Short = None
            Long = "build"
            Value = NoValue
            Description = "Build + load each plugin and report command counts"
            Option = ListBuild
            Completion = NoHint }
          { Short = None
            Long = "names"
            Value = NoValue
            Description = "Print one plugin name per line (for shell completion)"
            Option = ListNames
            Completion = NoHint }
          { Short = None
            Long = "plain"
            Value = NoValue
            Description = "Tab-separated name/version/status/path (for scripts)"
            Option = ListPlain
            Completion = NoHint } ]
      Subcommands = [] }

let private validateApp: CliApp<CommonOpt> =
    { Name = "fedit plugins validate"
      Summary = "Check a plugin folder's plugin.json"
      Positionals =
        [ { Name = "path"
            Description = "Folder containing plugin.json"
            Completion = DirectoryPath } ]
      Options = [ helpSpec ]
      Subcommands = [] }

let private subcommands: CliSubcommandSpec list =
    [ { Name = "install"
        Aliases = []
        HiddenAliases = []
        Summary = "Install a plugin from a folder, git URL, or .zip" }
      { Name = "remove"
        Aliases = []
        HiddenAliases = []
        Summary = "Uninstall a plugin by name" }
      { Name = "list"
        Aliases = [ "ls" ]
        HiddenAliases = []
        Summary = "List installed plugins" }
      { Name = "validate"
        Aliases = []
        HiddenAliases = []
        Summary = "Check a plugin folder's plugin.json" } ]

/// Descriptor projected from the parser-side CliApps, used by the
/// completions generator. The Options/Positionals come straight
/// from each subcommand's `CliApp<_>` so the completion script
/// can't lie about what the parser accepts.
let descriptor: CliCommandDescriptor =
    let sub (name: string) (aliases: string list) (summary: string) (app: CliApp<'a>) : CliCommandDescriptor =
        { Name = name
          Aliases = aliases
          HiddenAliases = []
          Summary = summary
          Positionals = app.Positionals
          Options = app.Options |> List.map Parser.toOptionDescriptor
          Subcommands = [] }

    { Name = "plugins"
      Aliases = []
      HiddenAliases = [ "plugin" ]
      Summary = "Manage installed plugins"
      Positionals = []
      Options = [ helpSpec ] |> List.map Parser.toOptionDescriptor
      Subcommands =
        [ sub "install" [] "Install a plugin from a folder, git URL, or .zip" installApp
          sub "remove" [] "Uninstall a plugin by name" removeApp
          sub "list" [ "ls" ] "List installed plugins" listApp
          sub "validate" [] "Check a plugin folder's plugin.json" validateApp ] }

let private topApp: CliApp<CommonOpt> =
    { Name = "fedit plugins"
      Summary = "Manage installed plugins"
      Positionals = []
      Options = [ helpSpec ]
      Subcommands = subcommands }

// ─────────────────────────────────────────────────────────────────────
// Argument extraction helpers
// ─────────────────────────────────────────────────────────────────────

let private firstPositional items =
    items
    |> List.tryPick (function
        | Argument s -> Some s
        | _ -> None)

let private wantsHelp items =
    items
    |> List.exists (function
        | Option(Help, _) -> true
        | _ -> false)

let private wantsListHelp items =
    items
    |> List.exists (function
        | Option(ListHelp, _) -> true
        | _ -> false)

let private wantsBuild items =
    items
    |> List.exists (function
        | Option(ListBuild, _) -> true
        | _ -> false)

let private wantsNames items =
    items
    |> List.exists (function
        | Option(ListNames, _) -> true
        | _ -> false)

let private wantsPlain items =
    items
    |> List.exists (function
        | Option(ListPlain, _) -> true
        | _ -> false)

// ─────────────────────────────────────────────────────────────────────
// Subcommand handlers
// ─────────────────────────────────────────────────────────────────────

let private install (argv: string[]) : int =
    match Parser.parse installApp.Options argv with
    | Result.Error errors ->
        System.Console.Error.WriteLine(Parser.formatErrors installApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        System.Console.Out.WriteLine(Parser.formatHelp installApp)
        0
    | Result.Ok items ->
        match firstPositional items with
        | None ->
            System.Console.Error.WriteLine("fedit plugins install: missing <source> argument")
            System.Console.Error.WriteLine("Run 'fedit plugins install --help' for usage.")
            2
        | Some source ->
            try
                let name =
                    Fedit.Plugins.install (pluginsRoot ()) (Fedit.Plugins.detectSource source)

                System.Console.Out.WriteLine("installed: " + name)
                0
            with ex ->
                System.Console.Error.WriteLine("fedit plugins install: " + ex.Message)
                1

let private remove (argv: string[]) : int =
    match Parser.parse removeApp.Options argv with
    | Result.Error errors ->
        System.Console.Error.WriteLine(Parser.formatErrors removeApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        System.Console.Out.WriteLine(Parser.formatHelp removeApp)
        0
    | Result.Ok items ->
        match firstPositional items with
        | None ->
            System.Console.Error.WriteLine("fedit plugins remove: missing <name> argument")
            System.Console.Error.WriteLine("Run 'fedit plugins remove --help' for usage.")
            2
        | Some name ->
            try
                Fedit.Plugins.uninstall (pluginsRoot ()) name
                System.Console.Out.WriteLine("removed: " + name)
                0
            with ex ->
                System.Console.Error.WriteLine("fedit plugins remove: " + ex.Message)
                1

/// Human-facing status cell — version (manifest mode) or live command
/// counts (`--build`), or the failure reason.
let private statusText (plugin: LoadedPlugin) =
    match plugin.Status with
    | Loaded -> $"ok ({plugin.Commands.Length} cmd, {plugin.Keybindings.Length} key)"
    | Disabled ->
        let version = plugin.Manifest.Version

        if String.IsNullOrEmpty version then
            "ok"
        else
            $"ok ({version})"
    | Failed reason -> $"FAIL: {reason}"

/// Stable single-token status for `--plain` (machine consumers).
let private statusToken (plugin: LoadedPlugin) =
    match plugin.Status with
    | Failed _ -> "fail"
    | _ -> "ok"

/// Abbreviate $HOME to `~` for human output.
let private tildify (path: string) =
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

    if not (String.IsNullOrEmpty home) && path.StartsWith home then
        "~" + path.Substring home.Length
    else
        path

let private list (argv: string[]) : int =
    match Parser.parse listApp.Options argv with
    | Result.Error errors ->
        System.Console.Error.WriteLine(Parser.formatErrors listApp errors)
        2
    | Result.Ok items when wantsListHelp items ->
        System.Console.Out.WriteLine(Parser.formatHelp listApp)
        0
    | Result.Ok items ->
        let root = pluginsRoot ()

        try
            if wantsNames items then
                // One name per line, no formatting, no header — the
                // shape shell completion scripts consume. Silent when
                // no plugins exist so completion shows nothing rather
                // than a literal "(none)". Never builds.
                for plugin in Fedit.Plugins.discover root do
                    System.Console.Out.WriteLine(plugin.Manifest.Name)

                0
            else
                let plugins =
                    if wantsBuild items then
                        // Full scan + build + load through the out-of-process
                        // host — matches the in-editor list and stays
                        // AOT-safe (the editor binary cannot load plugin
                        // assemblies in-process).
                        use client = new Fedit.PluginHostClient(Fedit.PluginHostClient.defaultHostPath ())

                        match client.Scan(root, Set.empty) with
                        | Result.Ok registry -> registry.Loaded |> Map.toList |> List.map snd
                        | Result.Error e ->
                            System.Console.Error.WriteLine("fedit plugins list: " + e)
                            []
                    else
                        // Manifest-only: no `dotnet build`.
                        Fedit.Plugins.discover root

                if wantsPlain items then
                    // Tab-separated, no header, silent when empty — for grep/awk/cut.
                    for p in plugins do
                        System.Console.Out.WriteLine(
                            p.Manifest.Name
                            + "\t"
                            + p.Manifest.Version
                            + "\t"
                            + statusToken p
                            + "\t"
                            + p.Path
                        )

                    0
                else
                    // Human default: print the install dir once (npm/pipx
                    // style), then name + status per row.
                    if List.isEmpty plugins then
                        System.Console.Out.WriteLine("no plugins installed (" + tildify root + ")")
                    else
                        System.Console.Out.WriteLine("plugins in " + tildify root)

                        for p in plugins do
                            System.Console.Out.WriteLine("  " + p.Manifest.Name.PadRight 24 + " " + statusText p)

                    0
        with ex ->
            System.Console.Error.WriteLine("fedit plugins list: " + ex.Message)
            1

let private validate (argv: string[]) : int =
    match Parser.parse validateApp.Options argv with
    | Result.Error errors ->
        System.Console.Error.WriteLine(Parser.formatErrors validateApp errors)
        2
    | Result.Ok items when wantsHelp items ->
        System.Console.Out.WriteLine(Parser.formatHelp validateApp)
        0
    | Result.Ok items ->
        match firstPositional items with
        | None ->
            System.Console.Error.WriteLine("fedit plugins validate: missing <path> argument")
            System.Console.Error.WriteLine("Run 'fedit plugins validate --help' for usage.")
            2
        | Some path ->
            let manifestPath = Path.Combine(path, "plugin.json")

            if not (File.Exists manifestPath) then
                System.Console.Error.WriteLine("fedit plugins validate: no plugin.json found in " + path)
                1
            else
                match Fedit.Plugins.tryParseManifest manifestPath with
                | Ok manifest ->
                    System.Console.Out.WriteLine(
                        "OK: "
                        + manifest.Name
                        + " "
                        + manifest.Version
                        + " (apiVersion "
                        + manifest.ApiVersion
                        + "); entryType="
                        + manifest.EntryType
                    )

                    0
                | Result.Error reason ->
                    System.Console.Error.WriteLine("fedit plugins validate: " + reason)
                    1

// ─────────────────────────────────────────────────────────────────────
// Top-level router
// ─────────────────────────────────────────────────────────────────────

let run (argv: string[]) : int =
    match Parser.route subcommands argv with
    | Some("install", rest) -> install rest
    | Some("remove", rest) -> remove rest
    | Some("list", rest) -> list rest
    | Some("validate", rest) -> validate rest
    | Some(other, _) ->
        // Defensive — should be unreachable while subcommands is exhaustive.
        System.Console.Error.WriteLine("fedit plugins: unknown subcommand '" + other + "'")
        System.Console.Error.WriteLine("Run 'fedit plugins --help' for usage.")
        2
    | None ->
        System.Console.Out.WriteLine(Parser.formatHelp topApp)
        0
