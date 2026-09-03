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

    let private eventStr (e: PluginEvent) =
        match e with
        | BufferSaved -> "bufferSaved"
        | BufferOpened -> "bufferOpened"
        | BufferChanged -> "bufferChanged"
        | FocusChanged -> "focusChanged"

    let private eventOf (s: string) =
        match s with
        | "bufferSaved" -> BufferSaved
        | "bufferOpened" -> BufferOpened
        | "bufferChanged" -> BufferChanged
        | _ -> FocusChanged

    let private writeHooks (w: Utf8JsonWriter) (name: string) (hooks: (PluginEvent * string) list) =
        w.WritePropertyName name
        w.WriteStartArray()

        for (event, command) in hooks do
            w.WriteStartObject()
            w.WriteString("event", eventStr event)
            w.WriteString("command", command)
            w.WriteEndObject()

        w.WriteEndArray()

    let private readHooks (e: JsonElement) : (PluginEvent * string) list =
        [ for h in e.EnumerateArray() ->
              eventOf (h.GetProperty("event").GetString()), h.GetProperty("command").GetString() ]

    let private writeStringArray (w: Utf8JsonWriter) (name: string) (xs: string list) =
        w.WritePropertyName name
        w.WriteStartArray()
        xs |> List.iter w.WriteStringValue
        w.WriteEndArray()

    let private writeServers (w: Utf8JsonWriter) (name: string) (servers: LanguageServerSpec list) =
        w.WritePropertyName name
        w.WriteStartArray()

        for server in servers do
            w.WriteStartObject()
            w.WriteString("name", server.Name)
            w.WriteString("command", server.Command)
            writeStringArray w "args" server.Args
            writeStringArray w "fileTypes" server.FileTypes
            writeStringArray w "rootMarkers" server.RootMarkers
            w.WriteEndObject()

        w.WriteEndArray()

    let private readServers (e: JsonElement) : LanguageServerSpec list =
        let strings (x: JsonElement) =
            [ for s in x.EnumerateArray() -> s.GetString() ]

        [ for s in e.EnumerateArray() ->
              { Name = strp s "name"
                Command = strp s "command"
                Args = strings (s.GetProperty "args")
                FileTypes = strings (s.GetProperty "fileTypes")
                RootMarkers = strings (s.GetProperty "rootMarkers") } ]

    let private writeGrammars (w: Utf8JsonWriter) (name: string) (grammars: GrammarSpec list) =
        w.WritePropertyName name
        w.WriteStartArray()

        for grammar in grammars do
            w.WriteStartObject()
            w.WriteString("name", grammar.Name)
            writeStringArray w "extensions" grammar.Extensions
            w.WriteString("library", grammar.Library)

            match grammar.Symbol with
            | Some symbol -> w.WriteString("symbol", symbol)
            | None -> w.WriteNull "symbol"

            match grammar.Queries with
            | Some queries -> w.WriteString("queries", queries)
            | None -> w.WriteNull "queries"

            w.WriteEndObject()

        w.WriteEndArray()

    let private readGrammars (e: JsonElement) : GrammarSpec list =
        let optional (x: JsonElement) (name: string) =
            match x.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
            | _ -> None

        [ for g in e.EnumerateArray() ->
              { Name = strp g "name"
                Extensions = [ for s in (g.GetProperty "extensions").EnumerateArray() -> s.GetString() ]
                Library = strp g "library"
                Symbol = optional g "symbol"
                Queries = optional g "queries" } ]

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
                writeHooks w "hooks" p.Hooks
                writeServers w "languageServers" p.LanguageServers
                writeGrammars w "languages" p.Languages
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
            w.WritePropertyName "hooks"
            w.WriteStartArray()

            for hook in r.Hooks do
                w.WriteStartObject()
                w.WriteString("event", eventStr hook.Event)
                w.WriteString("command", hook.Command)
                w.WriteString("source", hook.Source)
                w.WriteEndObject()

            w.WriteEndArray()
            writeServers w "languageServers" r.LanguageServers
            writeGrammars w "languages" r.Languages
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
                        AsyncCommands = Map.empty
                        Keybindings = readKeybindings (p.GetProperty "keybindings")
                        Hooks = readHooks (p.GetProperty "hooks")
                        LanguageServers = readServers (p.GetProperty "languageServers")
                        Languages = readGrammars (p.GetProperty "languages")
                        Conflicts = readStrings (p.GetProperty "conflicts") }

                  manifest.Name, lp ]

        let commands =
            [ for b in (root.GetProperty "commands").EnumerateArray() ->
                  let spec = readSpec (b.GetProperty "spec")

                  spec.Name,
                  ({ Source = strp b "source"
                     Spec = spec
                     Invoke = fun _ _ -> System.Threading.Tasks.Task.FromResult [] }
                  : PluginCommandBinding) ]

        { Loaded = Map.ofList loaded
          Enabled = set (readStrings (root.GetProperty "enabled"))
          Commands = Map.ofList commands
          Keybindings = readKeybindings (root.GetProperty "keybindings")
          Hooks =
            [ for h in (root.GetProperty "hooks").EnumerateArray() ->
                  { Event = eventOf (h.GetProperty("event").GetString())
                    Command = h.GetProperty("command").GetString()
                    Source = h.GetProperty("source").GetString() } ]
          LanguageServers = readServers (root.GetProperty "languageServers")
          Languages = readGrammars (root.GetProperty "languages")
          Conflicts = readStrings (root.GetProperty "conflicts") }

    // ---- requests (editor -> host) ----------------------------------------

    /// Every request carries an `id`; the host answers out of order and the
    /// client matches responses by it.
    let scanRequest (id: int) (pluginsRoot: string) (disabled: Set<string>) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
            w.WriteString("method", "scan")
            w.WriteString("pluginsRoot", pluginsRoot)
            w.WritePropertyName "disabled"
            w.WriteStartArray()
            disabled |> Set.iter w.WriteStringValue
            w.WriteEndArray()
            w.WriteEndObject())

    let invokeRequest (id: int) (command: string) (ctx: PluginContext) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
            w.WriteString("method", "invoke")
            w.WriteString("command", command)
            w.WritePropertyName "context"
            w.WriteRawValue(PluginWire.contextToJson ctx)
            w.WriteEndObject())

    let shutdownRequest: string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", 0)
            w.WriteString("method", "shutdown")
            w.WriteEndObject())

    /// Ask the host to cancel the in-flight request `target` (its token
    /// fires; the request still answers, typically with an error).
    let cancelRequest (id: int) (target: int) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
            w.WriteString("method", "cancel")
            w.WriteNumber("target", target)
            w.WriteEndObject())

    let methodOf (root: JsonElement) : string = root.GetProperty("method").GetString()

    /// A line with a `method` is a request; without one it is a response.
    /// Both peers send both: the editor asks the host to scan/invoke, the
    /// host asks the editor for read-backs (`readClipboard`).
    let isRequest (root: JsonElement) : bool =
        match root.TryGetProperty "method" with
        | true, m -> m.ValueKind = JsonValueKind.String
        | _ -> false

    // ---- read-backs (host -> editor) ----------------------------------------

    let editorRequest (id: int) (method: string) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
            w.WriteString("method", method)
            w.WriteEndObject())

    let valueResultJson (id: int) (value: string) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
            w.WriteBoolean("ok", true)
            w.WriteString("value", value)
            w.WriteEndObject())

    /// The `value` of an ok response, or the error message.
    let parseValueResult (json: string) : Result<string, string> =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        if root.GetProperty("ok").GetBoolean() then
            Ok(root.GetProperty("value").GetString())
        else
            Result.Error(root.GetProperty("error").GetString())

    /// Request/response id; 0 when absent (pre-id peers).
    let idOf (root: JsonElement) : int =
        match root.TryGetProperty "id" with
        | true, e when e.ValueKind = JsonValueKind.Number -> e.GetInt32()
        | _ -> 0

    let parseCancelRequest (root: JsonElement) : int = root.GetProperty("target").GetInt32()

    let parseScanRequest (root: JsonElement) : string * Set<string> =
        let disabled =
            set [ for d in (root.GetProperty "disabled").EnumerateArray() -> d.GetString() ]

        root.GetProperty("pluginsRoot").GetString(), disabled

    let parseInvokeRequest (root: JsonElement) : string * PluginContext =
        root.GetProperty("command").GetString(), PluginWire.readContext (root.GetProperty "context")

    // ---- responses (host -> editor) ---------------------------------------

    let scanResultJson (id: int) (registry: PluginRegistry) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
            w.WriteBoolean("ok", true)
            w.WritePropertyName "registry"
            w.WriteRawValue(registryToJson registry)
            w.WriteEndObject())

    let invokeResultJson (id: int) (actions: PluginAction list) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
            w.WriteBoolean("ok", true)
            w.WritePropertyName "actions"
            w.WriteRawValue(PluginWire.actionsToJson actions)
            w.WriteEndObject())

    let errorJson (id: int) (message: string) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteNumber("id", id)
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
