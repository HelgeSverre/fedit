namespace TodoNext

open Fedit.PluginApi

/// Moves the cursor to the next `TODO:` in the active buffer. Wraps
/// around to the top if nothing is found below the cursor.
///
/// Reference plugin #3 of the TODO trio. Demonstrates: reading the cursor
/// position; emitting `MoveCursor` to relocate the user; bind a chord
/// (`Ctrl+T`) so users can jump quickly without typing the command.
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
              Summary = "Jump cursor to the next `TODO:` in the active buffer (wraps)."
              Run =
                fun ctx ->
                    let text = ctx.ActiveBuffer.Text
                    // API cursor is 1-based; convert to 0-based line/col.
                    let cursorLine = ctx.ActiveBuffer.Cursor.Line - 1
                    let lines = text.Split('\n')

                    // Search below the cursor first; if nothing, wrap.
                    let hit =
                        match findFrom lines (cursorLine + 1) with
                        | Some result -> Some result
                        | None -> findFrom lines 0

                    match hit with
                    | None -> [ Notify(Warning, "No TODO: in this buffer") ]
                    | Some(line0, col0) ->
                        let position = { Line = line0 + 1; Column = col0 + 1 }

                        [ MoveCursor position; Notify(Info, $"TODO at line {line0 + 1}") ] }

        host.RegisterKeybinding(KeyChord.Ctrl 't', "todonext")
