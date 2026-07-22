namespace Fedit

open System

// Pure data for the LSP client: the protocol subset fedit consumes, plus
// path<->URI conversion. LSP's default position encoding is 0-based line +
// 0-based UTF-16 code unit — identical to fedit's own `Position`/`Buffer`
// semantics (.NET string indexes), so the mapping is the identity and no
// conversion layer exists anywhere.

/// A protocol position: 0-based line + 0-based UTF-16 code unit (LSP calls
/// the column `character`). Same semantics as fedit's `Position` — only the
/// field name differs.
[<Struct>]
type LspPosition = { Line: int; Character: int }

[<RequireQualifiedAccess>]
module LspPosition =
    /// Identity mapping — both sides are 0-based UTF-16 code units.
    let ofPosition (position: Position) : LspPosition =
        { Line = position.Line
          Character = position.Column }

    /// Identity mapping — both sides are 0-based UTF-16 code units.
    let toPosition (position: LspPosition) : Position =
        { Line = position.Line
          Column = position.Character }

[<Struct>]
type LspRange =
    { Start: LspPosition; End: LspPosition }

type LspLocation = { Uri: string; Range: LspRange }

[<RequireQualifiedAccess>]
type LspDiagnosticSeverity =
    | Error
    | Warning
    | Information
    | Hint

type LspDiagnostic =
    { Range: LspRange
      Severity: LspDiagnosticSeverity
      Message: string
      Source: string option
      Code: string option }

/// How the server wants document content synchronized. fedit always sends
/// the full text; `Incremental` servers accept full-document changes too
/// (a whole-document range is a valid incremental change).
[<RequireQualifiedAccess>]
type LspTextDocumentSyncKind =
    | None
    | Full
    | Incremental

/// The server capabilities subset fedit consumes from an InitializeResult.
type LspServerCapabilities =
    { TextDocumentSync: LspTextDocumentSyncKind
      DefinitionProvider: bool
      ReferencesProvider: bool
      HoverProvider: bool }

[<RequireQualifiedAccess>]
module LspServerCapabilities =
    /// What a server advertises before its InitializeResult has been parsed.
    let none =
        { TextDocumentSync = LspTextDocumentSyncKind.None
          DefinitionProvider = false
          ReferencesProvider = false
          HoverProvider = false }

/// One configured language server: which binary to spawn and which files it
/// owns. Lives here (not Config.fs, which compiles later) so Model can hold
/// it and Config can produce it.
type LanguageServerConfig =
    {
        Name: string
        Command: string
        Args: string list
        /// File extensions without the dot, e.g. ["sema"].
        FileTypes: string list
        /// Files whose presence marks a workspace root, e.g. ["sema.toml"].
        RootMarkers: string list
    }

[<RequireQualifiedAccess>]
type LspServerStatus =
    | NotStarted
    | Starting
    | Running
    | Failed of string
    | Stopped

/// file:// URI <-> canonical forward-slash path. LSP identifies documents by
/// URI; fedit's path model is canonical `/`-separated paths (`Paths.norm`).
[<RequireQualifiedAccess>]
module LspUri =
    /// Canonical absolute path -> file:// URI, percent-encoding each path
    /// segment (spaces, unicode). A Windows drive path `C:/...` gains the
    /// standard leading slash: `file:///C%3A/...`.
    let fromPath (path: string) : string =
        let normalized = Paths.norm path

        let escaped =
            normalized.Split '/' |> Array.map Uri.EscapeDataString |> String.concat "/"

        if escaped.StartsWith "/" then
            "file://" + escaped
        else
            "file:///" + escaped

    /// file:// URI -> canonical forward-slash path. Percent-decodes, handles
    /// the Windows drive form `file:///C:/...` (colon encoded or plain) and
    /// an authority component (`file://localhost/...`), and normalizes the
    /// result. None for non-file URIs.
    let toPath (uri: string) : string option =
        if not (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) then
            None
        else
            let rest = uri.Substring "file://".Length

            // Drop an authority component: file://localhost/tmp/x -> /tmp/x.
            let pathPart =
                if rest.StartsWith "/" then
                    rest
                else
                    match rest.IndexOf '/' with
                    | -1 -> "/"
                    | i -> rest.Substring i

            let decoded = Uri.UnescapeDataString pathPart

            // Windows drive form: /C:/... -> C:/...
            let path =
                if
                    decoded.Length >= 3
                    && decoded.[0] = '/'
                    && Char.IsAsciiLetter decoded.[1]
                    && decoded.[2] = ':'
                then
                    decoded.Substring 1
                else
                    decoded

            Some(Paths.norm path)
