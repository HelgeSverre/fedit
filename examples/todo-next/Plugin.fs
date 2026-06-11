namespace TodoNext

open Fedit.PluginApi

/// Moves the cursor to the next `TODO:` in the active buffer. Wraps
/// around to the top if nothing is found below the cursor; if the active
/// buffer has none at all, continues into the other open buffers.
///
/// Reference plugin #3 of the TODO trio. Demonstrates: reading the cursor
/// position; emitting `MoveCursor` to relocate the user; `SwitchBuffer` to
/// continue the search across `AllBuffers`; bind a chord (`Ctrl+T`) so
/// users can jump quickly without typing the command.
module Plugin =
    let private marker = "TODO:"

    let private findFrom (lines: string[]) (startLine: int) =
        let mutable found = None
        let mutable i = startLine

        while found.IsNone && i < lines.Length do
            let col = lines.[i].IndexOf marker

            if col >= 0 then
                found <- Some(i, col)

            i <- i + 1

        found

    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "todonext"
              Usage = "todonext"
              Summary = "Jump cursor to the next `TODO:` across open buffers (wraps)."
              Run =
                fun ctx ->
                    let active = ctx.ActiveBuffer
                    // API cursor is 1-based; convert to 0-based line/col.
                    let cursorLine = active.Cursor.Line - 1
                    let lines = active.Text.Split('\n')

                    // Search below the cursor first; if nothing, wrap.
                    let activeHit =
                        match findFrom lines (cursorLine + 1) with
                        | Some result -> Some result
                        | None -> findFrom lines 0

                    match activeHit with
                    | Some(line0, col0) ->
                        let position = { Line = line0 + 1; Column = col0 + 1 }

                        [ MoveCursor position; Notify(Info, $"TODO at line {line0 + 1}") ]
                    | None ->
                        // Nothing here — try the other open buffers, in list order.
                        let otherHit =
                            ctx.AllBuffers
                            |> List.filter (fun b -> b.Id <> active.Id)
                            |> List.tryPick (fun b -> findFrom (b.Text.Split('\n')) 0 |> Option.map (fun hit -> b, hit))

                        match otherHit with
                        | Some(buffer, (line0, col0)) ->
                            let position = { Line = line0 + 1; Column = col0 + 1 }

                            [ SwitchBuffer buffer.Id
                              MoveCursor position
                              Notify(Info, $"TODO in {buffer.Name} at line {line0 + 1}") ]
                        | None -> [ Notify(Warning, "No TODO: in any open buffer") ] }

        host.RegisterKeybinding(KeyChord.Ctrl 't', "todonext")
