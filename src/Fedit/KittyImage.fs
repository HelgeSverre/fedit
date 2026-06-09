namespace Fedit

open System

/// Kitty APC graphics protocol implementation.
[<RequireQualifiedAccess>]
module KittyImage =
    let private esc = "\u001b"
    let private st = $"{esc}\\"

    /// The minimal query payload for Kitty support detection.
    /// A 1x1 transparent PNG encoded as base64.
    let private queryPayload =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="

    /// Kitty image protocol implementation.
    let protocol: ImageProtocol =
        { Kind = ImageKitty
          Transmit =
            fun writer data ->
                let b64 = Convert.ToBase64String data.Bytes
                // Chunk size: 4096 bytes of base64 per APC chunk to stay
                // well under typical terminal input buffers.
                let chunkSize = 4096

                let chunks =
                    let rec loop (s: string) acc =
                        if s.Length <= chunkSize then
                            List.rev (s :: acc)
                        else
                            loop (s.Substring chunkSize) (s.Substring(0, chunkSize) :: acc)

                    loop b64 []

                let w = if data.CellWidth > 0 then $",c={data.CellWidth}" else ""
                let h = if data.CellHeight > 0 then $",r={data.CellHeight}" else ""
                let x = if data.Left > 0 then $",x={data.Left}" else ""
                let y = if data.Top > 0 then $",y={data.Top}" else ""

                let rec send chunks =
                    match chunks with
                    | [] -> ()
                    | [ last ] ->
                        // Last continuation chunk: m=0.
                        writer.Write($"{esc}_Gm=0;{last}{st}")
                    | head :: tail ->
                        // More chunks follow: m=1.
                        writer.Write($"{esc}_Gm=1;{head}{st}")
                        send tail

                // Header chunk carries placement geometry.
                match chunks with
                | [] -> ()
                | header :: rest ->
                    let more = if List.isEmpty rest then "" else ",m=1"
                    let headerPayload = $"a=T,f=100{w}{h}{x}{y}{more};{header}"
                    writer.Write($"{esc}_G{headerPayload}{st}")
                    send rest
                    writer.Flush()
          Clear =
            fun writer ->
                writer.Write($"{esc}_Ga=d,d=A;{st}")
                writer.Flush()
          QuerySupport =
            fun writer readKey timeout ->
                // Send a tiny transparent image with a=q (query).
                writer.Write($"{esc}_Gi=1,s=1,v=1,a=q,f=24;{queryPayload}{st}")
                writer.Flush()

                let sw = System.Diagnostics.Stopwatch.StartNew()
                let mutable found = false

                while not found && sw.Elapsed < timeout do
                    match readKey 50 with
                    | Some keyInfo when keyInfo.Key = System.ConsoleKey.Escape ->
                        // Drain a potential APC response: ESC _ ... ESC \
                        let sb = System.Text.StringBuilder()
                        sb.Append '\u001b' |> ignore

                        let mutable terminated = false
                        let mutable guard = 0

                        while not terminated && guard < 128 do
                            match readKey 50 with
                            | Some k ->
                                sb.Append k.KeyChar |> ignore
                                let s = sb.ToString()

                                if s.EndsWith(st, StringComparison.Ordinal) then
                                    terminated <- true
                                    // Check for OK response in the payload
                                    found <- s.Contains("OK", StringComparison.Ordinal)
                            | None ->
                                if sw.Elapsed >= timeout then
                                    terminated <- true

                            guard <- guard + 1
                    | _ -> ()

                found }
