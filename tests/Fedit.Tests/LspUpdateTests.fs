module Fedit.Tests.LspUpdateTests

open Fedit
open Xunit
open FsUnit.Xunit

/// Update-level coverage for the LSP document-sync chokepoint
/// (`Editor.lspSyncEffects`) and the LSP Msg handlers. Pure — no server
/// process is ever spawned; assertions inspect the emitted effects.
/// Note: no close-buffer action exists on this branch, so the Closed
/// transition is covered through the preview-slot path swap (the
/// FilePath-removed case).

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

    model

let private chr c : Chord = { Mods = Set.empty; Key = Key.Char c }

let private lspSyncsOf effects =
    effects
    |> List.collect (fun effect ->
        match effect with
        | LspSyncDocuments(_, documents) -> documents
        | _ -> [])

let private diagnostic severity message : LspDiagnostic =
    { Range =
        { Start = { Line = 0; Character = 0 }
          End = { Line = 0; Character = 1 } }
      Severity = severity
      Message = message
      Source = Some "sema"
      Code = None }

[<Fact>]
let ``opening a file with a matching server emits an Opened sync`` () =
    let _, effects =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    match lspSyncsOf effects with
    | [ sync ] ->
        sync.Path |> should equal "/root/main.sema"
        sync.Server.Name |> should equal "sema"
        sync.LanguageId |> should equal "sema"
        sync.Version |> should equal 0

        match sync.Kind with
        | LspDocumentSyncKind.Opened text -> PieceTable.toString text |> should equal "(def x 1)"
        | other -> failwith $"expected Opened, got %A{other}"
    | other -> failwith $"expected exactly one sync entry, got %A{other}"

[<Fact>]
let ``the sync effect carries the workspace root as the root fallback`` () =
    let _, effects =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    effects
    |> List.exists (fun effect ->
        match effect with
        | LspSyncDocuments("/root", _) -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``editing a synced buffer emits Changed with Version equal to EditTick`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    let edited, effects = Editor.update (KeyPressed(chr 'y')) opened
    let buffer = edited.Editors.Buffers[edited.Editors.ActiveBufferId]
    buffer.EditTick |> should equal 1

    match lspSyncsOf effects with
    | [ sync ] ->
        sync.Version |> should equal buffer.EditTick

        match sync.Kind with
        | LspDocumentSyncKind.Changed text -> PieceTable.toString text |> should equal "y(def x 1)"
        | other -> failwith $"expected Changed, got %A{other}"
    | other -> failwith $"expected exactly one sync entry, got %A{other}"

[<Fact>]
let ``an unmatched extension emits no sync`` () =
    let _, effects =
        Editor.update (FileOpened("/root/notes.txt", OpenPermanent, None, Result.Ok "hello")) (initModel ())

    lspSyncsOf effects |> List.isEmpty |> should equal true

[<Fact>]
let ``a disabled server emits no sync`` () =
    let model = initModel ()

    let disabled =
        { model with
            Config =
                { model.Config with
                    DisabledLanguageServers = Set.ofList [ "sema" ] } }

    let _, effects =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) disabled

    lspSyncsOf effects |> List.isEmpty |> should equal true

[<Fact>]
let ``re-opening an already-open file emits no sync`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    let _, effects =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) opened

    lspSyncsOf effects |> List.isEmpty |> should equal true

[<Fact>]
let ``preview slot reuse closes the old document and opens the new`` () =
    let previewed, _ =
        Editor.update (FileOpened("/root/a.sema", OpenPreview, None, Result.Ok "(a)")) (initModel ())

    let _, effects =
        Editor.update (FileOpened("/root/b.sema", OpenPreview, None, Result.Ok "(b)")) previewed

    match lspSyncsOf effects with
    | [ closed; opened ] ->
        closed.Path |> should equal "/root/a.sema"
        closed.Kind |> should equal LspDocumentSyncKind.Closed
        opened.Path |> should equal "/root/b.sema"

        match opened.Kind with
        | LspDocumentSyncKind.Opened text -> PieceTable.toString text |> should equal "(b)"
        | other -> failwith $"expected Opened, got %A{other}"
    | other -> failwith $"expected a Closed then an Opened entry, got %A{other}"

[<Fact>]
let ``a scratch buffer gaining a path on save emits Opened`` () =
    // BufferSaved with the current EditTick is how save-as lands: markSaved
    // assigns the FilePath, and the path-diff sees a document appear.
    let _, effects =
        Editor.update (BufferSaved(1, "/root/fresh.sema", 0, Result.Ok())) (initModel ())

    match lspSyncsOf effects with
    | [ sync ] ->
        sync.Path |> should equal "/root/fresh.sema"

        match sync.Kind with
        | LspDocumentSyncKind.Opened _ -> ()
        | other -> failwith $"expected Opened, got %A{other}"
    | other -> failwith $"expected exactly one sync entry, got %A{other}"

[<Fact>]
let ``LspServerStatusChanged lands in the model`` () =
    let next, effects =
        Editor.update (LspServerStatusChanged("sema", LspServerStatus.Running)) (initModel ())

    next.Lsp.Servers["sema"] |> should equal LspServerStatus.Running
    lspSyncsOf effects |> List.isEmpty |> should equal true

[<Fact>]
let ``LspDiagnosticsPublished lands in the model and the status segment counts it`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    let published, _ =
        Editor.update
            (LspDiagnosticsPublished(
                "/root/main.sema",
                [ diagnostic LspDiagnosticSeverity.Error "unknown symbol"
                  diagnostic LspDiagnosticSeverity.Error "arity mismatch"
                  diagnostic LspDiagnosticSeverity.Warning "unused binding" ]
            ))
            opened

    published.Lsp.Diagnostics["/root/main.sema"] |> List.length |> should equal 3

    let withFormat =
        { published with
            Config =
                { published.Config with
                    StatusFormat = "[DIAGNOSTICS]" } }

    (Status.render 20 withFormat).Trim() |> should equal "E2 W1"

[<Fact>]
let ``an empty diagnostics publish removes the path entry`` () =
    let published, _ =
        Editor.update
            (LspDiagnosticsPublished("/root/main.sema", [ diagnostic LspDiagnosticSeverity.Error "boom" ]))
            (initModel ())

    let cleared, _ =
        Editor.update (LspDiagnosticsPublished("/root/main.sema", [])) published

    cleared.Lsp.Diagnostics.ContainsKey "/root/main.sema" |> should equal false

[<Fact>]
let ``the diagnostics segment is empty for a buffer without diagnostics`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    let withFormat =
        { opened with
            Config =
                { opened.Config with
                    StatusFormat = "x[DIAGNOSTICS]y" } }

    Status.render 10 withFormat |> should haveSubstring "xy"
