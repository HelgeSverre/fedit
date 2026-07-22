namespace Fedit

open System
open System.IO
open System.Text

/// Content-Length framing over a raw stream — the LSP transport. Each
/// message is an ASCII header block (`Content-Length: N`, optionally
/// `Content-Type: ...`) terminated by a blank line, followed by exactly N
/// bytes of UTF-8 JSON. Pure stream plumbing — process wiring lives in
/// LspClient.
[<RequireQualifiedAccess>]
module LspTransport =

    /// Write one framed message and flush. Frames from concurrent writers
    /// must not interleave, so writes serialize on the stream object itself
    /// as the writer lock.
    let writeFrame (stream: Stream) (json: string) : unit =
        let body = Encoding.UTF8.GetBytes json

        let header =
            Encoding.ASCII.GetBytes(sprintf "Content-Length: %d\r\n\r\n" body.Length)

        lock stream (fun () ->
            stream.Write(header, 0, header.Length)
            stream.Write(body, 0, body.Length)
            stream.Flush())

    // One header line, byte at a time (headers are ASCII and tiny, and
    // reading past the blank line would eat body bytes). None on EOF before
    // the line terminator.
    let private readLine (stream: Stream) : string option =
        let line = StringBuilder()
        let mutable terminated = false
        let mutable endOfStream = false

        while not terminated && not endOfStream do
            match stream.ReadByte() with
            | -1 -> endOfStream <- true
            | 10 -> terminated <- true // '\n'
            | 13 -> () // '\r'
            | b -> line.Append(char b) |> ignore

        if endOfStream then None else Some(line.ToString())

    // Parse the header block down to its Content-Length. None on EOF or a
    // block that never names a parsable Content-Length (the stream is
    // unrecoverable without one).
    let rec private readContentLength (stream: Stream) (contentLength: int option) : int option =
        match readLine stream with
        | None -> None
        | Some "" -> contentLength
        | Some line ->
            let updated =
                match line.IndexOf ':' with
                | -1 -> contentLength
                | separator ->
                    let name = line.Substring(0, separator).Trim()

                    if name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) then
                        match Int32.TryParse(line.Substring(separator + 1).Trim()) with
                        | true, value when value >= 0 -> Some value
                        | _ -> contentLength
                    else
                        contentLength

            readContentLength stream updated

    /// Read one framed message: the header block (tolerant of Content-Type
    /// and unknown headers, in any order), then exactly Content-Length bytes
    /// of UTF-8 body. None on EOF — including a header block or body cut off
    /// mid-frame.
    let readFrame (stream: Stream) : string option =
        match readContentLength stream None with
        | None -> None
        | Some length ->
            let body = Array.zeroCreate<byte> length
            let mutable filled = 0
            let mutable endOfStream = false

            while filled < length && not endOfStream do
                match stream.Read(body, filled, length - filled) with
                | 0 -> endOfStream <- true
                | count -> filled <- filled + count

            if endOfStream then
                None
            else
                Some(Encoding.UTF8.GetString body)
