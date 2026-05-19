namespace Fedit

[<RequireQualifiedAccess>]
module Prompt =
    let modeOf (text: string) =
        if text.Length = 0 then
            FilePicker
        else
            match text[0] with
            | ':' -> Command
            | '/' -> Search
            | '@' -> Buffers
            | _ -> FilePicker

    let argumentOf (text: string) =
        if text.Length = 0 then
            text
        else
            match text[0] with
            | ':'
            | '/'
            | '@' -> text.Substring 1
            | _ -> text
