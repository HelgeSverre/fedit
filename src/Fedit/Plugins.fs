namespace Fedit

// FS3261: nullness warnings from BCL APIs (Process.Start etc.). The .NET 10
// SDK ships these enabled by default but we don't carry F#-level null
// annotations through this MVP.
#nowarn "3261"

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Text.Json
open Fedit.PluginApi

// `Severity` from Fedit.PluginApi has an `Error` case that would shadow
// `Result.Error` in pattern matches. Use the fully-qualified
// `Result.Error e` / `Result.Ok v` constructors throughout this file.

/// Parsed `plugin.json` manifest. Fields mirror the spec; optional ones
/// default to empty string when absent.
type PluginManifest =
    { Name: string
      Version: string
      ApiVersion: string
      Description: string
      Author: string
      Homepage: string
      EntryAssembly: string
      EntryType: string }

/// Lifecycle status of a discovered plugin.
type PluginLoadStatus =
    | Loaded
    | Failed of reason: string
    | Disabled

/// Resolved command registration owned by a single plugin.
type PluginCommandBinding = { Source: string; Spec: PluginCommand }

/// Everything the host knows about a single plugin discovered on disk.
type LoadedPlugin =
    { Manifest: PluginManifest
      Path: string
      Status: PluginLoadStatus
      Commands: PluginCommand list
      Keybindings: (KeyChord * string) list
      Conflicts: string list }

/// Aggregate registry of all plugins known to the host.
type PluginRegistry =
    { Loaded: Map<string, LoadedPlugin>
      Enabled: Set<string>
      Commands: Map<string, PluginCommandBinding>
      Keybindings: (KeyChord * string) list
      Conflicts: string list }

/// Where a `plugin install` request is sourced from.
type PluginSource =
    | FolderSource of path: string
    | GitSource of url: string
    | ZipSource of path: string

module private PluginNames =
    let private hasSeparator (value: string) =
        value.Contains(Path.DirectorySeparatorChar)
        || value.Contains(Path.AltDirectorySeparatorChar)
        || value.Contains('/')
        || value.Contains('\\')

    let validateName (name: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace name then
            Result.Error "plugin name must not be empty"
        elif name = "." || name = ".." then
            Result.Error "plugin name must not be '.' or '..'"
        elif Path.IsPathRooted name || hasSeparator name then
            Result.Error "plugin name must be a single path segment"
        elif name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 then
            Result.Error "plugin name contains invalid filename characters"
        elif
            name
            |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '.' || c = '_' || c = '-'))
        then
            Result.Error "plugin name may contain only letters, digits, '.', '_' and '-'"
        else
            Result.Ok name

    let validateFileName (field: string) (name: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace name then
            Result.Error $"{field} must not be empty"
        elif Path.IsPathRooted name || hasSeparator name then
            Result.Error $"{field} must be a filename, not a path"
        elif
            name = "."
            || name = ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        then
            Result.Error $"{field} contains invalid filename characters"
        else
            Result.Ok name

    let childPath (root: string) (name: string) : Result<string, string> =
        match validateName name with
        | Result.Error e -> Result.Error e
        | Result.Ok safeName ->
            let rootFull = Path.GetFullPath root
            let target = Path.GetFullPath(Path.Combine(rootFull, safeName))

            let rootWithSeparator =
                if rootFull.EndsWith(Path.DirectorySeparatorChar) then
                    rootFull
                else
                    rootFull + string Path.DirectorySeparatorChar

            let comparison =
                if Path.DirectorySeparatorChar = '\\' then
                    StringComparison.OrdinalIgnoreCase
                else
                    StringComparison.Ordinal

            if target.StartsWith(rootWithSeparator, comparison) then
                Result.Ok target
            else
                Result.Error "plugin path escapes the plugin root"

[<RequireQualifiedAccess>]
module PluginRegistry =
    let empty: PluginRegistry =
        { Loaded = Map.empty
          Enabled = Set.empty
          Commands = Map.empty
          Keybindings = []
          Conflicts = [] }

// ---------------------------------------------------------------------------
// Manifest parsing
// ---------------------------------------------------------------------------

module private ManifestJson =
    let private optStr (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, e when e.ValueKind = JsonValueKind.String ->
            match e.GetString() with
            | null -> None
            | s -> Some s
        | _ -> None

    let parse (path: string) : Result<PluginManifest, string> =
        try
            let json = File.ReadAllText path
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let required field =
                match optStr root field with
                | Some v when not (String.IsNullOrWhiteSpace v) -> Ok v
                | _ -> Result.Error $"plugin.json missing required field '{field}'"

            match required "name", required "version", required "apiVersion" with
            | Result.Error e, _, _
            | _, Result.Error e, _
            | _, _, Result.Error e -> Result.Error e
            | Ok name, Ok version, Ok apiVersion ->
                match PluginNames.validateName name with
                | Result.Error e -> Result.Error e
                | Result.Ok safeName ->
                    if apiVersion <> "1" then
                        Result.Error $"unsupported apiVersion '{apiVersion}' (host supports '1')"
                    else
                        match required "entryAssembly", required "entryType" with
                        | Result.Error e, _
                        | _, Result.Error e -> Result.Error e
                        | Ok entryAssembly, Ok entryType ->
                            match PluginNames.validateFileName "entryAssembly" entryAssembly with
                            | Result.Error e -> Result.Error e
                            | Result.Ok safeEntryAssembly ->
                                Ok
                                    { Name = safeName
                                      Version = version
                                      ApiVersion = apiVersion
                                      Description = optStr root "description" |> Option.defaultValue ""
                                      Author = optStr root "author" |> Option.defaultValue ""
                                      Homepage = optStr root "homepage" |> Option.defaultValue ""
                                      EntryAssembly = safeEntryAssembly
                                      EntryType = entryType }
        with ex ->
            Result.Error $"failed to parse plugin.json: {ex.Message}"

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

module private PluginIO =
    let dllPath (pluginDir: string) (entryAssembly: string) =
        Path.Combine(pluginDir, "bin", "Release", "net10.0", entryAssembly)

    let isBuildStale (pluginDir: string) (entryAssembly: string) : bool =
        let target = dllPath pluginDir entryAssembly

        if not (File.Exists target) then
            true
        else
            let dllStamp = File.GetLastWriteTimeUtc target

            Directory.EnumerateFiles(pluginDir, "*.fs", SearchOption.AllDirectories)
            |> Seq.exists (fun src -> File.GetLastWriteTimeUtc src > dllStamp)

    let rec copyDirectory (src: string) (dst: string) =
        Directory.CreateDirectory dst |> ignore

        for file in Directory.EnumerateFiles(src) do
            let name = Path.GetFileName file
            File.Copy(file, Path.Combine(dst, name), overwrite = true)

        for dir in Directory.EnumerateDirectories(src) do
            let name = Path.GetFileName dir

            if name <> "bin" && name <> "obj" then
                copyDirectory dir (Path.Combine(dst, name))

// ---------------------------------------------------------------------------
// Build invocation
// ---------------------------------------------------------------------------

module private PluginBuild =
    let private generatedFsprojName = "plugin.generated.fsproj"

    let ensureFsproj (pluginDir: string) (pluginApiDllPath: string) (manifest: PluginManifest) : string =
        let userFsproj = Directory.EnumerateFiles(pluginDir, "*.fsproj") |> Seq.tryHead

        match userFsproj with
        | Some path -> path
        | None ->
            let generated = Path.Combine(pluginDir, generatedFsprojName)
            let assemblyName = Path.GetFileNameWithoutExtension manifest.EntryAssembly

            let xml =
                $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <AssemblyName>{assemblyName}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="**/*.fs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Fedit.PluginApi">
      <HintPath>{pluginApiDllPath}</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
"""

            File.WriteAllText(generated, xml)
            generated

    let runDotnetBuild (fsprojPath: string) : Result<unit, string> =
        let info = System.Diagnostics.ProcessStartInfo()
        info.FileName <- "dotnet"
        info.ArgumentList.Add "build"
        info.ArgumentList.Add fsprojPath
        info.ArgumentList.Add "-c"
        info.ArgumentList.Add "Release"
        info.ArgumentList.Add "--nologo"
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false

        try
            use proc = System.Diagnostics.Process.Start info
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()

            if proc.ExitCode = 0 then
                Ok()
            else
                Result.Error $"dotnet build failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}"
        with ex ->
            Result.Error $"failed to invoke dotnet build: {ex.Message}"

// ---------------------------------------------------------------------------
// Host-side IPluginHost implementation that collects registrations
// ---------------------------------------------------------------------------

module private HostCollectorImpl =
    /// Chords reserved for the editor itself. Plain `Char` is reserved
    /// because that's regular text input; the others are explicit editor
    /// commands plugins shouldn't shadow.
    let private isReserved (chord: KeyChord) =
        match chord with
        | KeyChord.Char _ -> true
        | _ -> false

    type Collector(pluginName: string, log: string -> unit) =
        let commands = ResizeArray<PluginCommand>()
        let keys = ResizeArray<KeyChord * string>()
        let conflicts = ResizeArray<string>()

        member _.Commands = List.ofSeq commands
        member _.Keybindings = List.ofSeq keys
        member _.Conflicts = List.ofSeq conflicts

        interface IPluginHost with
            member _.RegisterCommand(cmd) =
                if commands |> Seq.exists (fun c -> c.Name = cmd.Name) then
                    conflicts.Add $"{pluginName}: duplicate command '{cmd.Name}' ignored"
                else
                    commands.Add cmd

            member _.RegisterKeybinding(chord, commandName) =
                if isReserved chord then
                    conflicts.Add $"{pluginName}: refusing to bind reserved chord for '{commandName}'"
                else
                    keys.Add(chord, commandName)

            member _.Log(message) = log $"[plugin:{pluginName}] {message}"

// ---------------------------------------------------------------------------
// Assembly loading
// ---------------------------------------------------------------------------

type private PluginLoadContext(name: string) =
    inherit AssemblyLoadContext(name = name, isCollectible = false)
    /// Delegate everything to the default context. The plugin should not
    /// ship its own copies of FSharp.Core / Fedit.PluginApi.
    override _.Load(_assemblyName) = null

module private PluginLoad =
    let resolveRegister (assembly: Assembly) (entryType: string) : Result<IPluginHost -> unit, string> =
        let t = assembly.GetType(entryType, throwOnError = false, ignoreCase = false)

        if isNull t then
            Result.Error $"entryType '{entryType}' not found in {assembly.GetName().Name}"
        else
            let methodInfo =
                t.GetMethod(
                    "register",
                    BindingFlags.Public ||| BindingFlags.Static,
                    binder = null,
                    types = [| typeof<IPluginHost> |],
                    modifiers = null
                )

            if isNull methodInfo then
                Result.Error $"no static method 'register : IPluginHost -> unit' on {entryType}"
            else
                Ok(fun host -> methodInfo.Invoke(null, [| box host |]) |> ignore)

// ---------------------------------------------------------------------------
// Public surface
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Plugins =
    let tryParseManifest (path: string) : Result<PluginManifest, string> = ManifestJson.parse path

    /// Classify an install argument into the right source kind. Used by
    /// both the in-editor `:plugin install` handler and the CLI
    /// `fedit plugins install` subcommand so the URL/zip/path detection
    /// stays in one place.
    let detectSource (arg: string) : PluginSource =
        if
            arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
        then
            GitSource arg
        elif arg.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) then
            ZipSource arg
        else
            FolderSource arg

    let isBuildStale (pluginDir: string) (entryAssembly: string) : bool =
        PluginIO.isBuildStale pluginDir entryAssembly

    let private placeholderManifest (name: string) : PluginManifest =
        { Name = name
          Version = "?"
          ApiVersion = "?"
          Description = ""
          Author = ""
          Homepage = ""
          EntryAssembly = ""
          EntryType = "" }

    /// Scan a plugins root directory and return one `LoadedPlugin` per
    /// child directory. Manifest parse errors surface as `Failed` plugins
    /// rather than swallowed — users see them in `plugin list`.
    let discover (pluginsRoot: string) : LoadedPlugin list =
        if not (Directory.Exists pluginsRoot) then
            []
        else
            Directory.EnumerateDirectories pluginsRoot
            |> Seq.filter (fun d ->
                let name = Path.GetFileName d
                not (name.StartsWith "." || name.StartsWith ".staging-"))
            |> Seq.choose (fun pluginDir ->
                let manifestPath = Path.Combine(pluginDir, "plugin.json")

                if not (File.Exists manifestPath) then
                    None
                else
                    match tryParseManifest manifestPath with
                    | Ok manifest ->
                        Some
                            { Manifest = manifest
                              Path = pluginDir
                              Status = Disabled
                              Commands = []
                              Keybindings = []
                              Conflicts = [] }
                    | Result.Error reason ->
                        Some
                            { Manifest = placeholderManifest (Path.GetFileName pluginDir)
                              Path = pluginDir
                              Status = Failed reason
                              Commands = []
                              Keybindings = []
                              Conflicts = [] })
            |> List.ofSeq

    /// Build the plugin if its DLL is missing or stale. Returns the path
    /// to the built DLL on success.
    let build (pluginApiDllPath: string) (loaded: LoadedPlugin) : Result<string, string> =
        match loaded.Status with
        | Failed _ -> Result.Error "plugin is in Failed state; cannot build"
        | _ ->
            if not (isBuildStale loaded.Path loaded.Manifest.EntryAssembly) then
                Ok(PluginIO.dllPath loaded.Path loaded.Manifest.EntryAssembly)
            else
                let fsproj = PluginBuild.ensureFsproj loaded.Path pluginApiDllPath loaded.Manifest

                match PluginBuild.runDotnetBuild fsproj with
                | Ok() ->
                    let target = PluginIO.dllPath loaded.Path loaded.Manifest.EntryAssembly

                    if File.Exists target then
                        Ok target
                    else
                        Result.Error $"build succeeded but DLL not found at {target}"
                | Result.Error e -> Result.Error e

    /// Load the plugin DLL into an isolated AssemblyLoadContext, locate
    /// the `register` entry, and call it with a fresh collector. Returns
    /// the loaded plugin enriched with whatever it registered.
    let load (log: string -> unit) (loaded: LoadedPlugin) (dllPath: string) : LoadedPlugin =
        try
            let alc =
                PluginLoadContext($"fedit-plugin:{loaded.Manifest.Name}") :> AssemblyLoadContext

            let asm = alc.LoadFromAssemblyPath dllPath

            match PluginLoad.resolveRegister asm loaded.Manifest.EntryType with
            | Result.Error e -> { loaded with Status = Failed e }
            | Ok register ->
                let collector = HostCollectorImpl.Collector(loaded.Manifest.Name, log)

                try
                    register (collector :> IPluginHost)

                    { loaded with
                        Status = Loaded
                        Commands = collector.Commands
                        Keybindings = collector.Keybindings
                        Conflicts = collector.Conflicts }
                with ex ->
                    { loaded with
                        Status = Failed $"register threw: {ex.Message}" }
        with ex ->
            { loaded with
                Status = Failed $"assembly load failed: {ex.Message}" }

    /// Full pipeline: discover, build, load. Conflicts (duplicate
    /// command names across plugins) surface in the returned registry's
    /// `Conflicts` field for the UI to flag.
    let scanAndLoad
        (pluginsRoot: string)
        (pluginApiDllPath: string)
        (disabledPlugins: Set<string>)
        (log: string -> unit)
        : PluginRegistry =
        let discovered = discover pluginsRoot
        let conflicts = ResizeArray<string>()

        let processed =
            discovered
            |> List.map (fun loaded ->
                match loaded.Status with
                | _ when disabledPlugins.Contains loaded.Manifest.Name -> { loaded with Status = Disabled }
                | Failed _ -> loaded
                | _ ->
                    match build pluginApiDllPath loaded with
                    | Result.Error e -> { loaded with Status = Failed e }
                    | Ok dll -> load log loaded dll)

        let commands = System.Collections.Generic.Dictionary<string, PluginCommandBinding>()

        let keys = ResizeArray<KeyChord * string>()

        for plugin in processed do
            plugin.Conflicts |> List.iter conflicts.Add

            if plugin.Status = Loaded then
                for cmd in plugin.Commands do
                    if commands.ContainsKey cmd.Name then
                        conflicts.Add
                            $"command '{cmd.Name}' already registered by '{commands[cmd.Name].Source}'; '{plugin.Manifest.Name}' ignored"
                    else
                        commands[cmd.Name] <-
                            { Source = plugin.Manifest.Name
                              Spec = cmd }

                keys.AddRange plugin.Keybindings

        { Loaded = processed |> List.map (fun p -> p.Manifest.Name, p) |> Map.ofList
          Enabled =
            processed
            |> List.filter (fun p -> p.Status = Loaded)
            |> List.map (fun p -> p.Manifest.Name)
            |> Set.ofList
          Commands = commands |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
          Keybindings = List.ofSeq keys
          Conflicts = List.ofSeq conflicts }

    // -----------------------------------------------------------------------
    // Install / uninstall
    // -----------------------------------------------------------------------

    let install (pluginsRoot: string) (source: PluginSource) : string =
        Directory.CreateDirectory pluginsRoot |> ignore

        let staging = Path.Combine(pluginsRoot, ".staging-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory staging |> ignore

        try
            match source with
            | FolderSource path -> PluginIO.copyDirectory path staging
            | GitSource url ->
                let info = System.Diagnostics.ProcessStartInfo("git")
                info.ArgumentList.Add "clone"
                info.ArgumentList.Add "--depth"
                info.ArgumentList.Add "1"
                info.ArgumentList.Add url
                info.ArgumentList.Add staging
                info.RedirectStandardError <- true
                info.UseShellExecute <- false
                use proc = System.Diagnostics.Process.Start info
                let err = proc.StandardError.ReadToEnd()
                proc.WaitForExit()

                if proc.ExitCode <> 0 then
                    failwith $"git clone failed: {err}"
            | ZipSource path -> System.IO.Compression.ZipFile.ExtractToDirectory(path, staging)

            let manifestPath = Path.Combine(staging, "plugin.json")

            match tryParseManifest manifestPath with
            | Result.Error e -> failwith e
            | Ok manifest ->
                let target =
                    match PluginNames.childPath pluginsRoot manifest.Name with
                    | Result.Ok path -> path
                    | Result.Error e -> failwith e

                if Directory.Exists target then
                    failwith $"plugin '{manifest.Name}' already installed"

                Directory.Move(staging, target)
                manifest.Name
        finally
            if Directory.Exists staging then
                try
                    Directory.Delete(staging, recursive = true)
                with _ ->
                    ()

    let uninstall (pluginsRoot: string) (name: string) : unit =
        let target =
            match PluginNames.childPath pluginsRoot name with
            | Result.Ok path -> path
            | Result.Error e -> failwith e

        if not (Directory.Exists target) then
            failwith $"plugin '{name}' not installed"

        Directory.Delete(target, recursive = true)
