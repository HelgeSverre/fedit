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
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

        if String.IsNullOrEmpty home then
            path
        elif path = home then
            "~"
        elif path.StartsWith(home + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
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
        // default StatusFormat — the only discoverability surface this phase.
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

        sprintf "%d/%d" (idx + 1) ids.Length

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
        | _ ->
            // Surface typos by rendering the token literally.
            match modifier with
            | Some m -> $"[{name}:{m}]"
            | None -> $"[{name}]"

    // ─────────────────────────────────────────────────────────────────────
    // Layout
    // ─────────────────────────────────────────────────────────────────────

    /// Substitute every Token with its resolved Literal, leaving Expand
    /// parts in place.
    let private resolve (model: Model) (part: Part) =
        match part with
        | Token(name, modifier) -> Literal(resolveToken model name modifier)
        | other -> other

    /// Lay parts into `width` columns. `<EXPAND>` placeholders share
    /// whatever space the literals don't consume; any odd remainder is
    /// distributed left-to-right one column at a time. Overflow truncates
    /// the right side rather than wrapping.
    let private layout (width: int) (parts: Part list) =
        let fixedLen =
            parts
            |> List.sumBy (function
                | Literal s -> s.Length
                | _ -> 0)

        let expandCount =
            parts
            |> List.sumBy (function
                | Expand -> 1
                | _ -> 0)

        let remaining = max 0 (width - fixedLen)

        let perExpand, extra =
            if expandCount = 0 then
                0, 0
            else
                remaining / expandCount, remaining % expandCount

        let sb = StringBuilder()
        let mutable expandsSeen = 0

        for part in parts do
            match part with
            | Literal s -> sb.Append s |> ignore
            | Expand ->
                let pad = perExpand + (if expandsSeen < extra then 1 else 0)
                sb.Append(String.replicate pad " ") |> ignore
                expandsSeen <- expandsSeen + 1
            | Token _ -> ()

        let result = sb.ToString()

        if width > 0 && result.Length > width then
            result.Substring(0, width)
        else
            result

    // ─────────────────────────────────────────────────────────────────────
    // Public entry point
    // ─────────────────────────────────────────────────────────────────────

    /// Render the status bar for the current model into a string of
    /// exactly `width` columns (or fewer if the format produces less
    /// content and contains no `<EXPAND>`).
    let render (width: int) (model: Model) : string =
        model.Config.StatusFormat
        |> parseFormat
        |> List.map (resolve model)
        |> layout width
