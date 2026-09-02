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

    let private textStyleStr (style: TextStyle) =
        match style with
        | TextStyle.Plain -> "plain"
        | TextStyle.Accent -> "accent"
        | TextStyle.Muted -> "muted"
        | TextStyle.Error -> "error"
        | TextStyle.Warning -> "warning"
        | TextStyle.Keyword -> "keyword"
        | TextStyle.String -> "string"

    let private textStyleOf (name: string) =
        match name with
        | "accent" -> TextStyle.Accent
        | "muted" -> TextStyle.Muted
        | "error" -> TextStyle.Error
        | "warning" -> TextStyle.Warning
        | "keyword" -> TextStyle.Keyword
        | "string" -> TextStyle.String
        | _ -> TextStyle.Plain

    let private writeCursor (w: Utf8JsonWriter) (c: CursorPosition) =
        w.WriteStartObject()
        w.WriteNumber("line", c.Line)
        w.WriteNumber("column", c.Column)
        w.WriteEndObject()

    let private writeNamedCursor (w: Utf8JsonWriter) (name: string) (c: CursorPosition) =
        w.WritePropertyName name
        writeCursor w c

    let private writeOptString (w: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some s -> w.WriteString(name, s)
        | None -> w.WriteNull name

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
        | MoveLinesUp count ->
            w.WriteString("tag", "moveLinesUp")
            w.WriteNumber("count", count)
        | MoveLinesDown count ->
            w.WriteString("tag", "moveLinesDown")
            w.WriteNumber("count", count)
        | ShowPanel(title, lines) ->
            w.WriteString("tag", "showPanel")
            w.WriteString("title", title)
            w.WriteStartArray "lines"

            for line in lines do
                w.WriteStartArray()

                for segment in line do
                    w.WriteStartObject()
                    w.WriteString("text", segment.Text)
                    w.WriteString("style", textStyleStr segment.Style)
                    w.WriteEndObject()

                w.WriteEndArray()

            w.WriteEndArray()
        | SetStatusItem text ->
            w.WriteString("tag", "setStatusItem")

            match text with
            | Some value -> w.WriteString("text", value)
            | None -> w.WriteNull "text"
        | ShowPicker(title, items, onSelect) ->
            w.WriteString("tag", "showPicker")
            w.WriteString("title", title)
            w.WriteString("onSelect", onSelect)
            w.WriteStartArray "items"

            for item in items do
                w.WriteStartObject()
                w.WriteString("id", item.Id)
                w.WriteString("title", item.Title)
                writeOptString w "subtitle" item.Subtitle
                w.WriteEndObject()

            w.WriteEndArray()
        | PromptInput(label, initial, onSubmit) ->
            w.WriteString("tag", "promptInput")
            w.WriteString("label", label)
            w.WriteString("initial", initial)
            w.WriteString("onSubmit", onSubmit)

        w.WriteEndObject()

    let private writeBufferView (w: Utf8JsonWriter) (b: BufferView) =
        w.WriteStartObject()
        w.WriteNumber("id", b.Id)
        w.WriteString("name", b.Name)
        writeOptString w "filePath" b.FilePath
        w.WriteString("text", b.Text)
        writeNamedCursor w "cursor" b.Cursor
        writeOptString w "language" b.Language
        w.WriteBoolean("dirty", b.Dirty)
        w.WriteNumber("editTick", b.EditTick)
        w.WriteStartArray "diagnostics"

        for d in b.Diagnostics do
            w.WriteStartObject()
            w.WriteString("severity", severityStr d.Severity)
            w.WriteString("message", d.Message)
            writeOptString w "source" d.Source
            writeNamedCursor w "start" d.Start
            writeNamedCursor w "end" d.End
            w.WriteEndObject()

        w.WriteEndArray()

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
        w.WritePropertyName "event"

        match ctx.Event with
        | Some BufferSaved -> w.WriteStringValue "bufferSaved"
        | Some BufferOpened -> w.WriteStringValue "bufferOpened"
        | Some BufferChanged -> w.WriteStringValue "bufferChanged"
        | Some FocusChanged -> w.WriteStringValue "focusChanged"
        | None -> w.WriteNullValue()

        w.WriteStartObject "config"

        for KeyValue(key, value) in ctx.Config do
            w.WriteString(key, value)

        w.WriteEndObject()
        writeOptString w "argument" ctx.Argument
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

    let private optString (e: JsonElement) (name: string) : string option =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

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
        | "moveLinesUp" -> MoveLinesUp(e.GetProperty("count").GetInt32())
        | "moveLinesDown" -> MoveLinesDown(e.GetProperty("count").GetInt32())
        | "showPanel" ->
            let lines =
                [ for line in e.GetProperty("lines").EnumerateArray() ->
                      [ for segment in line.EnumerateArray() ->
                            { Text = str segment "text"
                              Style = textStyleOf (str segment "style") } ] ]

            ShowPanel(str e "title", lines)
        | "setStatusItem" ->
            let text = e.GetProperty "text"

            SetStatusItem(
                if text.ValueKind = JsonValueKind.Null then
                    None
                else
                    Some(text.GetString())
            )
        | "showPicker" ->
            ShowPicker(
                str e "title",
                [ for item in e.GetProperty("items").EnumerateArray() ->
                      { Id = str item "id"
                        Title = str item "title"
                        Subtitle = optString item "subtitle" } ],
                str e "onSelect"
            )
        | "promptInput" -> PromptInput(str e "label", str e "initial", str e "onSubmit")
        | other -> failwith ("unknown PluginAction tag: " + other)

    let actionsFromJson (json: string) : PluginAction list =
        use doc = JsonDocument.Parse json
        [ for e in doc.RootElement.EnumerateArray() -> readAction e ]

    // ---- context reader (the host parses what the editor wrote) ------------

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
          Selection = selection
          Language = optString e "language"
          Dirty =
            match e.TryGetProperty "dirty" with
            | true, d -> d.ValueKind = JsonValueKind.True
            | _ -> false
          EditTick =
            match e.TryGetProperty "editTick" with
            | true, t when t.ValueKind = JsonValueKind.Number -> t.GetInt32()
            | _ -> 0
          Diagnostics =
            match e.TryGetProperty "diagnostics" with
            | true, ds when ds.ValueKind = JsonValueKind.Array ->
                [ for d in ds.EnumerateArray() ->
                      { Severity =
                          match str d "severity" with
                          | "warning" -> Warning
                          | "error" -> Error
                          | _ -> Info
                        Message = str d "message"
                        Source = optString d "source"
                        Start = readCursor (d.GetProperty "start")
                        End = readCursor (d.GetProperty "end") } ]
            | _ -> [] }

    let readContext (e: JsonElement) : PluginContext =
        let ws = e.GetProperty "workspace"

        { ActiveBuffer = readBufferView (e.GetProperty "activeBuffer")
          AllBuffers = [ for b in (e.GetProperty "allBuffers").EnumerateArray() -> readBufferView b ]
          Workspace =
            { RootPath = str ws "rootPath"
              SelectedPath = optString ws "selectedPath"
              Files = [ for f in (ws.GetProperty "files").EnumerateArray() -> f.GetString() ] }
          Event =
            match e.TryGetProperty "event" with
            | true, ev when ev.ValueKind = JsonValueKind.String ->
                match ev.GetString() with
                | "bufferSaved" -> Some BufferSaved
                | "bufferOpened" -> Some BufferOpened
                | "bufferChanged" -> Some BufferChanged
                | "focusChanged" -> Some FocusChanged
                | _ -> None
            | _ -> None
          Config =
            match e.TryGetProperty "config" with
            | true, c when c.ValueKind = JsonValueKind.Object ->
                [ for p in c.EnumerateObject() -> p.Name, p.Value.GetString() ] |> Map.ofList
            | _ -> Map.empty
          Argument = optString e "argument" }

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
              OpenFileAt("f.fs", { Line = 9; Column = 2 }, true)
              MoveLinesUp 3
              MoveLinesDown 2
              ShowPanel(
                  "Panel",
                  [ [ { Text = "a"; Style = TextStyle.Plain }
                      { Text = "b"; Style = TextStyle.Accent } ]
                    [] ]
              )
              SetStatusItem(Some "3 todos")
              SetStatusItem None
              ShowPicker(
                  "Pick",
                  [ { Id = "a"
                      Title = "A"
                      Subtitle = None }
                    { Id = "b"
                      Title = "B"
                      Subtitle = Some "second" } ],
                  "picked"
              )
              PromptInput("Name", "draft", "answer") ]

        let json1 = actionsToJson sample
        let round = actionsFromJson json1
        let json2 = actionsToJson round
        json1 = json2 && List.length round = List.length sample
