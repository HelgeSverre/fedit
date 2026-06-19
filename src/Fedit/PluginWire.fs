namespace Fedit

// FS3261: System.Text.Json's GetString() returns a nullable string; the wire
// payloads come from our own writer, so the reads below are total in practice.
#nowarn "3261"

open System
open System.Text.Json
open Fedit.PluginApi

/// AOT-safe JSON wire format for the editor <-> plugin-host process boundary.
///
/// Deliberately hand-rolled on Utf8JsonWriter + JsonDocument (zero reflection):
/// the editor is NativeAOT-compiled, where reflection-based JsonSerializer is
/// unavailable. This is the keystone that lets the JIT plugin host run
/// out-of-process while the editor stays AOT — `PluginAction` (a closed DU)
/// and `PluginContext` (records + options) cross the boundary as tagged JSON.
[<RequireQualifiedAccess>]
module PluginWire =

    // ---- writers (Utf8JsonWriter) -----------------------------------------

    let private severityStr =
        function
        | Info -> "info"
        | Warning -> "warning"
        | Error -> "error"

    let private writeCursor (w: Utf8JsonWriter) (c: CursorPosition) =
        w.WriteStartObject()
        w.WriteNumber("line", c.Line)
        w.WriteNumber("column", c.Column)
        w.WriteEndObject()

    let private writeNamedCursor (w: Utf8JsonWriter) (name: string) (c: CursorPosition) =
        w.WritePropertyName name
        writeCursor w c

    /// Write one PluginAction as a tagged object. Tags mirror the DU case
    /// names (camelCase); payload fields match the case fields.
    let writeAction (w: Utf8JsonWriter) (action: PluginAction) =
        w.WriteStartObject()

        match action with
        | Notify(sev, msg) ->
            w.WriteString("tag", "notify")
            w.WriteString("severity", severityStr sev)
            w.WriteString("message", msg)
        | InsertText s ->
            w.WriteString("tag", "insertText")
            w.WriteString("text", s)
        | ReplaceSelection s ->
            w.WriteString("tag", "replaceSelection")
            w.WriteString("text", s)
        | MoveCursor c ->
            w.WriteString("tag", "moveCursor")
            writeNamedCursor w "cursor" c
        | OpenFile p ->
            w.WriteString("tag", "openFile")
            w.WriteString("path", p)
        | SaveActiveBuffer -> w.WriteString("tag", "saveActiveBuffer")
        | RunCommand n ->
            w.WriteString("tag", "runCommand")
            w.WriteString("name", n)
        | SetClipboard s ->
            w.WriteString("tag", "setClipboard")
            w.WriteString("text", s)
        | SelectRange(anchor, cursor) ->
            w.WriteString("tag", "selectRange")
            writeNamedCursor w "anchor" anchor
            writeNamedCursor w "cursor" cursor
        | OpenFilePreview p ->
            w.WriteString("tag", "openFilePreview")
            w.WriteString("path", p)
        | RevealPath p ->
            w.WriteString("tag", "revealPath")
            w.WriteString("path", p)
        | ReplaceRange(from, to_, text) ->
            w.WriteString("tag", "replaceRange")
            writeNamedCursor w "from" from
            writeNamedCursor w "to" to_
            w.WriteString("text", text)
        | ClearSelection -> w.WriteString("tag", "clearSelection")
        | DeleteSelection -> w.WriteString("tag", "deleteSelection")
        | SwitchBuffer id ->
            w.WriteString("tag", "switchBuffer")
            w.WriteNumber("id", id)
        | NewBuffer(name, text) ->
            w.WriteString("tag", "newBuffer")
            w.WriteString("name", name)
            w.WriteString("text", text)
        | SetBufferActivation cmd ->
            w.WriteString("tag", "setBufferActivation")
            w.WriteString("commandName", cmd)
        | OpenFileAt(path, position, preview) ->
            w.WriteString("tag", "openFileAt")
            w.WriteString("path", path)
            writeNamedCursor w "position" position
            w.WriteBoolean("preview", preview)

        w.WriteEndObject()

    let private writeOptString (w: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some s -> w.WriteString(name, s)
        | None -> w.WriteNull name

    let private writeBufferView (w: Utf8JsonWriter) (b: BufferView) =
        w.WriteStartObject()
        w.WriteNumber("id", b.Id)
        w.WriteString("name", b.Name)
        writeOptString w "filePath" b.FilePath
        w.WriteString("text", b.Text)
        writeNamedCursor w "cursor" b.Cursor

        match b.Selection with
        | Some(a, c) ->
            w.WritePropertyName "selection"
            w.WriteStartObject()
            writeNamedCursor w "anchor" a
            writeNamedCursor w "cursor" c
            w.WriteEndObject()
        | None -> w.WriteNull "selection"

        w.WriteEndObject()

    let private writeContext (w: Utf8JsonWriter) (ctx: PluginContext) =
        w.WriteStartObject()
        w.WritePropertyName "activeBuffer"
        writeBufferView w ctx.ActiveBuffer
        w.WritePropertyName "allBuffers"
        w.WriteStartArray()
        ctx.AllBuffers |> List.iter (writeBufferView w)
        w.WriteEndArray()
        w.WritePropertyName "workspace"
        w.WriteStartObject()
        w.WriteString("rootPath", ctx.Workspace.RootPath)
        writeOptString w "selectedPath" ctx.Workspace.SelectedPath
        w.WritePropertyName "files"
        w.WriteStartArray()
        ctx.Workspace.Files |> List.iter w.WriteStringValue
        w.WriteEndArray()
        w.WriteEndObject()
        w.WriteEndObject()

    let private toJson (write: Utf8JsonWriter -> unit) : string =
        use ms = new IO.MemoryStream()

        (use w = new Utf8JsonWriter(ms)
         write w
         w.Flush())

        Text.Encoding.UTF8.GetString(ms.ToArray())

    let actionsToJson (actions: PluginAction list) : string =
        toJson (fun w ->
            w.WriteStartArray()
            actions |> List.iter (writeAction w)
            w.WriteEndArray())

    let contextToJson (ctx: PluginContext) : string = toJson (fun w -> writeContext w ctx)

    // ---- readers (JsonDocument / JsonElement) -----------------------------

    let private readCursor (e: JsonElement) : CursorPosition =
        { Line = e.GetProperty("line").GetInt32()
          Column = e.GetProperty("column").GetInt32() }

    let private str (e: JsonElement) (name: string) : string = e.GetProperty(name).GetString()

    let readAction (e: JsonElement) : PluginAction =
        match str e "tag" with
        | "notify" ->
            let sev =
                match str e "severity" with
                | "warning" -> Warning
                | "error" -> Error
                | _ -> Info

            Notify(sev, str e "message")
        | "insertText" -> InsertText(str e "text")
        | "replaceSelection" -> ReplaceSelection(str e "text")
        | "moveCursor" -> MoveCursor(readCursor (e.GetProperty "cursor"))
        | "openFile" -> OpenFile(str e "path")
        | "saveActiveBuffer" -> SaveActiveBuffer
        | "runCommand" -> RunCommand(str e "name")
        | "setClipboard" -> SetClipboard(str e "text")
        | "selectRange" -> SelectRange(readCursor (e.GetProperty "anchor"), readCursor (e.GetProperty "cursor"))
        | "openFilePreview" -> OpenFilePreview(str e "path")
        | "revealPath" -> RevealPath(str e "path")
        | "replaceRange" ->
            ReplaceRange(readCursor (e.GetProperty "from"), readCursor (e.GetProperty "to"), str e "text")
        | "clearSelection" -> ClearSelection
        | "deleteSelection" -> DeleteSelection
        | "switchBuffer" -> SwitchBuffer(e.GetProperty("id").GetInt32())
        | "newBuffer" -> NewBuffer(str e "name", str e "text")
        | "setBufferActivation" -> SetBufferActivation(str e "commandName")
        | "openFileAt" ->
            OpenFileAt(str e "path", readCursor (e.GetProperty "position"), e.GetProperty("preview").GetBoolean())
        | other -> failwith ("unknown PluginAction tag: " + other)

    let actionsFromJson (json: string) : PluginAction list =
        use doc = JsonDocument.Parse json
        [ for e in doc.RootElement.EnumerateArray() -> readAction e ]

    // ---- context reader (the host parses what the editor wrote) ------------

    let private optString (e: JsonElement) (name: string) : string option =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private readBufferView (e: JsonElement) : BufferView =
        let selection =
            match e.TryGetProperty "selection" with
            | true, s when s.ValueKind = JsonValueKind.Object ->
                Some(readCursor (s.GetProperty "anchor"), readCursor (s.GetProperty "cursor"))
            | _ -> None

        { Id = e.GetProperty("id").GetInt32()
          Name = str e "name"
          FilePath = optString e "filePath"
          Text = str e "text"
          Cursor = readCursor (e.GetProperty "cursor")
          Selection = selection }

    let readContext (e: JsonElement) : PluginContext =
        let ws = e.GetProperty "workspace"

        { ActiveBuffer = readBufferView (e.GetProperty "activeBuffer")
          AllBuffers = [ for b in (e.GetProperty "allBuffers").EnumerateArray() -> readBufferView b ]
          Workspace =
            { RootPath = str ws "rootPath"
              SelectedPath = optString ws "selectedPath"
              Files = [ for f in (ws.GetProperty "files").EnumerateArray() -> f.GetString() ] } }

    let contextFromJson (json: string) : PluginContext =
        use doc = JsonDocument.Parse json
        readContext doc.RootElement

    // ---- self-test: prove the round-trip runs (and stays stable) under AOT.

    /// Round-trips a representative action of every case and checks the
    /// re-serialized JSON is byte-identical. Returns true on success. Wired to
    /// the hidden `__plugin-wire-selftest` arg so it can run inside the AOT
    /// binary, where reflection-based JSON would crash.
    let selfTest () : bool =
        let sample =
            [ Notify(Warning, "hi \"there\"\n")
              InsertText "abc"
              ReplaceSelection "x"
              MoveCursor { Line = 3; Column = 7 }
              OpenFile "a/b.fs"
              SaveActiveBuffer
              RunCommand "wordcount"
              SetClipboard "clip"
              SelectRange({ Line = 1; Column = 1 }, { Line = 2; Column = 5 })
              OpenFilePreview "p.txt"
              RevealPath "r.txt"
              ReplaceRange({ Line = 1; Column = 1 }, { Line = 1; Column = 4 }, "new")
              ClearSelection
              DeleteSelection
              SwitchBuffer 42
              NewBuffer("scratch", "body")
              SetBufferActivation "jump"
              OpenFileAt("f.fs", { Line = 9; Column = 2 }, true) ]

        let json1 = actionsToJson sample
        let round = actionsFromJson json1
        let json2 = actionsToJson round
        json1 = json2 && List.length round = List.length sample
