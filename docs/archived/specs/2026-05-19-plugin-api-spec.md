# Plugin API Design Spec

**Status:** Draft
**Date:** 2026-05-19
**Scope:** MVP plugin API for fedit

## Goal

Let third-party developers write fedit extensions in F# that register **commands** and **keybindings**. Plugins live in `~/.config/fedit/plugins/<name>/`, are distributed as folders / git repos / zips, and are managed inside fedit through a `plugin` command.

## Non-goals (MVP)

- Sandboxing / capability restriction. Plugins are full .NET code and have full trust. This is documented in `docs/plugins.md`.
- Cross-language plugins (TypeScript, Lua, etc.).
- A package registry / discovery service.
- Hot-reload while running. Reload requires restart or `plugin reload`.
- Plugin UI panels, custom themes, custom file types, LSP integration.
- Marketplace, signing, version constraints between plugins.

## Distribution model

Plugins are folders. A plugin folder is valid when it contains:

```
~/.config/fedit/plugins/<name>/
├── plugin.json          (manifest — required)
├── Plugin.fs            (source — required; multiple .fs files allowed)
├── plugin.fsproj        (optional — auto-generated if absent)
└── bin/Release/net9.0/  (build output — auto-managed by host)
```

Three install sources:

1. **Folder path** — `plugin install /some/path` copies the folder into the plugin directory.
2. **Git URL** — `plugin install https://github.com/foo/fedit-plugin-bar` runs `git clone` into the plugin directory.
3. **Zip path** — `plugin install /some/plugin.zip` extracts into the plugin directory.

The plugin's directory name comes from `plugin.json`'s `name` field, not the source path.

## Plugin manifest (`plugin.json`)

```json
{
    "name": "wordcount",
    "version": "0.1.0",
    "apiVersion": "1",
    "description": "Adds :wc to count words in the active buffer.",
    "author": "Helge",
    "homepage": "https://github.com/helgesverre/fedit-wordcount",
    "entryAssembly": "wordcount.dll",
    "entryType": "Wordcount.Plugin"
}
```

| Field           | Required | Notes                                                                                      |
| --------------- | -------- | ------------------------------------------------------------------------------------------ |
| `name`          | yes      | Kebab-case, used as folder name and command-source label. Must match folder name on disk.  |
| `version`       | yes      | Semver. Informational only in MVP.                                                         |
| `apiVersion`    | yes      | Plugin-API major version. MVP is `"1"`. Host refuses to load mismatched majors.            |
| `description`   | no       | Shown in `plugin list`.                                                                    |
| `author`        | no       | Informational.                                                                             |
| `homepage`      | no       | Informational.                                                                             |
| `entryAssembly` | yes      | DLL filename produced by `dotnet build`. Loaded from `bin/Release/net9.0/<entryAssembly>`. |
| `entryType`     | yes      | Fully-qualified F# type or module containing a `register` function.                        |

## Plugin entry contract

Plugins implement a `register : IPluginHost -> unit` function on the type/module named in `entryType`:

```fsharp
namespace Wordcount

open Fedit.PluginApi

module Plugin =
    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "wc"
              Usage = "wc"
              Summary = "Count words in the active buffer."
              Run = fun ctx ->
                  let n = ctx.ActiveBuffer.Text.Split() |> Array.length
                  [ Notify(Info, sprintf "%d words" n) ] }

        host.RegisterKeybinding(KeyChord.Ctrl 'w', "wc")
```

`register` is called once at plugin load. It must not throw. Failures during register are logged and the plugin is marked failed.

## Public API surface (`Fedit.PluginApi`)

The API ships as a separate `src/Fedit.PluginApi/Fedit.PluginApi.fsproj` library. It only depends on FSharp.Core. Plugins reference this DLL (or NuGet package eventually).

### Types

```fsharp
namespace Fedit.PluginApi

type Severity = Info | Warning | Error

type CursorPosition = { Line: int; Column: int }

type BufferView =
    { Id: int
      Name: string
      FilePath: string option
      Text: string
      Cursor: CursorPosition
      Selection: (CursorPosition * CursorPosition) option }

type WorkspaceView =
    { RootPath: string }

type PluginContext =
    { ActiveBuffer: BufferView
      AllBuffers: BufferView list
      Workspace: WorkspaceView }

type PluginAction =
    | Notify of Severity * string
    | InsertText of string
    | ReplaceSelection of string
    | MoveCursor of CursorPosition
    | OpenFile of path: string
    | SaveActiveBuffer
    | RunCommand of name: string  // dispatch a built-in or other plugin command by name
    | SetClipboard of string

type PluginCommand =
    { Name: string
      Usage: string
      Summary: string
      Run: PluginContext -> PluginAction list }

type KeyChord =
    | Char of char
    | Ctrl of char
    | Alt of char
    | CtrlShift of char
    | F of int           // F1..F12

type IPluginHost =
    abstract member RegisterCommand: PluginCommand -> unit
    abstract member RegisterKeybinding: KeyChord * commandName: string -> unit
    abstract member Log: string -> unit
```

### Notes on the contract

- `PluginContext` is a snapshot — plugins never see mutable state. The host builds it fresh for each `Run` invocation.
- `PluginAction` is a closed DU of well-defined side effects. New action variants in v2+ are additive.
- `KeyChord` deliberately excludes chord sequences and arrow keys for MVP — only modifier+character. Function keys included for power users.
- `RunCommand` lets plugins chain into other commands (built-in or plugin) without re-implementing them.

## Host integration

### State model changes (`src/Fedit/Model.fs`)

Add a `PluginRegistry` field to `Model`. The registry holds:

- `Loaded: Map<string, LoadedPlugin>` — plugins discovered on disk
- `Enabled: Set<string>` — names of plugins currently active
- `Commands: Map<string, PluginCommandBinding>` — command name → binding (which plugin, handler closure)
- `Keybindings: (KeyChord * string) list` — (chord, command-name) pairs

A `LoadedPlugin` carries: manifest, on-disk path, load status (`Loaded | Failed of string`), the resolved `Fedit.PluginApi.IPluginHost`-using assembly handle, and the list of commands/keybindings it registered.

### New Msgs

```fsharp
| PluginsScanned of Result<PluginRegistry, string>
| PluginInstalled of name:string * Result<unit, string>
| PluginRemoved of name:string * Result<unit, string>
| PluginEnableChanged of name:string * enabled:bool
| PluginBuildFinished of name:string * Result<unit, string>
| PluginCommandFinished of source:string * commandName:string
```

### New Effects

```fsharp
| ScanPlugins
| InstallPluginFromSource of source: PluginSource  // Folder | GitUrl | Zip
| RemovePluginDir of name: string
| BuildPlugin of pluginPath: string
| RunPluginCommand of source:string * commandName:string * snapshot:PluginContext * handler:(PluginContext -> PluginAction list)
```

`RunPluginCommand` is interesting: the handler is a closure inside the effect. The runtime runs it on the thread pool (since plugin code may be slow) and converts the returned `PluginAction list` into core `Effect`s plus `Msg`s posted to the queue. This keeps plugin execution off the UI thread.

For MVP we may want to run plugin handlers synchronously on the UI thread initially — they're expected to be fast, and async dispatching introduces ordering questions (insert-then-cursor-move). Decision: **run plugin handlers synchronously inline** in `executeCommand`. Document the constraint that handlers should be fast (< 50ms ideally). Revisit if we add long-running commands.

### Command resolution

`Commands.specs` becomes `Commands.builtinSpecs`, and a new `Commands.allSpecs : PluginRegistry -> Spec list` merges built-ins with plugin commands (registered as `Spec`s with constructors that produce a new `PluginInvoke of source:string * name:string` command variant).

The `Command` DU in `Commands.fs` gains:

```fsharp
| PluginInvoke of source: string * commandName: string * argument: string
| Plugin of verb: string * argument: string   // for `plugin list`, `plugin install ...`
```

### Conflict policy

When two plugins register the same command name, **the first wins**; the loser logs a warning to the notification dock at startup. When a plugin command collides with a built-in, the built-in wins. Users see the conflict in `plugin list`.

Plugin keybindings can shadow built-in keys, but `Enter`, `Escape`, `Backspace`, `Tab`, arrow keys, and basic character input are **reserved** — `RegisterKeybinding` rejects them with a logged warning.

### Keybinding dispatch (`src/Fedit/Editor.fs`)

In the editor focus key-pressed branch, before the existing match, check `model.Plugins.Keybindings` for a chord match. If matched, dispatch `RunCommand commandName` synthetically and skip default handling. Plugin keybindings only fire when focus is `Editor` for MVP (not in command bar, sidebar, search).

### Plugin discovery + loading (`src/Fedit/Plugins.fs` — new module)

New file `src/Fedit/Plugins.fs` placed in the project after `Commands.fs` and before `Model.fs` references it via the registry type. Responsibilities:

- Manifest parsing (`plugin.json`)
- Source-vs-DLL freshness check (rebuild if any `.fs` mtime > DLL mtime)
- `dotnet build` invocation with structured stdout/stderr capture
- `AssemblyLoadContext` isolation per plugin
- Reflection-based discovery of `entryType.register`
- Calling `register` with a fedit-internal `IPluginHost` implementation that collects commands/keybindings into a per-plugin registration record

### Build invocation

Host invokes `dotnet build -c Release --nologo` synchronously inside the `BuildPlugin` effect (on thread pool). If the plugin directory contains no `.fsproj`, host writes a minimal one:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Library</OutputType>
    <AssemblyName>{plugin-name}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="**/*.fs" />
    <Reference Include="Fedit.PluginApi">
      <HintPath>{path-to-host-installed-PluginApi.dll}</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

The host stamps the `HintPath` to the `Fedit.PluginApi.dll` shipped alongside the fedit binary (or in the dotnet publish output). Plugin authors who write their own `.fsproj` can reference `Fedit.PluginApi` however they like (PackageReference once we publish to NuGet, ProjectReference for local dev).

### Persistence

Per-plugin enable/disable in `~/.config/fedit/config.json`:

```json
{
  "theme": "...",
  "recent": [...],
  "plugins": {
    "wordcount": "enabled",
    "experimental-thing": "disabled"
  }
}
```

A plugin discovered on disk but missing from this map is treated as **enabled** by default.

## Plugin manager commands

A single built-in `plugin` command with verb sub-parsing. Tab completion suggests verbs first, then plugin names.

| Invocation                     | Behavior                                                                                                                            |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------- |
| `plugin list`                  | Show loaded plugins (name, version, status, # commands, # keys) in the dock panel.                                                  |
| `plugin enable <name>`         | Mark enabled in config, reload registry.                                                                                            |
| `plugin disable <name>`        | Mark disabled, drop its commands/keybindings from registry.                                                                         |
| `plugin install <url-or-path>` | Detect source kind, install, build, register. Emits notification on success/failure.                                                |
| `plugin remove <name>`         | Delete plugin folder, drop from registry. Prompts for confirmation via two-stage notification.                                      |
| `plugin reload`                | Re-scan plugin dir, rebuild stale plugins, reload all.                                                                              |
| `plugin validate <path>`       | Dry-run install: parse manifest, attempt build, attempt entry resolution, list what would be registered. Does **not** copy/install. |

## Failure semantics

- A failed build → plugin is `Failed`, error visible in `plugin list`, host continues.
- A `register` that throws → plugin marked `Failed`, partial registrations rolled back, error logged.
- A plugin command handler that throws → notification dock surfaces the error, plugin stays loaded.
- A keybinding conflict at registration → second registration ignored with warning.

## Open questions (defer to v2)

- **API versioning** — When v2 lands, how do we let v1 plugins keep working? Likely: ship `Fedit.PluginApi.v1.dll` alongside, route by `apiVersion`.
- **Long-running commands** — `RunPluginCommand` as an effect (async) once we have a use case.
- **Async actions** — Plugins returning `Async<PluginAction list>` for I/O.
- **Workspace mutation** — Allow plugins to create/delete files? Today only via `RunCommand "open ..."`.
- **Settings per plugin** — A `~/.config/fedit/plugins/<name>/config.json` accessible via `IPluginHost.Config<T>()`.
