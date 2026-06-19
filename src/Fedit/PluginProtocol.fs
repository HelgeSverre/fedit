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

    /// Editor-side command spec — everything the palette and keybindings need,
    /// WITHOUT the `Run` closure (that lives only in the host process).
    type CommandSpec =
        { Name: string
          Usage: string
          Summary: string
          Source: string }

    /// What a `scan` returns: the command specs, the (chord, command) bindings,
    /// and any load/name conflicts to surface as a warning.
    type ScanResult =
        { Specs: CommandSpec list
          Keybindings: (KeyChord * string) list
          Conflicts: string list }

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

    let scanResultJson (r: ScanResult) : string =
        build (fun w ->
            w.WriteStartObject()
            w.WriteBoolean("ok", true)
            w.WritePropertyName "specs"
            w.WriteStartArray()

            for s in r.Specs do
                w.WriteStartObject()
                w.WriteString("name", s.Name)
                w.WriteString("usage", s.Usage)
                w.WriteString("summary", s.Summary)
                w.WriteString("source", s.Source)
                w.WriteEndObject()

            w.WriteEndArray()
            w.WritePropertyName "keybindings"
            w.WriteStartArray()

            for (chord, cmd) in r.Keybindings do
                w.WriteStartObject()
                w.WritePropertyName "chord"
                writeChord w chord
                w.WriteString("command", cmd)
                w.WriteEndObject()

            w.WriteEndArray()
            w.WritePropertyName "conflicts"
            w.WriteStartArray()
            r.Conflicts |> List.iter w.WriteStringValue
            w.WriteEndArray()
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

    let parseScanResult (json: string) : Result<ScanResult, string> =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        if not (root.GetProperty("ok").GetBoolean()) then
            Result.Error(root.GetProperty("error").GetString())
        else
            let specs =
                [ for s in (root.GetProperty "specs").EnumerateArray() ->
                      { Name = s.GetProperty("name").GetString()
                        Usage = s.GetProperty("usage").GetString()
                        Summary = s.GetProperty("summary").GetString()
                        Source = s.GetProperty("source").GetString() } ]

            let keybindings =
                [ for kb in (root.GetProperty "keybindings").EnumerateArray() ->
                      readChord (kb.GetProperty "chord"), kb.GetProperty("command").GetString() ]

            let conflicts =
                [ for c in (root.GetProperty "conflicts").EnumerateArray() -> c.GetString() ]

            Result.Ok
                { Specs = specs
                  Keybindings = keybindings
                  Conflicts = conflicts }

    let parseInvokeResult (json: string) : Result<PluginAction list, string> =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        if root.GetProperty("ok").GetBoolean() then
            Result.Ok [ for e in (root.GetProperty "actions").EnumerateArray() -> PluginWire.readAction e ]
        else
            Result.Error(root.GetProperty("error").GetString())
