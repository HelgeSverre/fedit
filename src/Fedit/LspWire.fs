namespace Fedit

// FS3261: System.Text.Json's GetString() returns a nullable string; the reads
// below guard ValueKind first, so they are total in practice.
#nowarn "3261"

open System
open System.IO
open System.Text
open System.Text.Json

/// Classification of one JSON-RPC message read off a language server's
/// stdout. The carried JsonElements point into the caller's JsonDocument —
/// decode them before disposing it.
[<RequireQualifiedAccess>]
type LspIncomingMessage =
    /// Reply to one of our requests: request id + result element, or the
    /// server's error message.
    | Response of id: int * outcome: Result<JsonElement, string>
    /// Server-initiated notification, e.g. textDocument/publishDiagnostics.
    | Notification of methodName: string * parameters: JsonElement option
    /// Server-initiated request that expects a reply. RawId is the id's raw
    /// JSON text (number or string), so a reply can splice it back verbatim.
    | Request of rawId: string * methodName: string * parameters: JsonElement option

/// JSON-RPC 2.0 wire format for the editor <-> language-server boundary.
/// Pure string/JSON — no I/O; framing and process plumbing live in the LSP
/// client layer. Hand-rolled on Utf8JsonWriter + JsonDocument (zero
/// reflection) so it runs under NativeAOT, same as PluginWire.
[<RequireQualifiedAccess>]
module LspWire =

    // ---- writers (Utf8JsonWriter) -----------------------------------------

    let private toJson (write: Utf8JsonWriter -> unit) : string =
        use ms = new MemoryStream()

        (use w = new Utf8JsonWriter(ms)
         write w
         w.Flush())

        Encoding.UTF8.GetString(ms.ToArray())

    let private envelope (write: Utf8JsonWriter -> unit) : string =
        toJson (fun w ->
            w.WriteStartObject()
            w.WriteString("jsonrpc", "2.0")
            write w
            w.WriteEndObject())

    let private writePosition (w: Utf8JsonWriter) (name: string) (position: LspPosition) =
        w.WritePropertyName name
        w.WriteStartObject()
        w.WriteNumber("line", position.Line)
        w.WriteNumber("character", position.Character)
        w.WriteEndObject()

    let private writeTextDocumentIdentifier (w: Utf8JsonWriter) (uri: string) =
        w.WritePropertyName "textDocument"
        w.WriteStartObject()
        w.WriteString("uri", uri)
        w.WriteEndObject()

    let private writeEmptyObject (w: Utf8JsonWriter) (name: string) =
        w.WritePropertyName name
        w.WriteStartObject()
        w.WriteEndObject()

    /// initialize request: process id, workspace root, and the minimal client
    /// capabilities fedit consumes (plaintext-preferred hover, definition,
    /// references, published diagnostics; full-text sync).
    let initializeRequest (id: int) (processId: int) (rootUri: string) : string =
        envelope (fun w ->
            w.WriteNumber("id", id)
            w.WriteString("method", "initialize")
            w.WritePropertyName "params"
            w.WriteStartObject()
            w.WriteNumber("processId", processId)
            w.WritePropertyName "clientInfo"
            w.WriteStartObject()
            w.WriteString("name", "fedit")
            w.WriteEndObject()
            w.WriteString("rootUri", rootUri)
            w.WritePropertyName "capabilities"
            w.WriteStartObject()
            w.WritePropertyName "textDocument"
            w.WriteStartObject()
            w.WritePropertyName "synchronization"
            w.WriteStartObject()
            w.WriteBoolean("dynamicRegistration", false)
            w.WriteEndObject()
            w.WritePropertyName "hover"
            w.WriteStartObject()
            w.WritePropertyName "contentFormat"
            w.WriteStartArray()
            w.WriteStringValue "plaintext"
            w.WriteStringValue "markdown"
            w.WriteEndArray()
            w.WriteEndObject()
            writeEmptyObject w "definition"
            writeEmptyObject w "references"
            writeEmptyObject w "publishDiagnostics"
            w.WriteEndObject() // textDocument
            w.WriteEndObject() // capabilities
            w.WriteEndObject()) // params

    let initializedNotification: string =
        envelope (fun w ->
            w.WriteString("method", "initialized")
            writeEmptyObject w "params")

    /// textDocument/didOpen with the buffer's full (LF-normalized) text.
    let didOpenNotification (uri: string) (languageId: string) (version: int) (text: string) : string =
        envelope (fun w ->
            w.WriteString("method", "textDocument/didOpen")
            w.WritePropertyName "params"
            w.WriteStartObject()
            w.WritePropertyName "textDocument"
            w.WriteStartObject()
            w.WriteString("uri", uri)
            w.WriteString("languageId", languageId)
            w.WriteNumber("version", version)
            w.WriteString("text", text)
            w.WriteEndObject()
            w.WriteEndObject())

    /// textDocument/didChange carrying the full new text as a single
    /// content change (TextDocumentSyncKind.Full).
    let didChangeNotification (uri: string) (version: int) (text: string) : string =
        envelope (fun w ->
            w.WriteString("method", "textDocument/didChange")
            w.WritePropertyName "params"
            w.WriteStartObject()
            w.WritePropertyName "textDocument"
            w.WriteStartObject()
            w.WriteString("uri", uri)
            w.WriteNumber("version", version)
            w.WriteEndObject()
            w.WritePropertyName "contentChanges"
            w.WriteStartArray()
            w.WriteStartObject()
            w.WriteString("text", text)
            w.WriteEndObject()
            w.WriteEndArray()
            w.WriteEndObject())

    let didCloseNotification (uri: string) : string =
        envelope (fun w ->
            w.WriteString("method", "textDocument/didClose")
            w.WritePropertyName "params"
            w.WriteStartObject()
            writeTextDocumentIdentifier w uri
            w.WriteEndObject())

    let shutdownRequest (id: int) : string =
        envelope (fun w ->
            w.WriteNumber("id", id)
            w.WriteString("method", "shutdown")
            w.WriteNull "params")

    let exitNotification: string =
        envelope (fun w ->
            w.WriteString("method", "exit")
            w.WriteNull "params")

    /// Error reply for a server->client request fedit does not implement
    /// (JSON-RPC MethodNotFound, -32601). Some servers stall until every
    /// request is answered. `rawId` is the id's raw JSON text from
    /// `classifyMessage`, spliced back verbatim (number or string).
    let methodNotFoundResponse (rawId: string) (methodName: string) : string =
        envelope (fun w ->
            w.WritePropertyName "id"
            w.WriteRawValue rawId
            w.WritePropertyName "error"
            w.WriteStartObject()
            w.WriteNumber("code", -32601)
            w.WriteString("message", "method not found: " + methodName)
            w.WriteEndObject())

    let private textDocumentPositionRequest (id: int) (methodName: string) (uri: string) (position: LspPosition) =
        envelope (fun w ->
            w.WriteNumber("id", id)
            w.WriteString("method", methodName)
            w.WritePropertyName "params"
            w.WriteStartObject()
            writeTextDocumentIdentifier w uri
            writePosition w "position" position
            w.WriteEndObject())

    let definitionRequest (id: int) (uri: string) (position: LspPosition) : string =
        textDocumentPositionRequest id "textDocument/definition" uri position

    let hoverRequest (id: int) (uri: string) (position: LspPosition) : string =
        textDocumentPositionRequest id "textDocument/hover" uri position

    /// textDocument/references — always asks for the declaration too.
    let referencesRequest (id: int) (uri: string) (position: LspPosition) : string =
        envelope (fun w ->
            w.WriteNumber("id", id)
            w.WriteString("method", "textDocument/references")
            w.WritePropertyName "params"
            w.WriteStartObject()
            writeTextDocumentIdentifier w uri
            writePosition w "position" position
            w.WritePropertyName "context"
            w.WriteStartObject()
            w.WriteBoolean("includeDeclaration", true)
            w.WriteEndObject()
            w.WriteEndObject())

    // ---- readers (JsonDocument / JsonElement) -----------------------------

    /// Classify one message from the server: a response to one of our
    /// requests, a server notification, or a server request expecting a
    /// reply. The returned elements share the caller's JsonDocument.
    let classifyMessage (root: JsonElement) : LspIncomingMessage =
        if root.ValueKind <> JsonValueKind.Object then
            LspIncomingMessage.Response(-1, Result.Error "not a JSON-RPC message")
        else
            let parameters =
                match root.TryGetProperty "params" with
                | true, p when p.ValueKind <> JsonValueKind.Null -> Some p
                | _ -> None

            match root.TryGetProperty "method" with
            | true, m when m.ValueKind = JsonValueKind.String ->
                let methodName = m.GetString()

                match root.TryGetProperty "id" with
                | true, id when id.ValueKind <> JsonValueKind.Null ->
                    LspIncomingMessage.Request(id.GetRawText(), methodName, parameters)
                | _ -> LspIncomingMessage.Notification(methodName, parameters)
            | _ ->
                let id =
                    match root.TryGetProperty "id" with
                    | true, idElement when idElement.ValueKind = JsonValueKind.Number -> idElement.GetInt32()
                    | _ -> -1

                match root.TryGetProperty "error" with
                | true, errorElement when errorElement.ValueKind = JsonValueKind.Object ->
                    let message =
                        match errorElement.TryGetProperty "message" with
                        | true, m when m.ValueKind = JsonValueKind.String -> m.GetString()
                        | _ -> "unknown error"

                    let code =
                        match errorElement.TryGetProperty "code" with
                        | true, c when c.ValueKind = JsonValueKind.Number -> sprintf " (code %d)" (c.GetInt32())
                        | _ -> ""

                    LspIncomingMessage.Response(id, Result.Error(message + code))
                | _ ->
                    match root.TryGetProperty "result" with
                    | true, result -> LspIncomingMessage.Response(id, Result.Ok result)
                    | _ -> LspIncomingMessage.Response(id, Result.Error "response carries neither result nor error")

    let private syncKindOfInt (value: int) : LspTextDocumentSyncKind =
        match value with
        | 1 -> LspTextDocumentSyncKind.Full
        | 2 -> LspTextDocumentSyncKind.Incremental
        | _ -> LspTextDocumentSyncKind.None

    // A provider capability may be a bool or an options object; an object
    // means the provider is present.
    let private providerEnabled (capabilities: JsonElement) (name: string) : bool =
        match capabilities.TryGetProperty name with
        | true, p -> p.ValueKind = JsonValueKind.True || p.ValueKind = JsonValueKind.Object
        | _ -> false

    /// Decode the capabilities subset we consume from an InitializeResult.
    let readInitializeResult (result: JsonElement) : LspServerCapabilities =
        if result.ValueKind <> JsonValueKind.Object then
            LspServerCapabilities.none
        else
            match result.TryGetProperty "capabilities" with
            | true, capabilities when capabilities.ValueKind = JsonValueKind.Object ->
                let sync =
                    match capabilities.TryGetProperty "textDocumentSync" with
                    | true, s when s.ValueKind = JsonValueKind.Number -> syncKindOfInt (s.GetInt32())
                    | true, s when s.ValueKind = JsonValueKind.Object ->
                        match s.TryGetProperty "change" with
                        | true, change when change.ValueKind = JsonValueKind.Number -> syncKindOfInt (change.GetInt32())
                        | _ -> LspTextDocumentSyncKind.None
                    | _ -> LspTextDocumentSyncKind.None

                { TextDocumentSync = sync
                  DefinitionProvider = providerEnabled capabilities "definitionProvider"
                  ReferencesProvider = providerEnabled capabilities "referencesProvider"
                  HoverProvider = providerEnabled capabilities "hoverProvider" }
            | _ -> LspServerCapabilities.none

    let private readPosition (e: JsonElement) : LspPosition =
        { Line = e.GetProperty("line").GetInt32()
          Character = e.GetProperty("character").GetInt32() }

    let private readRange (e: JsonElement) : LspRange =
        { Start = readPosition (e.GetProperty "start")
          End = readPosition (e.GetProperty "end") }

    // A definition/references result element: a Location {uri, range} or a
    // LocationLink {targetUri, targetRange, targetSelectionRange}. For links,
    // prefer targetSelectionRange (the symbol-name span) over targetRange
    // (the whole declaration).
    let private readLocationOrLink (e: JsonElement) : LspLocation option =
        if e.ValueKind <> JsonValueKind.Object then
            None
        else
            match e.TryGetProperty "targetUri" with
            | true, uri when uri.ValueKind = JsonValueKind.String ->
                let range =
                    match e.TryGetProperty "targetSelectionRange" with
                    | true, r when r.ValueKind = JsonValueKind.Object -> readRange r
                    | _ -> readRange (e.GetProperty "targetRange")

                Some { Uri = uri.GetString(); Range = range }
            | _ ->
                match e.TryGetProperty "uri" with
                | true, uri when uri.ValueKind = JsonValueKind.String ->
                    Some
                        { Uri = uri.GetString()
                          Range = readRange (e.GetProperty "range") }
                | _ -> None

    /// Normalize a definition or references result — `Location | Location[]
    /// | LocationLink[] | null` — to a flat location list.
    let readLocations (result: JsonElement) : LspLocation list =
        match result.ValueKind with
        | JsonValueKind.Array -> result.EnumerateArray() |> Seq.choose readLocationOrLink |> List.ofSeq
        | JsonValueKind.Object -> readLocationOrLink result |> Option.toList
        | _ -> []

    // Hover contents piece: MarkupContent {kind, value}, a bare string, or a
    // {language, value} MarkedString.
    let private hoverPieceText (e: JsonElement) : string =
        match e.ValueKind with
        | JsonValueKind.String -> e.GetString()
        | JsonValueKind.Object ->
            match e.TryGetProperty "value" with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        | _ -> ""

    let private toPlainLines (text: string) : string seq =
        text.Replace("\r\n", "\n").Split '\n'
        |> Seq.filter (fun line -> not (line.TrimStart().StartsWith "```"))

    /// Flatten a textDocument/hover result to plain-text lines. Handles
    /// `MarkupContent`, a bare `MarkedString`, and `MarkedString[]`; strips
    /// markdown code-fence markers and leading/trailing blank lines.
    let readHoverResult (result: JsonElement) : string list =
        let lines =
            if result.ValueKind <> JsonValueKind.Object then
                []
            else
                match result.TryGetProperty "contents" with
                | true, contents when contents.ValueKind = JsonValueKind.Array ->
                    contents.EnumerateArray()
                    |> Seq.collect (hoverPieceText >> toPlainLines)
                    |> List.ofSeq
                | true, contents -> toPlainLines (hoverPieceText contents) |> List.ofSeq
                | _ -> []

        let isBlank (line: string) = String.IsNullOrWhiteSpace line

        lines
        |> List.skipWhile isBlank
        |> List.rev
        |> List.skipWhile isBlank
        |> List.rev

    let private readSeverity (e: JsonElement) : LspDiagnosticSeverity =
        match e.TryGetProperty "severity" with
        | true, s when s.ValueKind = JsonValueKind.Number ->
            match s.GetInt32() with
            | 2 -> LspDiagnosticSeverity.Warning
            | 3 -> LspDiagnosticSeverity.Information
            | 4 -> LspDiagnosticSeverity.Hint
            | _ -> LspDiagnosticSeverity.Error
        | _ -> LspDiagnosticSeverity.Error

    let private optStringProperty (e: JsonElement) (name: string) : string option =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private readDiagnostic (e: JsonElement) : LspDiagnostic =
        let code =
            match e.TryGetProperty "code" with
            | true, c when c.ValueKind = JsonValueKind.String -> Some(c.GetString())
            | true, c when c.ValueKind = JsonValueKind.Number -> Some(c.GetRawText())
            | _ -> None

        { Range = readRange (e.GetProperty "range")
          Severity = readSeverity e
          Message = e.GetProperty("message").GetString()
          Source = optStringProperty e "source"
          Code = code }

    /// textDocument/publishDiagnostics params: document URI + diagnostics.
    let readPublishDiagnostics (parameters: JsonElement) : string * LspDiagnostic list =
        if parameters.ValueKind <> JsonValueKind.Object then
            "", []
        else
            let diagnostics =
                match parameters.TryGetProperty "diagnostics" with
                | true, d when d.ValueKind = JsonValueKind.Array -> [ for e in d.EnumerateArray() -> readDiagnostic e ]
                | _ -> []

            let uri = optStringProperty parameters "uri" |> Option.defaultValue ""
            uri, diagnostics
