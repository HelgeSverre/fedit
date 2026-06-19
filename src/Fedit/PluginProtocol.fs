namespace Fedit

// FS3261: System.Text.Json GetString() is nullable; payloads come from our own
// writer, so these reads are total in practice.
#nowarn "3261"

open System.IO
open System.Text
open System.Text.Json
open Fedit.PluginApi

/// RPC message layer for the editor <-> plugin-host process: newline-delimited
/// JSON over stdio. Built on PluginWire (AOT-safe Utf8JsonWriter/JsonDocument),
/// so the editor side runs under NativeAOT. Compiled into both the editor and
/// the host exe (the host links this file + PluginWire + Plugins).
[<RequireQualifiedAccess>]
module PluginProtocol =

    // ---- framing: one JSON object per line --------------------------------

    let writeFrame (w: TextWriter) (json: string) =
        w.WriteLine json
        w.Flush()

    let readFrame (r: TextReader) : string option =
        match r.ReadLine() with
        | null -> None
        | line -> Some line

    let private build (write: Utf8JsonWriter -> unit) : string =
        use ms = new MemoryStream()

        (use w = new Utf8JsonWriter(ms)
         write w
         w.Flush())

        Encoding.UTF8.GetString(ms.ToArray())

    // ---- KeyChord <-> JSON -------------------------------------------------

    let private writeChord (w: Utf8JsonWriter) (c: KeyChord) =
        w.WriteStartObject()

        match c with
        | Char ch ->
            w.WriteString("k", "char")
            w.WriteString("c", string ch)
        | Ctrl ch ->
            w.WriteString("k", "ctrl")
            w.WriteString("c", string ch)
        | Alt ch ->
            w.WriteString("k", "alt")
            w.WriteString("c", string ch)
        | CtrlShift ch ->
            w.WriteString("k", "ctrlShift")
            w.WriteString("c", string ch)
        | F n ->
            w.WriteString("k", "f")
            w.WriteNumber("n", n)

        w.WriteEndObject()

    let private readChord (e: JsonElement) : KeyChord =
        let ch () = (e.GetProperty("c").GetString()).[0]

        match e.GetProperty("k").GetString() with
        | "char" -> Char(ch ())
        | "ctrl" -> Ctrl(ch ())
        | "alt" -> Alt(ch ())
        | "ctrlShift" -> CtrlShift(ch ())
        | "f" -> F(e.GetProperty("n").GetInt32())
        | other -> failwith ("unknown KeyChord kind: " + other)

    // ---- PluginRegistry <-> JSON ------------------------------------------
    // The whole registry crosses the wire so the editor reproduces it intact
    // (plugin manager UI, palette, keybindings). The Run closure cannot be
    // serialized; the editor reads a stub (`fun _ -> []`) and never calls it —
    // invocation goes back to the host via `invoke`.

    let private strp (e: JsonElement) (n: string) : string = e.GetProperty(n).GetString()

    let private writeManifest (w: Utf8JsonWriter) (m: PluginManifest) =
        w.WriteStartObject()
        w.WriteString("name", m.Name)
        w.WriteString("version", m.Version)
        w.WriteString("apiVersion", m.ApiVersion)
        w.WriteString("description", m.Description)
        w.WriteString("author", m.Author)
        w.WriteString("homepage", m.Homepage)
        w.WriteString("entryAssembly", m.EntryAssembly)
        w.WriteString("entryType", m.EntryType)
        w.WriteEndObject()

    let private readManifest (e: JsonElement) : PluginManifest =
        { Name = strp e "name"
          Version = strp e "version"
          ApiVersion = strp e "apiVersion"
          Description = strp e "description"
          Author = strp e "author"
          Homepage = strp e "homepage"
          EntryAssembly = strp e "entryAssembly"
          EntryType = strp e "entryType" }

    let private writeStatus (w: Utf8JsonWriter) (s: PluginLoadStatus) =
        w.WriteStartObject()

        match s with
        | Loaded -> w.WriteString("kind", "loaded")
        | Disabled -> w.WriteString("kind", "disabled")
        | Failed reason ->
            w.WriteString("kind", "failed")
            w.WriteString("reason", reason)

        w.WriteEndObject()

    let private readStatus (e: JsonElement) : PluginLoadStatus =
        match strp e "kind" with
        | "loaded" -> Loaded
        | "failed" -> Failed(strp e "reason")
        | _ -> Disabled

    // A command spec without its Run closure; readSpec installs a stub.
    let private writeSpec (w: Utf8JsonWriter) (c: PluginCommand) =
        w.WriteStartObject()
        w.WriteString("name", c.Name)
        w.WriteString("usage", c.Usage)
        w.WriteString("summary", c.Summary)
        w.WriteEndObject()

    let private readSpec (e: JsonElement) : PluginCommand =
        { Name = strp e "name"
          Usage = strp e "usage"
          Summary = strp e "summary"
          Run = fun _ -> [] }

    let private writeKeybindings (w: Utf8JsonWriter) (name: string) (kbs: (KeyChord * string) list) =
        w.WritePropertyName name
        w.WriteStartArray()

        for (chord, cmd) in kbs do
            w.WriteStartObject()
            w.WritePropertyName "chord"
            writeChord w chord
            w.WriteString("command", cmd)
            w.WriteEndObject()

        w.WriteEndArray()

    let private readKeybindings (e: JsonElement) : (KeyChord * string) list =
        [ for kb in e.EnumerateArray() -> readChord (kb.GetProperty "chord"), kb.GetProperty("command").GetString() ]

    let private writeStrings (w: Utf8JsonWriter) (name: string) (xs: string list) =
        w.WritePropertyName name
        w.WriteStartArray()
        xs |> List.iter w.WriteStringValue
        w.WriteEndArray()

    let private readStrings (e: JsonElement) : string list =
        [ for x in e.EnumerateArray() -> x.GetString() ]

    let registryToJson (r: PluginRegistry) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WritePropertyName "loaded"
            w.WriteStartArray()

            for KeyValue(_, p) in r.Loaded do
                w.WriteStartObject()
                w.WritePropertyName "manifest"
                writeManifest w p.Manifest
                w.WriteString("path", p.Path)
                w.WritePropertyName "status"
                writeStatus w p.Status
                w.WritePropertyName "commands"
                w.WriteStartArray()
                p.Commands |> List.iter (writeSpec w)
                w.WriteEndArray()
                writeKeybindings w "keybindings" p.Keybindings
                writeStrings w "conflicts" p.Conflicts
                w.WriteEndObject()

            w.WriteEndArray()
            writeStrings w "enabled" (Set.toList r.Enabled)
            w.WritePropertyName "commands"
            w.WriteStartArray()

            for KeyValue(_, b) in r.Commands do
                w.WriteStartObject()
                w.WriteString("source", b.Source)
                w.WritePropertyName "spec"
                writeSpec w b.Spec
                w.WriteEndObject()

            w.WriteEndArray()
            writeKeybindings w "keybindings" r.Keybindings
            writeStrings w "conflicts" r.Conflicts
            w.WriteEndObject())

    let private readRegistry (root: JsonElement) : PluginRegistry =
        let loaded =
            [ for p in (root.GetProperty "loaded").EnumerateArray() ->
                  let manifest = readManifest (p.GetProperty "manifest")

                  let lp: LoadedPlugin =
                      { Manifest = manifest
                        Path = strp p "path"
                        Status = readStatus (p.GetProperty "status")
                        Commands = [ for c in (p.GetProperty "commands").EnumerateArray() -> readSpec c ]
                        Keybindings = readKeybindings (p.GetProperty "keybindings")
                        Conflicts = readStrings (p.GetProperty "conflicts") }

                  manifest.Name, lp ]

        let commands =
            [ for b in (root.GetProperty "commands").EnumerateArray() ->
                  let spec = readSpec (b.GetProperty "spec")

                  spec.Name,
                  ({ Source = strp b "source"
                     Spec = spec }
                  : PluginCommandBinding) ]

        { Loaded = Map.ofList loaded
          Enabled = set (readStrings (root.GetProperty "enabled"))
          Commands = Map.ofList commands
          Keybindings = readKeybindings (root.GetProperty "keybindings")
          Conflicts = readStrings (root.GetProperty "conflicts") }

    // ---- requests (editor -> host) ----------------------------------------

    let scanRequest (pluginsRoot: string) (disabled: Set<string>) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteString("method", "scan")
            w.WriteString("pluginsRoot", pluginsRoot)
            w.WritePropertyName "disabled"
            w.WriteStartArray()
            disabled |> Set.iter w.WriteStringValue
            w.WriteEndArray()
            w.WriteEndObject())

    let invokeRequest (command: string) (ctx: PluginContext) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteString("method", "invoke")
            w.WriteString("command", command)
            w.WritePropertyName "context"
            w.WriteRawValue(PluginWire.contextToJson ctx)
            w.WriteEndObject())

    let shutdownRequest: string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteString("method", "shutdown")
            w.WriteEndObject())

    let methodOf (root: JsonElement) : string = root.GetProperty("method").GetString()

    let parseScanRequest (root: JsonElement) : string * Set<string> =
        let disabled =
            set [ for d in (root.GetProperty "disabled").EnumerateArray() -> d.GetString() ]

        root.GetProperty("pluginsRoot").GetString(), disabled

    let parseInvokeRequest (root: JsonElement) : string * PluginContext =
        root.GetProperty("command").GetString(), PluginWire.readContext (root.GetProperty "context")

    // ---- responses (host -> editor) ---------------------------------------

    let scanResultJson (registry: PluginRegistry) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteBoolean("ok", true)
            w.WritePropertyName "registry"
            w.WriteRawValue(registryToJson registry)
            w.WriteEndObject())

    let invokeResultJson (actions: PluginAction list) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteBoolean("ok", true)
            w.WritePropertyName "actions"
            w.WriteRawValue(PluginWire.actionsToJson actions)
            w.WriteEndObject())

    let errorJson (message: string) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteBoolean("ok", false)
            w.WriteString("error", message)
            w.WriteEndObject())

    let parseScanResult (json: string) : Result<PluginRegistry, string> =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        if root.GetProperty("ok").GetBoolean() then
            Result.Ok(readRegistry (root.GetProperty "registry"))
        else
            Result.Error(root.GetProperty("error").GetString())

    let parseInvokeResult (json: string) : Result<PluginAction list, string> =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        if root.GetProperty("ok").GetBoolean() then
            Result.Ok [ for e in (root.GetProperty "actions").EnumerateArray() -> PluginWire.readAction e ]
        else
            Result.Error(root.GetProperty("error").GetString())
