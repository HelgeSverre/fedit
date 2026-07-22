module Fedit.Tests.LspWireTests

open System.Text.Json
open Fedit
open Xunit
open FsUnit.Xunit

// ---- encoders: golden envelope shapes -------------------------------------

[<Fact>]
let ``initialized notification is a fixed envelope`` () =
    LspWire.initializedNotification
    |> should equal """{"jsonrpc":"2.0","method":"initialized","params":{}}"""

[<Fact>]
let ``exit notification is a fixed envelope`` () =
    LspWire.exitNotification
    |> should equal """{"jsonrpc":"2.0","method":"exit","params":null}"""

[<Fact>]
let ``method-not-found reply splices a numeric id back verbatim`` () =
    LspWire.methodNotFoundResponse "7" "workspace/configuration"
    |> should
        equal
        """{"jsonrpc":"2.0","id":7,"error":{"code":-32601,"message":"method not found: workspace/configuration"}}"""

[<Fact>]
let ``method-not-found reply splices a string id back verbatim`` () =
    LspWire.methodNotFoundResponse "\"req-1\"" "client/registerCapability"
    |> should
        equal
        """{"jsonrpc":"2.0","id":"req-1","error":{"code":-32601,"message":"method not found: client/registerCapability"}}"""

[<Fact>]
let ``shutdown request carries the id`` () =
    LspWire.shutdownRequest 9
    |> should equal """{"jsonrpc":"2.0","id":9,"method":"shutdown","params":null}"""

[<Fact>]
let ``didClose names the document`` () =
    LspWire.didCloseNotification "file:///proj/main.sema"
    |> should
        equal
        """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///proj/main.sema"}}}"""

[<Fact>]
let ``definition request carries document + position with LSP field names`` () =
    LspWire.definitionRequest 7 "file:///proj/main.sema" { Line = 3; Character = 9 }
    |> should
        equal
        """{"jsonrpc":"2.0","id":7,"method":"textDocument/definition","params":{"textDocument":{"uri":"file:///proj/main.sema"},"position":{"line":3,"character":9}}}"""

[<Fact>]
let ``hover request differs from definition only by method`` () =
    LspWire.hoverRequest 8 "file:///proj/main.sema" { Line = 0; Character = 0 }
    |> should
        equal
        """{"jsonrpc":"2.0","id":8,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///proj/main.sema"},"position":{"line":0,"character":0}}}"""

[<Fact>]
let ``references request always includes the declaration`` () =
    LspWire.referencesRequest 11 "file:///proj/main.sema" { Line = 2; Character = 4 }
    |> should
        equal
        """{"jsonrpc":"2.0","id":11,"method":"textDocument/references","params":{"textDocument":{"uri":"file:///proj/main.sema"},"position":{"line":2,"character":4},"context":{"includeDeclaration":true}}}"""

[<Fact>]
let ``didChange sends the full text as a single content change`` () =
    LspWire.didChangeNotification "file:///proj/main.sema" 4 "(def x 1)"
    |> should
        equal
        """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///proj/main.sema","version":4},"contentChanges":[{"text":"(def x 1)"}]}}"""

[<Fact>]
let ``didOpen carries uri, languageId, version, and the exact text`` () =
    let json =
        LspWire.didOpenNotification "file:///proj/main.sema" "sema" 1 "(def x 1)\n(def y 2)\n"

    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    root.GetProperty("method").GetString() |> should equal "textDocument/didOpen"
    let textDocument = root.GetProperty("params").GetProperty "textDocument"

    textDocument.GetProperty("uri").GetString()
    |> should equal "file:///proj/main.sema"

    textDocument.GetProperty("languageId").GetString() |> should equal "sema"
    textDocument.GetProperty("version").GetInt32() |> should equal 1

    textDocument.GetProperty("text").GetString()
    |> should equal "(def x 1)\n(def y 2)\n"

[<Fact>]
let ``initialize request advertises the minimal client capabilities`` () =
    let json = LspWire.initializeRequest 1 4242 "file:///proj"
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    root.GetProperty("jsonrpc").GetString() |> should equal "2.0"
    root.GetProperty("id").GetInt32() |> should equal 1
    root.GetProperty("method").GetString() |> should equal "initialize"
    let parameters = root.GetProperty "params"
    parameters.GetProperty("processId").GetInt32() |> should equal 4242
    parameters.GetProperty("rootUri").GetString() |> should equal "file:///proj"

    parameters.GetProperty("clientInfo").GetProperty("name").GetString()
    |> should equal "fedit"

    let textDocument = parameters.GetProperty("capabilities").GetProperty "textDocument"

    [ for f in textDocument.GetProperty("hover").GetProperty("contentFormat").EnumerateArray() -> f.GetString() ]
    |> should equal [ "plaintext"; "markdown" ]

    for capability in [ "synchronization"; "definition"; "references"; "publishDiagnostics" ] do
        textDocument.GetProperty(capability).ValueKind
        |> should equal JsonValueKind.Object

// ---- classify -------------------------------------------------------------

[<Fact>]
let ``a message with result and id classifies as a response`` () =
    use doc =
        JsonDocument.Parse """{"jsonrpc":"2.0","id":3,"result":{"capabilities":{}}}"""

    match LspWire.classifyMessage doc.RootElement with
    | LspIncomingMessage.Response(id, Result.Ok result) ->
        id |> should equal 3

        result.GetProperty("capabilities").ValueKind
        |> should equal JsonValueKind.Object
    | other -> failwith $"expected an ok response, got {other}"

[<Fact>]
let ``a null result still classifies as an ok response`` () =
    use doc = JsonDocument.Parse """{"jsonrpc":"2.0","id":2,"result":null}"""

    match LspWire.classifyMessage doc.RootElement with
    | LspIncomingMessage.Response(id, Result.Ok result) ->
        id |> should equal 2
        result.ValueKind |> should equal JsonValueKind.Null
    | other -> failwith $"expected an ok response, got {other}"

[<Fact>]
let ``an error response carries the server's message and code`` () =
    use doc =
        JsonDocument.Parse """{"jsonrpc":"2.0","id":4,"error":{"code":-32601,"message":"method not found"}}"""

    match LspWire.classifyMessage doc.RootElement with
    | LspIncomingMessage.Response(id, Result.Error message) ->
        id |> should equal 4
        message |> should equal "method not found (code -32601)"
    | other -> failwith $"expected an error response, got {other}"

[<Fact>]
let ``a method without id classifies as a notification`` () =
    use doc =
        JsonDocument.Parse
            """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///a.sema","diagnostics":[]}}"""

    match LspWire.classifyMessage doc.RootElement with
    | LspIncomingMessage.Notification(methodName, Some parameters) ->
        methodName |> should equal "textDocument/publishDiagnostics"
        parameters.GetProperty("uri").GetString() |> should equal "file:///a.sema"
    | other -> failwith $"expected a notification, got {other}"

[<Fact>]
let ``a method with id classifies as a server request, id kept as raw JSON`` () =
    use withStringId =
        JsonDocument.Parse """{"jsonrpc":"2.0","id":"reg-1","method":"client/registerCapability","params":{}}"""

    match LspWire.classifyMessage withStringId.RootElement with
    | LspIncomingMessage.Request(rawId, methodName, Some _) ->
        rawId |> should equal "\"reg-1\""
        methodName |> should equal "client/registerCapability"
    | other -> failwith $"expected a server request, got {other}"

    use withNumberId =
        JsonDocument.Parse """{"jsonrpc":"2.0","id":5,"method":"workspace/configuration","params":{"items":[]}}"""

    match LspWire.classifyMessage withNumberId.RootElement with
    | LspIncomingMessage.Request(rawId, methodName, Some _) ->
        rawId |> should equal "5"
        methodName |> should equal "workspace/configuration"
    | other -> failwith $"expected a server request, got {other}"

// ---- InitializeResult capabilities ----------------------------------------

[<Fact>]
let ``sema-shaped InitializeResult decodes full sync and all providers`` () =
    use doc =
        JsonDocument.Parse
            """{"capabilities":{"textDocumentSync":1,"definitionProvider":true,"referencesProvider":true,"hoverProvider":true,"completionProvider":{"triggerCharacters":["("]},"renameProvider":true},"serverInfo":{"name":"sema-lsp","version":"1.30.0"}}"""

    LspWire.readInitializeResult doc.RootElement
    |> should
        equal
        { TextDocumentSync = LspTextDocumentSyncKind.Full
          DefinitionProvider = true
          ReferencesProvider = true
          HoverProvider = true }

[<Fact>]
let ``object-form sync and object-form providers decode`` () =
    use doc =
        JsonDocument.Parse
            """{"capabilities":{"textDocumentSync":{"openClose":true,"change":2},"hoverProvider":{"workDoneProgress":false}}}"""

    LspWire.readInitializeResult doc.RootElement
    |> should
        equal
        { TextDocumentSync = LspTextDocumentSyncKind.Incremental
          DefinitionProvider = false
          ReferencesProvider = false
          HoverProvider = true }

[<Fact>]
let ``missing or null capabilities decode to none`` () =
    use empty = JsonDocument.Parse "{}"

    LspWire.readInitializeResult empty.RootElement
    |> should equal LspServerCapabilities.none

    use nullResult = JsonDocument.Parse "null"

    LspWire.readInitializeResult nullResult.RootElement
    |> should equal LspServerCapabilities.none

// ---- definition / references locations ------------------------------------

let private location uri startLine startCharacter endLine endCharacter =
    { Uri = uri
      Range =
        { Start =
            { Line = startLine
              Character = startCharacter }
          End =
            { Line = endLine
              Character = endCharacter } } }

[<Fact>]
let ``a bare Location result decodes to a one-element list`` () =
    use doc =
        JsonDocument.Parse
            """{"uri":"file:///proj/lib.sema","range":{"start":{"line":4,"character":6},"end":{"line":4,"character":11}}}"""

    LspWire.readLocations doc.RootElement
    |> should equal [ location "file:///proj/lib.sema" 4 6 4 11 ]

[<Fact>]
let ``a Location array decodes in order`` () =
    use doc =
        JsonDocument.Parse
            """[{"uri":"file:///proj/lib.sema","range":{"start":{"line":4,"character":6},"end":{"line":4,"character":11}}},{"uri":"file:///proj/main.sema","range":{"start":{"line":10,"character":1},"end":{"line":10,"character":6}}}]"""

    LspWire.readLocations doc.RootElement
    |> should
        equal
        [ location "file:///proj/lib.sema" 4 6 4 11
          location "file:///proj/main.sema" 10 1 10 6 ]

[<Fact>]
let ``a LocationLink array prefers targetSelectionRange`` () =
    use doc =
        JsonDocument.Parse
            """[{"originSelectionRange":{"start":{"line":9,"character":2},"end":{"line":9,"character":7}},"targetUri":"file:///proj/lib.sema","targetRange":{"start":{"line":2,"character":0},"end":{"line":8,"character":1}},"targetSelectionRange":{"start":{"line":2,"character":6},"end":{"line":2,"character":11}}}]"""

    LspWire.readLocations doc.RootElement
    |> should equal [ location "file:///proj/lib.sema" 2 6 2 11 ]

[<Fact>]
let ``a LocationLink without targetSelectionRange falls back to targetRange`` () =
    use doc =
        JsonDocument.Parse
            """[{"targetUri":"file:///proj/lib.sema","targetRange":{"start":{"line":2,"character":0},"end":{"line":8,"character":1}}}]"""

    LspWire.readLocations doc.RootElement
    |> should equal [ location "file:///proj/lib.sema" 2 0 8 1 ]

[<Fact>]
let ``a null definition result decodes to an empty list`` () =
    use doc = JsonDocument.Parse "null"
    LspWire.readLocations doc.RootElement |> should equal List.empty<LspLocation>

// ---- hover ----------------------------------------------------------------

[<Fact>]
let ``MarkupContent hover strips markdown fences to plain lines`` () =
    use doc =
        JsonDocument.Parse
            """{"contents":{"kind":"markdown","value":"```sema\n(defn greet [name])\n```\nGreets a person."},"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":6}}}"""

    LspWire.readHoverResult doc.RootElement
    |> should equal [ "(defn greet [name])"; "Greets a person." ]

[<Fact>]
let ``a bare MarkedString hover decodes to one line`` () =
    use doc = JsonDocument.Parse """{"contents":"a plain hover"}"""
    LspWire.readHoverResult doc.RootElement |> should equal [ "a plain hover" ]

[<Fact>]
let ``a MarkedString array flattens language blocks and text sections`` () =
    use doc =
        JsonDocument.Parse """{"contents":[{"language":"sema","value":"(defn foo [x])"},"Second section."]}"""

    LspWire.readHoverResult doc.RootElement
    |> should equal [ "(defn foo [x])"; "Second section." ]

[<Fact>]
let ``null and blank hovers decode to no lines`` () =
    use nullResult = JsonDocument.Parse "null"

    LspWire.readHoverResult nullResult.RootElement
    |> should equal List.empty<string>

    use blank =
        JsonDocument.Parse """{"contents":{"kind":"markdown","value":"```\n```\n\n"}}"""

    LspWire.readHoverResult blank.RootElement |> should equal List.empty<string>

// ---- publishDiagnostics ---------------------------------------------------

[<Fact>]
let ``publishDiagnostics decodes uri, severities, optional source and code`` () =
    use doc =
        JsonDocument.Parse
            """{"uri":"file:///proj/main.sema","version":3,"diagnostics":[{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":9}},"severity":1,"code":"E011","source":"sema","message":"unresolved symbol `frobnicate`"},{"range":{"start":{"line":5,"character":0},"end":{"line":5,"character":4}},"severity":2,"message":"unused binding"},{"range":{"start":{"line":7,"character":0},"end":{"line":7,"character":1}},"severity":3,"code":404,"message":"note"},{"range":{"start":{"line":8,"character":0},"end":{"line":8,"character":1}},"message":"no severity given"}]}"""

    let uri, diagnostics = LspWire.readPublishDiagnostics doc.RootElement
    uri |> should equal "file:///proj/main.sema"
    List.length diagnostics |> should equal 4

    diagnostics.[0]
    |> should
        equal
        { Range =
            { Start = { Line = 1; Character = 2 }
              End = { Line = 1; Character = 9 } }
          Severity = LspDiagnosticSeverity.Error
          Message = "unresolved symbol `frobnicate`"
          Source = Some "sema"
          Code = Some "E011" }

    diagnostics.[1].Severity |> should equal LspDiagnosticSeverity.Warning
    diagnostics.[1].Source |> should equal (Option<string>.None)
    diagnostics.[1].Code |> should equal (Option<string>.None)
    diagnostics.[2].Severity |> should equal LspDiagnosticSeverity.Information
    diagnostics.[2].Code |> should equal (Some "404")
    diagnostics.[3].Severity |> should equal LspDiagnosticSeverity.Error

[<Fact>]
let ``an empty diagnostics list decodes to an empty list`` () =
    use doc = JsonDocument.Parse """{"uri":"file:///proj/main.sema","diagnostics":[]}"""

    LspWire.readPublishDiagnostics doc.RootElement
    |> should equal ("file:///proj/main.sema", List.empty<LspDiagnostic>)

// ---- path <-> URI ---------------------------------------------------------

[<Fact>]
let ``fromPath percent-encodes spaces`` () =
    LspUri.fromPath "/Users/helge/my file.fs"
    |> should equal "file:///Users/helge/my%20file.fs"

[<Fact>]
let ``paths with spaces, unicode, and emoji round-trip`` () =
    for path in
        [ "/Users/helge/my file.fs"
          "/Users/helge/æøå/blåbær.sema"
          "/tmp/emoji 🚀/x.fs"
          "/proj/100%/notes.md" ] do
        LspUri.toPath (LspUri.fromPath path) |> should equal (Some path)

[<Fact>]
let ``windows drive paths gain the standard leading slash and round-trip`` () =
    LspUri.fromPath "C:/proj/a.sema" |> should equal "file:///C%3A/proj/a.sema"

    LspUri.toPath (LspUri.fromPath "C:/proj/a.sema")
    |> should equal (Some "C:/proj/a.sema")

[<Fact>]
let ``toPath handles plain and encoded drive colons`` () =
    LspUri.toPath "file:///C:/Users/helge/x.fs"
    |> should equal (Some "C:/Users/helge/x.fs")

    LspUri.toPath "file:///c%3A/proj/a.sema" |> should equal (Some "c:/proj/a.sema")

[<Fact>]
let ``toPath drops an authority component`` () =
    LspUri.toPath "file://localhost/tmp/x" |> should equal (Some "/tmp/x")

[<Fact>]
let ``toPath rejects non-file URIs`` () =
    LspUri.toPath "untitled:Untitled-1" |> should equal (Option<string>.None)
    LspUri.toPath "https://example.com/a.sema" |> should equal (Option<string>.None)

// ---- position identity ----------------------------------------------------

[<Fact>]
let ``LspPosition maps to and from fedit Position as the identity`` () =
    let position: Position = { Line = 12; Column = 34 }
    let lspPosition = LspPosition.ofPosition position
    lspPosition |> should equal { Line = 12; Character = 34 }
    LspPosition.toPosition lspPosition |> should equal position
