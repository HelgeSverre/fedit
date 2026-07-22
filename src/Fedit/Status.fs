namespace Fedit

open System
open System.IO
open System.Text

/// Status bar format-string renderer. Pure: takes a model + width,
/// returns the rendered line. The format string lives in
/// `Config.StatusFormat`; tokens look like `[NAME]` or `[NAME:modifier]`
/// and `<EXPAND>` is a flexible spacer that absorbs leftover width.
///
/// Starship-inspired in spirit (named tokens, light syntax) but
/// intentionally bare — extending the registry of tokens is a
/// one-line addition in `resolveToken`.
[<RequireQualifiedAccess>]
module Status =

    /// A piece of the parsed format string. `Literal` carries fixed
    /// text from the format. `Token` is a placeholder that resolves to
    /// model-derived text. `Expand` is a flexible spacer.
    type private Part =
        | Literal of string
        | Token of name: string * modifier: string option
        | Expand

    // ─────────────────────────────────────────────────────────────────────
    // Parser
    // ─────────────────────────────────────────────────────────────────────

    /// Walk the format string left-to-right, splitting it into Literal /
    /// Token / Expand parts. A token is `[name]` or `[name:modifier]`;
    /// an expand placeholder is `<expand>` (case-insensitive). Anything
    /// that doesn't match either of those shapes — including a bare `[`
    /// without a matching `]` — falls through as literal text.
    let private parseFormat (fmt: string) : Part list =
        let parts = ResizeArray<Part>()
        let mutable i = 0
        let mutable cursor = 0

        let flushLiteral upto =
            if upto > cursor then
                parts.Add(Literal(fmt.Substring(cursor, upto - cursor)))

        while i < fmt.Length do
            if fmt[i] = '[' then
                let close = fmt.IndexOf(']', i + 1)

                if close > i then
                    flushLiteral i
                    let inside = fmt.Substring(i + 1, close - i - 1)

                    let name, modifier =
                        let colon = inside.IndexOf ':'

                        if colon < 0 then
                            inside.ToLowerInvariant(), None
                        else
                            inside.Substring(0, colon).ToLowerInvariant(),
                            Some(inside.Substring(colon + 1).ToLowerInvariant())

                    parts.Add(Token(name, modifier))
                    i <- close + 1
                    cursor <- i
                else
                    i <- i + 1
            elif fmt[i] = '<' then
                let close = fmt.IndexOf('>', i + 1)

                if close > i then
                    let inside = fmt.Substring(i + 1, close - i - 1).ToLowerInvariant()

                    if inside = "expand" then
                        flushLiteral i
                        parts.Add Expand
                        i <- close + 1
                        cursor <- i
                    else
                        // Unknown angle directive — leave it as literal text.
                        i <- i + 1
                else
                    i <- i + 1
            else
                i <- i + 1

        flushLiteral fmt.Length
        List.ofSeq parts

    // ─────────────────────────────────────────────────────────────────────
    // Token resolution
    // ─────────────────────────────────────────────────────────────────────

    let private tildify (path: string) =
        // Paths are canonical `/`; normalize home to match so the prefix test
        // works on Windows (UserProfile comes back with `\`).
        let home =
            Paths.norm (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)

        if String.IsNullOrEmpty home then
            path
        elif path = home then
            "~"
        elif path.StartsWith(home + "/", StringComparison.Ordinal) then
            "~" + path.Substring(home.Length)
        else
            path

    let private fileNameOf (path: string) =
        Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

    let private promptModeLabel mode =
        match mode with
        | FilePicker -> "FILE"
        | Command -> "CMD"
        | Search -> "FIND"
        | Buffers -> "BUF"

    let private focusText (model: Model) =
        // An in-flight multi-key sequence prefixes the mode label so it's
        // visible without a custom StatusFormat (the [pending] token is the
        // composable alternative).
        let pending =
            match model.PendingPrefix with
            | Some chords -> Chord.renderStroke chords + "… "
            | None -> ""

        // Active macro recording prefixes the mode so it shows under the
        // default StatusFormat.
        let recording =
            match model.Recording with
            | Some r -> $"REC @{r}  "
            | None -> ""

        let label =
            match model.Focus with
            | Sidebar ->
                if model.Workspace.SearchBuffer.Length > 0 then
                    $"TREE  find:{model.Workspace.SearchBuffer}"
                else
                    "TREE"
            | Editor -> "EDIT"
            | Prompt -> promptModeLabel model.Prompt.Mode

        recording + pending + label

    let private bufferIndicator (model: Model) =
        let ids = model.Editors.Buffers |> Map.toList |> List.map fst |> List.sort

        let idx =
            ids
            |> List.tryFindIndex (fun id -> id = model.Editors.ActiveBufferId)
            |> Option.defaultValue 0

        string (idx + 1) + "/" + string ids.Length

    let private resolveToken (model: Model) (name: string) (modifier: string option) =
        let buffer = model.Editors.Buffers[model.Editors.ActiveBufferId]

        // Appended to the rendered path, never stored in the buffer's Name —
        // promotion just clears the slot without rewriting buffer state.
        let previewSuffix =
            if model.Editors.PreviewBufferId = Some model.Editors.ActiveBufferId then
                " [preview]"
            else
                ""

        match name, modifier with
        | "mode", _ -> focusText model
        | "line", _ -> string (buffer.Cursor.Line + 1)
        | "column", _ -> string (buffer.Cursor.Column + 1)
        | "line_ending", _ -> if buffer.Newline = "\r\n" then "CRLF" else "LF"
        | "buffer", _ -> bufferIndicator model
        | "current_file", Some "full" -> (buffer.FilePath |> Option.defaultValue "[scratch]") + previewSuffix
        | "current_file", Some "short" ->
            (buffer.FilePath |> Option.map tildify |> Option.defaultValue "[scratch]")
            + previewSuffix
        | "current_file", _ ->
            (buffer.FilePath |> Option.map fileNameOf |> Option.defaultValue "[scratch]")
            + previewSuffix
        // Dirty includes its own leading space so the marker vanishes
        // cleanly when the buffer is clean.
        | "dirty", _ -> if buffer.Dirty then " [+]" else ""
        | "notification", _ -> model.Notification |> Option.map (fun n -> n.Message) |> Option.defaultValue ""
        | "pending", _ ->
            match model.PendingPrefix with
            | Some chords -> Chord.renderStroke chords + " …"
            | None -> ""
        // Compact language-server diagnostic counts for the active buffer,
        // e.g. "E2 W1". Like [DIRTY] it carries its own leading spaces so
        // it vanishes cleanly when the buffer has no diagnostics.
        | "diagnostics", _ ->
            let diagnostics =
                buffer.FilePath
                |> Option.bind (fun path -> Map.tryFind path model.Lsp.Diagnostics)
                |> Option.defaultValue []

            let countOf severity =
                diagnostics
                |> List.filter (fun diagnostic -> diagnostic.Severity = severity)
                |> List.length

            let segments =
                [ "E", countOf LspDiagnosticSeverity.Error
                  "W", countOf LspDiagnosticSeverity.Warning
                  "I", countOf LspDiagnosticSeverity.Information
                  "H", countOf LspDiagnosticSeverity.Hint ]
                |> List.choose (fun (label, count) -> if count > 0 then Some(label + string count) else None)

            match segments with
            | [] -> ""
            | _ -> "  " + String.concat " " segments
        | _ ->
            // Surface typos by rendering the token literally.
            match modifier with
            | Some m -> $"[{name}:{m}]"
            | None -> $"[{name}]"

    // ─────────────────────────────────────────────────────────────────────
    // Layout
    // ─────────────────────────────────────────────────────────────────────

    /// A format part resolved against the model, ready for layout.
    /// `NotificationText` is the resolved `[NOTIFICATION]` token, kept
    /// distinct from plain text so the view can restyle its final column
    /// span by severity.
    type private ResolvedPart =
        | ResolvedText of string
        | NotificationText of string
        | ResolvedExpand

    /// Substitute every Token with its resolved text, leaving Expand
    /// parts in place.
    let private resolve (model: Model) (part: Part) =
        match part with
        | Token("notification", modifier) -> NotificationText(resolveToken model "notification" modifier)
        | Token(name, modifier) -> ResolvedText(resolveToken model name modifier)
        | Literal s -> ResolvedText s
        | Expand -> ResolvedExpand

    /// Lay parts into `width` columns. `<EXPAND>` placeholders share
    /// whatever space the literals don't consume; any odd remainder is
    /// distributed left-to-right one column at a time. Overflow truncates
    /// the right side rather than wrapping. Also reports the (start,
    /// length) span of the first non-empty notification part, clipped to
    /// what survives truncation.
    let private layout (width: int) (parts: ResolvedPart list) =
        let fixedLen =
            parts
            |> List.sumBy (function
                | ResolvedText s -> s.Length
                | NotificationText s -> s.Length
                | ResolvedExpand -> 0)

        let expandCount =
            parts
            |> List.sumBy (function
                | ResolvedExpand -> 1
                | _ -> 0)

        let remaining = max 0 (width - fixedLen)

        let perExpand, extra =
            if expandCount = 0 then
                0, 0
            else
                remaining / expandCount, remaining % expandCount

        let sb = StringBuilder()
        let mutable expandsSeen = 0
        let mutable notificationSpan = None

        for part in parts do
            match part with
            | ResolvedText s -> sb.Append s |> ignore
            | NotificationText s ->
                if s.Length > 0 && Option.isNone notificationSpan then
                    notificationSpan <- Some(sb.Length, s.Length)

                sb.Append s |> ignore
            | ResolvedExpand ->
                let pad = perExpand + (if expandsSeen < extra then 1 else 0)
                sb.Append(String.replicate pad " ") |> ignore
                expandsSeen <- expandsSeen + 1

        let result = sb.ToString()

        let truncated =
            if width > 0 && result.Length > width then
                result.Substring(0, width)
            else
                result

        let clippedSpan =
            notificationSpan
            |> Option.bind (fun (start, length) ->
                if start >= truncated.Length then
                    None
                else
                    Some(start, min length (truncated.Length - start)))

        truncated, clippedSpan

    // ─────────────────────────────────────────────────────────────────────
    // Public entry points
    // ─────────────────────────────────────────────────────────────────────

    /// Render the status bar plus the column span (start, length) where
    /// the `[NOTIFICATION]` token landed — `None` when the format has no
    /// notification token, it resolved empty, or truncation cut it off.
    /// The view restyles that span by the notification's severity.
    let renderWithNotificationSpan (width: int) (model: Model) : string * (int * int) option =
        model.Config.StatusFormat
        |> parseFormat
        |> List.map (resolve model)
        |> layout width

    /// Render the status bar for the current model into a string of
    /// exactly `width` columns (or fewer if the format produces less
    /// content and contains no `<EXPAND>`).
    let render (width: int) (model: Model) : string =
        fst (renderWithNotificationSpan width model)
