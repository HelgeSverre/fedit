module Fedit.PluginHost.Program

open System
open System.IO
open System.Text.Json
open Fedit
open Fedit.PluginApi

/// Out-of-process plugin host. Reads newline-delimited JSON-RPC requests on
/// stdin, serves them, writes responses on stdout. stderr is the log channel.
///
/// Holds the loaded PluginRegistry (with each command's `Run` closure) in this
/// JIT process so the editor can stay NativeAOT — only command SPECS and
/// PluginAction results cross the wire.
[<EntryPoint>]
let main _argv =
    let stdin = Console.In
    let stdout = Console.Out
    let log (s: string) = Console.Error.WriteLine s

    // Path to the Fedit.PluginApi.dll the auto-generated plugin fsproj resolves
    // as its HintPath (see Plugins.fs). Prefer the sidecar beside the host:
    // a single-file/self-contained host bundles PluginApi, so Assembly.Location
    // is empty — but the .dll ships next to the host (release + AOT bundle +
    // Homebrew all place it there), so build it from there.
    let apiDll =
        let beside = Path.Combine(AppContext.BaseDirectory, "Fedit.PluginApi.dll")

        if File.Exists beside then
            beside
        else
            typeof<IPluginHost>.Assembly.Location

    let mutable registry = PluginRegistry.empty
    let mutable running = true

    let handle (line: string) : string =
        use doc = JsonDocument.Parse line
        let root = doc.RootElement

        match PluginProtocol.methodOf root with
        | "scan" ->
            let pluginsRoot, disabled = PluginProtocol.parseScanRequest root
            registry <- Plugins.scanAndLoad pluginsRoot apiDll disabled log
            PluginProtocol.scanResultJson registry
        | "invoke" ->
            let command, ctx = PluginProtocol.parseInvokeRequest root

            match registry.Commands.TryFind command with
            | Some b ->
                try
                    PluginProtocol.invokeResultJson (b.Spec.Run ctx)
                with ex ->
                    PluginProtocol.errorJson ("plugin '" + b.Source + "' threw: " + ex.Message)
            | None -> PluginProtocol.errorJson ("unknown command: " + command)
        | "shutdown" ->
            running <- false
            PluginProtocol.errorJson "shutting down"
        | other -> PluginProtocol.errorJson ("unknown method: " + other)

    while running do
        match PluginProtocol.readFrame stdin with
        | None -> running <- false
        | Some line ->
            let response =
                try
                    handle line
                with ex ->
                    PluginProtocol.errorJson ("host error: " + ex.Message)

            if running || response <> "" then
                PluginProtocol.writeFrame stdout response

    0
