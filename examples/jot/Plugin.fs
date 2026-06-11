namespace Jot

open Fedit.PluginApi

/// Session scratchpad: `:jot` appends the current location to a scratch
/// buffer named "jottings", `:jotdone` toggles the entry's checkbox, and
/// `:jotgo` jumps back to a jotted file.
///
/// Reference plugin for the post-MVP action set. Demonstrates:
/// `NewBuffer` + `SwitchBuffer` to write into a scratch buffer and return,
/// `MoveCursor` clamping (Line = 999999 lands on the last line),
/// `ReplaceRange` as a single undoable in-place edit, and
/// `RevealPath` + `OpenFilePreview` validated against `Workspace.Files`.
module Plugin =
    let private notesName = "jottings"

    /// The jottings scratch buffer, if open. Scratch means no file path —
    /// a file-backed buffer that happens to be called "jottings" doesn't count.
    let private tryFindNotes (ctx: PluginContext) =
        ctx.AllBuffers
        |> List.tryFind (fun b -> b.Name = notesName && b.FilePath.IsNone)

    /// Root-relative path for file-backed buffers; the buffer name for scratch.
    let private location (ctx: PluginContext) =
        match ctx.ActiveBuffer.FilePath with
        | Some path -> System.IO.Path.GetRelativePath(ctx.Workspace.RootPath, path)
        | None -> ctx.ActiveBuffer.Name

    let private currentLine (buffer: BufferView) =
        let lines = buffer.Text.Split '\n'
        lines.[min (buffer.Cursor.Line - 1) (lines.Length - 1)]

    let private jot (ctx: PluginContext) =
        let snippet = (currentLine ctx.ActiveBuffer).Trim()

        let entry = $"[ ] {location ctx}:{ctx.ActiveBuffer.Cursor.Line}  {snippet}"

        match tryFindNotes ctx with
        | None ->
            // NewBuffer activates the new scratch buffer, so switch back.
            [ NewBuffer(notesName, entry + "\n")
              SwitchBuffer ctx.ActiveBuffer.Id
              Notify(Info, "Jotted.") ]
        | Some notes ->
            [ SwitchBuffer notes.Id
              // Out-of-range coordinates clamp: this lands at end-of-buffer.
              MoveCursor { Line = 999999; Column = 999999 }
              InsertText(entry + "\n")
              SwitchBuffer ctx.ActiveBuffer.Id
              Notify(Info, "Jotted.") ]

    let private toggle (ctx: PluginContext) =
        let line = ctx.ActiveBuffer.Cursor.Line
        let text = currentLine ctx.ActiveBuffer

        let marker =
            if text.StartsWith "[ ]" then Some "[x]"
            elif text.StartsWith "[x]" then Some "[ ]"
            else None

        match marker with
        | None -> [ Notify(Warning, "Not on a jot entry.") ]
        | Some marker ->
            // One undo entry: swap the three-char checkbox in place.
            [ ReplaceRange({ Line = line; Column = 1 }, { Line = line; Column = 4 }, marker) ]

    let private go (ctx: PluginContext) =
        let text = (currentLine ctx.ActiveBuffer).Trim()

        let body =
            if text.StartsWith "[ ] " || text.StartsWith "[x] " then
                text.Substring 4
            else
                text

        let token = (body.Split ' ').[0]

        match token.LastIndexOf ':' with
        | -1 -> [ Notify(Warning, "No path:line on this line.") ]
        | i ->
            let path = token.Substring(0, i)

            if ctx.Workspace.Files |> List.contains path then
                // Reveal selects it in the sidebar without stealing focus;
                // the preview slot keeps the buffer list tidy while browsing.
                [ RevealPath path; OpenFilePreview path ]
            else
                [ Notify(Warning, $"Not in the workspace index: {path}") ]

    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "jot"
              Usage = "jot"
              Summary = "Append the current location to the jottings scratch buffer."
              Run = jot }

        host.RegisterCommand
            { Name = "jotdone"
              Usage = "jotdone"
              Summary = "Toggle the [ ]/[x] checkbox on the cursor's jot entry."
              Run = toggle }

        host.RegisterCommand
            { Name = "jotgo"
              Usage = "jotgo"
              Summary = "Reveal and preview the file on the cursor's jot entry."
              Run = go }

        host.RegisterKeybinding(KeyChord.Alt 'j', "jot")
