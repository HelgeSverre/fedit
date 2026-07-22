module Fedit.Tests.LspUpdateTests

open Fedit
open Fedit.PromptTypes
open Xunit
open FsUnit.Xunit

/// Update-level coverage for the LSP document-sync chokepoint
/// (`Editor.lspSyncEffects`) and the LSP Msg handlers. Pure — no server
/// process is ever spawned; assertions inspect the emitted effects.
/// The Closed transition is covered both through the preview-slot path
/// swap (the FilePath-removed case) and through close-buffer directly
/// (the integration seam with the unified quit/close work).

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
let ``LspServerStatusChanged lands in the model keyed by client key`` () =
    let next, effects =
        Editor.update (LspServerStatusChanged("sema@/root", LspServerStatus.Running)) (initModel ())

    next.Lsp.Servers["sema@/root"] |> should equal LspServerStatus.Running
    lspSyncsOf effects |> List.isEmpty |> should equal true

    LspState.statusLabel Set.empty next.Lsp "sema" |> should equal "running"

[<Fact>]
let ``statusLabel aggregates per-root clients worst-status-wins`` () =
    // One server name can run several clients (one per workspace root); a
    // dead client must never be masked by a healthy sibling.
    let state =
        { LspState.empty with
            Servers =
                Map.ofList
                    [ "sema@/project-a", LspServerStatus.Running
                      "sema@/project-b", LspServerStatus.Failed "spawn failed" ] }

    LspState.statusLabel Set.empty state "sema" |> should equal "failed"

    LspState.statusLabel (Set.ofList [ "sema" ]) state "sema"
    |> should equal "disabled"

    LspState.statusLabel Set.empty state "rust" |> should equal "idle"

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
let ``a diagnostics publish for a disabled server's file is dropped`` () =
    // A publish enqueued by the reader thread before `:lsp disable` landed
    // must not resurrect diagnostics the disable purge removed — the dead
    // server would never send the clearing empty set.
    let model = initModel ()

    let disabled =
        { model with
            Config =
                { model.Config with
                    DisabledLanguageServers = Set.ofList [ "sema" ] } }

    let next, _ =
        Editor.update
            (LspDiagnosticsPublished("/root/main.sema", [ diagnostic LspDiagnosticSeverity.Error "stale" ]))
            disabled

    next.Lsp.Diagnostics.ContainsKey "/root/main.sema" |> should equal false

[<Fact>]
let ``a disabled server does not shadow an enabled server for the same file type`` () =
    // The disabled set subtracts BEFORE extension matching: with the
    // built-in sema disabled, an enabled user server claiming .sema files
    // must own them (previously the match hit the disabled entry first and
    // the whole file type silently lost its LSP).
    let model = initModel ()

    let alternate =
        { Name = "sema-alt"
          Command = "sema-alt"
          Args = []
          FileTypes = [ "sema" ]
          RootMarkers = [ "sema.toml" ] }

    let configured =
        { model with
            Config =
                { model.Config with
                    LanguageServers = model.Config.LanguageServers @ [ alternate ]
                    DisabledLanguageServers = Set.ofList [ "sema" ] } }

    let _, effects =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) configured

    match lspSyncsOf effects with
    | [ sync ] -> sync.Server.Name |> should equal "sema-alt"
    | other -> failwith $"expected exactly one sync entry, got %A{other}"

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

// ── stage 5: navigation, jump stack, pickers, :lsp ───────────────────────

let private f12: Chord = { Mods = Set.empty; Key = Fn 12 }

let private altMinus: Chord =
    { Mods = Set.ofList [ Alt ]
      Key = Key.Char '-' }

let private enterKey: Chord = { Mods = Set.empty; Key = Named Enter }
let private escapeKey: Chord = { Mods = Set.empty; Key = Named Escape }

/// A .sema buffer opened at /root/main.sema with three known lines.
let private openedModel () =
    let model, _ =
        Editor.update
            (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)\n(def y 2)\n(use x)"))
            (initModel ())

    model

/// Open the palette, type `text`, press Enter — the full prompt route.
let private typeAndRun (text: string) model =
    let ctrlP: Chord =
        { Mods = Set.ofList [ Ctrl ]
          Key = Key.Char 'p' }

    let opened, _ = Editor.update (KeyPressed ctrlP) model

    let typed =
        text
        |> Seq.fold (fun state c -> fst (Editor.update (KeyPressed(chr c)) state)) opened

    Editor.update (KeyPressed enterKey) typed

let private location path line column : LspResolvedLocation =
    { Path = path
      Position = { Line = line; Column = column }
      Preview = "" }

[<Fact>]
let ``goto-definition emits a position request for the active buffer`` () =
    let model = openedModel ()
    let _, effects = Editor.update (KeyPressed f12) model

    match
        effects
        |> List.tryPick (fun effect ->
            match effect with
            | LspRequestDefinition request -> Some request
            | _ -> None)
    with
    | Some request ->
        request.Path |> should equal "/root/main.sema"
        request.Server.Name |> should equal "sema"
        request.BufferId |> should equal model.Editors.ActiveBufferId
        request.EditTick |> should equal 0
        request.WorkspaceRoot |> should equal "/root"
    | None -> failwith "expected an LspRequestDefinition effect"

[<Fact>]
let ``goto-definition on a scratch buffer warns instead of requesting`` () =
    let next, effects = Editor.update (KeyPressed f12) (initModel ())

    effects
    |> List.exists (fun effect ->
        match effect with
        | LspRequestDefinition _ -> true
        | _ -> false)
    |> should equal false

    next.Notification.IsSome |> should equal true

[<Fact>]
let ``a stale definition result is dropped`` () =
    let model = openedModel ()
    let bufferId = model.Editors.ActiveBufferId
    let edited, _ = Editor.update (KeyPressed(chr 'z')) model // EditTick -> 1

    let next, effects =
        Editor.update (LspDefinitionResolved(Result.Ok [ location "/root/main.sema" 1 0 ], 0, bufferId)) edited

    next.Editors.Buffers[bufferId].Cursor
    |> should equal edited.Editors.Buffers[bufferId].Cursor

    next.Prompt.Active |> should equal false
    next.JumpStack |> should equal ([]: (string * Position) list)
    effects |> List.isEmpty |> should equal true

[<Fact>]
let ``a definition result for a no-longer-active buffer is dropped`` () =
    // Switching buffers while the request is in flight makes the response
    // stale even when the requesting buffer's EditTick is unchanged (the
    // SearchCompleted convention): a late jump must never yank the view
    // away from the buffer the user moved to.
    let model = openedModel ()
    let requestingBufferId = model.Editors.ActiveBufferId

    let switched, _ =
        Editor.update (FileOpened("/root/other.sema", OpenPermanent, None, Result.Ok "(other)")) model

    switched.Editors.ActiveBufferId |> should not' (equal requestingBufferId)

    let next, effects =
        Editor.update
            (LspDefinitionResolved(Result.Ok [ location "/root/main.sema" 1 0 ], 0, requestingBufferId))
            switched

    next.Editors.ActiveBufferId |> should equal switched.Editors.ActiveBufferId

    next.Editors.Buffers[requestingBufferId].Cursor
    |> should equal switched.Editors.Buffers[requestingBufferId].Cursor

    next.JumpStack |> should equal ([]: (string * Position) list)
    effects |> List.isEmpty |> should equal true

[<Fact>]
let ``a hover result for a no-longer-active buffer is dropped`` () =
    let model = openedModel ()
    let requestingBufferId = model.Editors.ActiveBufferId

    let switched, _ =
        Editor.update (FileOpened("/root/other.sema", OpenPermanent, None, Result.Ok "(other)")) model

    let next, _ =
        Editor.update (LspHoverResolved(Result.Ok [ "about main.sema" ], 0, requestingBufferId)) switched

    next.Lsp.Panel |> should equal (None: LspInfoPanel option)

[<Fact>]
let ``a single definition in the same file moves the cursor and pushes the jump origin`` () =
    let model = openedModel ()
    let bufferId = model.Editors.ActiveBufferId

    let next, _ =
        Editor.update (LspDefinitionResolved(Result.Ok [ location "/root/main.sema" 1 1 ], 0, bufferId)) model

    next.Editors.Buffers[bufferId].Cursor |> should equal { Line = 1; Column = 1 }
    next.JumpStack |> should equal [ "/root/main.sema", { Line = 0; Column = 0 } ]

[<Fact>]
let ``jump-back returns to the recorded origin and pops the stack`` () =
    let model = openedModel ()
    let bufferId = model.Editors.ActiveBufferId

    let jumped, _ =
        Editor.update (LspDefinitionResolved(Result.Ok [ location "/root/main.sema" 2 3 ], 0, bufferId)) model

    let back, _ = Editor.update (KeyPressed altMinus) jumped
    back.Editors.Buffers[bufferId].Cursor |> should equal { Line = 0; Column = 0 }
    back.JumpStack |> should equal ([]: (string * Position) list)

[<Fact>]
let ``a definition in another file loads it with the target position`` () =
    let model = openedModel ()

    let next, effects =
        Editor.update
            (LspDefinitionResolved(Result.Ok [ location "/root/lib.sema" 4 2 ], 0, model.Editors.ActiveBufferId))
            model

    effects
    |> List.exists (fun effect ->
        match effect with
        | LoadFile("/root/lib.sema", OpenPermanent, Some { Line = 4; Column = 2 }) -> true
        | _ -> false)
    |> should equal true

    next.JumpStack |> should equal [ "/root/main.sema", { Line = 0; Column = 0 } ]

[<Fact>]
let ``multiple definitions open the location picker with previews from open buffers`` () =
    let model = openedModel ()

    let next, _ =
        Editor.update
            (LspDefinitionResolved(
                Result.Ok [ location "/root/main.sema" 0 5; location "/root/main.sema" 1 5 ],
                0,
                model.Editors.ActiveBufferId
            ))
            model

    next.Prompt.Active |> should equal true
    next.Prompt.Session |> should equal PromptSessionKind.LocationsSession

    match next.Lsp.Locations with
    | Some set ->
        set.Title |> should equal "Definitions"

        set.Entries
        |> List.map (fun entry -> entry.Preview)
        |> should equal [ "(def x 1)"; "(def y 2)" ]
    | None -> failwith "expected a location set"

[<Fact>]
let ``Enter in the location picker jumps to the selected entry and closes it`` () =
    let model = openedModel ()
    let bufferId = model.Editors.ActiveBufferId

    let picker, _ =
        Editor.update
            (LspReferencesResolved(
                Result.Ok [ location "/root/main.sema" 2 1; location "/root/main.sema" 0 0 ],
                0,
                bufferId
            ))
            model

    picker.Lsp.Locations
    |> Option.map (fun set -> set.Title)
    |> should equal (Some "References")

    let after, _ = Editor.update (KeyPressed enterKey) picker
    after.Prompt.Active |> should equal false
    after.Editors.Buffers[bufferId].Cursor |> should equal { Line = 2; Column = 1 }
    after.JumpStack |> should equal [ "/root/main.sema", { Line = 0; Column = 0 } ]

[<Fact>]
let ``an empty definition result only notifies`` () =
    let model = openedModel ()

    let next, effects =
        Editor.update (LspDefinitionResolved(Result.Ok [], 0, model.Editors.ActiveBufferId)) model

    next.Prompt.Active |> should equal false
    next.Notification.IsSome |> should equal true
    effects |> List.isEmpty |> should equal true

[<Fact>]
let ``hover text lands in the dock panel and the next keypress dismisses it`` () =
    let model = openedModel ()

    let hovered, _ =
        Editor.update (LspHoverResolved(Result.Ok [ "(def x 1)"; "a binding" ], 0, model.Editors.ActiveBufferId)) model

    hovered.Lsp.Panel
    |> Option.map (fun panel -> panel.Title)
    |> should equal (Some "Hover")

    let after, _ = Editor.update (KeyPressed(chr 'j')) hovered
    after.Lsp.Panel |> should equal (None: LspInfoPanel option)
    // the keypress still performed its normal action (an edit)
    after.Editors.Buffers[model.Editors.ActiveBufferId].EditTick |> should equal 1

[<Fact>]
let ``escape only dismisses the hover panel`` () =
    let model = openedModel ()

    let hovered, _ =
        Editor.update (LspHoverResolved(Result.Ok [ "(def x 1)" ], 0, model.Editors.ActiveBufferId)) model

    let after, _ = Editor.update (KeyPressed escapeKey) hovered
    after.Lsp.Panel |> should equal (None: LspInfoPanel option)
    after.Editors.Buffers[model.Editors.ActiveBufferId].EditTick |> should equal 0

[<Fact>]
let ``escape closes the prompt even when an invisible panel is set`` () =
    // A hover response landing after the prompt opened sets the panel while
    // the dock shows the prompt (the panel is not rendered). Escape must
    // close the prompt the user is looking at — not burn a keypress on the
    // hidden panel.
    let model = openedModel ()

    let ctrlP: Chord =
        { Mods = Set.ofList [ Ctrl ]
          Key = Key.Char 'p' }

    let prompted, _ = Editor.update (KeyPressed ctrlP) model

    let hovered, _ =
        Editor.update (LspHoverResolved(Result.Ok [ "late" ], 0, model.Editors.ActiveBufferId)) prompted

    hovered.Prompt.Active |> should equal true

    let after, _ = Editor.update (KeyPressed escapeKey) hovered
    after.Prompt.Active |> should equal false
    after.Lsp.Panel |> should equal (None: LspInfoPanel option)

[<Fact>]
let ``escape cancels a pending key-sequence prefix even when a panel is set`` () =
    let model = openedModel ()

    let hovered, _ =
        Editor.update (LspHoverResolved(Result.Ok [ "info" ], 0, model.Editors.ActiveBufferId)) model

    let pending =
        { hovered with
            PendingPrefix =
                Some
                    [ { Mods = Set.ofList [ Ctrl ]
                        Key = Key.Char 'k' } ] }

    let after, _ = Editor.update (KeyPressed escapeKey) pending
    after.PendingPrefix |> should equal (None: Chord list option)
    after.Lsp.Panel |> should equal (None: LspInfoPanel option)

[<Fact>]
let ``a stale hover result is dropped`` () =
    let model = openedModel ()
    let edited, _ = Editor.update (KeyPressed(chr 'z')) model

    let next, _ =
        Editor.update (LspHoverResolved(Result.Ok [ "stale" ], 0, model.Editors.ActiveBufferId)) edited

    next.Lsp.Panel |> should equal (None: LspInfoPanel option)

[<Fact>]
let ``a fetched log shows its tail in the dock panel`` () =
    let model = initModel ()
    let lines = [ for i in 1..20 -> $"line {i}" ]
    let next, _ = Editor.update (LspLogFetched("LSP log", lines)) model

    match next.Lsp.Panel with
    | Some panel ->
        panel.Title |> should equal "LSP log"
        panel.Lines |> List.last |> should equal "line 20"
        panel.Lines.Length |> should equal (model.Panels.DockHeight - 1)
    | None -> failwith "expected a dock panel"

[<Fact>]
let ``a fetched log keeps the newest lines on a short terminal`` () =
    // The dock's effective height shrinks to a third of a short terminal
    // (Dock.effectiveHeightCap) and the View truncates from the top, so
    // the tail must be sized against the rows actually painted — otherwise
    // the newest lines (the reason the user ran `:lsp log`) are cut.
    let model =
        { initModel () with
            Terminal = { Width = 80; Height = 15 } }

    let lines = [ for i in 1..20 -> $"line {i}" ]
    let next, _ = Editor.update (LspLogFetched("LSP log", lines)) model

    match next.Lsp.Panel with
    | Some panel ->
        panel.Lines |> List.last |> should equal "line 20"
        panel.Lines.Length |> should equal (Dock.effectiveHeightCap model - 1)
        (Dock.effectiveHeightCap model) |> should be (lessThan model.Panels.DockHeight)
    | None -> failwith "expected a dock panel"

[<Fact>]
let ``lsp command parses bare, verb, pending, and unknown forms`` () =
    match Commands.parse "lsp" with
    | Ready(Command.Lsp("", "")) -> ()
    | other -> failwith $"expected Ready (Lsp('', '')), got %A{other}"

    match Commands.parse "lsp enable sema" with
    | Ready(Command.Lsp("enable", "sema")) -> ()
    | other -> failwith $"expected Ready (Lsp(enable, sema)), got %A{other}"

    match Commands.parse "lsp enable" with
    | Pending _ -> ()
    | other -> failwith $"expected Pending, got %A{other}"

    match Commands.parse "lsp bogus" with
    | Invalid _ -> ()
    | other -> failwith $"expected Invalid, got %A{other}"

    match Commands.parse "diagnostics" with
    | Ready Command.Diagnostics -> ()
    | other -> failwith $"expected Ready Diagnostics, got %A{other}"

[<Fact>]
let ``:diagnostics opens the location picker over the active buffer's diagnostics`` () =
    let model = openedModel ()

    let published, _ =
        Editor.update
            (LspDiagnosticsPublished("/root/main.sema", [ diagnostic LspDiagnosticSeverity.Error "boom" ]))
            model

    let next, _ = typeAndRun "diagnostics" published
    next.Prompt.Session |> should equal PromptSessionKind.LocationsSession

    match next.Lsp.Locations with
    | Some set ->
        set.Title |> should equal "Diagnostics"

        set.Entries
        |> List.map (fun entry -> entry.Preview)
        |> should equal [ "error: boom" ]
    | None -> failwith "expected a location set"

[<Fact>]
let ``bare :lsp opens the manager and 'e' disables the selected server`` () =
    let manager, _ = typeAndRun "lsp" (initModel ())
    manager.Prompt.Active |> should equal true
    manager.Prompt.Session |> should equal PromptSessionKind.LanguageServersSession
    manager.Prompt.SelectedItemId |> should equal (Some "sema")

    let after, effects = Editor.update (KeyPressed(chr 'e')) manager
    after.Config.DisabledLanguageServers |> Set.contains "sema" |> should equal true

    effects
    |> List.exists (fun effect ->
        match effect with
        | SaveConfig _ -> true
        | _ -> false)
    |> should equal true

    effects
    |> List.exists (fun effect ->
        match effect with
        | LspRestart(Some "sema") -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``:lsp enable on an already-enabled server is a no-op`` () =
    // A running server must not receive a second didOpen for documents it
    // already has open (an LSP protocol violation) — and nothing should be
    // persisted for a state that didn't change.
    let model = openedModel ()
    let next, effects = typeAndRun "lsp enable sema" model

    lspSyncsOf effects |> List.isEmpty |> should equal true

    effects
    |> List.exists (fun effect ->
        match effect with
        | SaveConfig _
        | LspRestart _ -> true
        | _ -> false)
    |> should equal false

    next.Notification
    |> Option.map (fun notification -> notification.Message)
    |> should equal (Some "'sema' is already enabled.")

[<Fact>]
let ``:lsp disable on an already-disabled server is a no-op`` () =
    let model = openedModel ()

    let disabled =
        { model with
            Config =
                { model.Config with
                    DisabledLanguageServers = Set.ofList [ "sema" ] } }

    let next, effects = typeAndRun "lsp disable sema" disabled

    effects
    |> List.exists (fun effect ->
        match effect with
        | SaveConfig _
        | LspRestart _
        | LspSyncDocuments _ -> true
        | _ -> false)
    |> should equal false

    next.Config.DisabledLanguageServers |> should equal (Set.ofList [ "sema" ])

    next.Notification
    |> Option.map (fun notification -> notification.Message)
    |> should equal (Some "'sema' is already disabled.")

[<Fact>]
let ``:lsp restart tears the server down before re-opening its documents`` () =
    let model = openedModel ()
    let _, effects = typeAndRun "lsp restart sema" model

    let restartIndex =
        effects
        |> List.tryFindIndex (fun effect ->
            match effect with
            | LspRestart(Some "sema") -> true
            | _ -> false)

    let reopenIndex =
        effects
        |> List.tryFindIndex (fun effect ->
            match effect with
            | LspSyncDocuments(_, [ document ]) -> document.Path = "/root/main.sema"
            | _ -> false)

    match restartIndex, reopenIndex with
    | Some restart, Some reopen -> restart |> should be (lessThan reopen)
    | other -> failwith $"expected both a restart and a re-opening sync, got %A{other}"

// ── integration seams with the ux-macros work ────────────────────────────
// These cover interactions that exist only on the merged branch: the
// close-buffer action against the sync diff, the replay fence engine
// against the LSP request effects, and the combined Escape chain.

[<Fact>]
let ``closing a synced buffer emits a Closed sync`` () =
    let opened, _ =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    // Dispatch through `update` (not `runAction`): the Closed sync is
    // emitted by the `lspSyncEffects` chokepoint, which only the full
    // pipeline applies. The buffer is clean, so Ctrl+W closes at once.
    let ctrlW: Chord =
        { Mods = Set.ofList [ Ctrl ]
          Key = Key.Char 'w' }

    let closed, effects = Editor.update (KeyPressed ctrlW) opened

    closed.Editors.Buffers
    |> Map.exists (fun _ buffer -> buffer.FilePath = Some "/root/main.sema")
    |> should equal false

    match lspSyncsOf effects with
    | [ sync ] ->
        sync.Path |> should equal "/root/main.sema"
        sync.Kind |> should equal LspDocumentSyncKind.Closed
    | other -> failwith $"expected exactly one Closed sync, got %A{other}"

[<Fact>]
let ``a replayed goto-definition fences until the definition resolves`` () =
    let hasPump effects =
        effects
        |> List.exists (fun effect ->
            match effect with
            | ReplayPump -> true
            | _ -> false)

    let opened, _ =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    let model =
        { opened with
            Registers = Map.ofList [ 'a', [ RunAction GotoDefinition; RunAction(InsertText "x") ] ] }

    let started, startEffects = Editor.runAction (ReplayMacro('a', 1)) model
    startEffects |> should equal [ ReplayPump ]

    // Step 1 schedules the async definition request: fenced, no pump —
    // the insert must not run before the jump lands.
    let requested, requestEffects = Editor.update ReplayStepReady started

    let request =
        requestEffects
        |> List.tryPick (fun effect ->
            match effect with
            | LspRequestDefinition request -> Some request
            | _ -> None)
        |> Option.defaultWith (fun () -> failwith "expected an LspRequestDefinition effect")

    hasPump requestEffects |> should equal false

    (match requested.Replay with
     | Some state -> state.Queue
     | None -> failwith "replay vanished while fenced")
    |> should equal [ ReplayStep(RunAction(InsertText "x")) ]

    // The resolution clears the fence and pumps; the insert lands at the
    // jump target, not wherever the cursor was when the replay started.
    let location: LspResolvedLocation =
        { Path = "/root/main.sema"
          Position = { Line = 0; Column = 5 }
          Preview = "(def x 1)" }

    let landed, landedEffects =
        Editor.update (LspDefinitionResolved(Result.Ok [ location ], request.EditTick, request.BufferId)) requested

    hasPump landedEffects |> should equal true

    let rec drain (model: Model) =
        let next, effects = Editor.update ReplayStepReady model
        if hasPump effects then drain next else next

    let finished = drain landed
    let buffer = finished.Editors.Buffers[finished.Editors.ActiveBufferId]
    Buffer.text buffer |> should equal "(def xx 1)"
    finished.Replay |> should equal (None: ReplayState option)

[<Fact>]
let ``escape dismisses the error, then the panel, then the selection`` () =
    // The combined Escape precedence chain: a visible Error outranks the
    // LSP info panel, which outranks the keymap's clear-selection binding
    // — one press, one dismissal, three presses to a bare editor.
    let opened, _ =
        Editor.update (FileOpened("/root/main.sema", OpenPermanent, None, Result.Ok "(def x 1)")) (initModel ())

    let selected, _ = Editor.runAction ExtendRight opened

    let contested =
        { selected with
            Lsp =
                { selected.Lsp with
                    Panel = Some { Title = "Hover"; Lines = [ "doc" ] } }
            Notification = Some(Notification.error "boom") }

    let activeBuffer (model: Model) =
        model.Editors.Buffers[model.Editors.ActiveBufferId]

    (activeBuffer contested).Selection.IsSome |> should equal true

    // First Escape: the error goes; the panel and the selection stay.
    let first, _ = Editor.update (KeyPressed escapeKey) contested
    first.Notification |> should equal (None: Notification option)
    first.Lsp.Panel.IsSome |> should equal true
    (activeBuffer first).Selection.IsSome |> should equal true

    // Second Escape: the panel goes; the selection stays.
    let second, _ = Editor.update (KeyPressed escapeKey) first
    second.Lsp.Panel |> should equal (None: LspInfoPanel option)
    (activeBuffer second).Selection.IsSome |> should equal true

    // Third Escape resolves through the keymap: the selection clears.
    let third, _ = Editor.update (KeyPressed escapeKey) second
    (activeBuffer third).Selection.IsSome |> should equal false
