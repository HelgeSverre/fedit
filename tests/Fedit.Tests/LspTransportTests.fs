module Fedit.Tests.LspTransportTests

open System
open System.IO
open System.Text
open Fedit
open Xunit
open FsUnit.Xunit

/// Delivers at most one byte per Read call — a pipe handing frames over in
/// fragments.
type private TrickleStream(inner: Stream) =
    inherit Stream()
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = inner.Length

    override _.Position
        with get () = inner.Position
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()

    override _.Read(buffer, offset, count) = inner.Read(buffer, offset, min 1 count)

    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

let private streamOf (text: string) =
    new MemoryStream(Encoding.UTF8.GetBytes text)

[<Fact>]
let ``writeFrame then readFrame round-trips`` () =
    use stream = new MemoryStream()
    LspTransport.writeFrame stream """{"jsonrpc":"2.0","id":1}"""
    stream.Position <- 0L

    LspTransport.readFrame stream
    |> should equal (Some """{"jsonrpc":"2.0","id":1}""")

[<Fact>]
let ``round-trip preserves multi-byte UTF-8 and frame boundaries`` () =
    use stream = new MemoryStream()
    LspTransport.writeFrame stream "{\"text\":\"héllo — ✂\"}"
    LspTransport.writeFrame stream "{\"second\":true}"
    stream.Position <- 0L
    LspTransport.readFrame stream |> should equal (Some "{\"text\":\"héllo — ✂\"}")
    LspTransport.readFrame stream |> should equal (Some "{\"second\":true}")
    Assert.True(LspTransport.readFrame stream |> Option.isNone)

[<Fact>]
let ``writeFrame emits a Content-Length header counting UTF-8 bytes`` () =
    use stream = new MemoryStream()
    LspTransport.writeFrame stream "ø" // 1 char, 2 UTF-8 bytes

    Encoding.UTF8.GetString(stream.ToArray())
    |> should equal "Content-Length: 2\r\n\r\nø"

[<Fact>]
let ``readFrame tolerates a Content-Type header in either order`` () =
    use after =
        streamOf "Content-Length: 2\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{}"

    LspTransport.readFrame after |> should equal (Some "{}")

    use before =
        streamOf "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: 2\r\n\r\n{}"

    LspTransport.readFrame before |> should equal (Some "{}")

[<Fact>]
let ``readFrame assembles a frame delivered one byte at a time`` () =
    use inner = streamOf "Content-Length: 15\r\n\r\n{\"split\":\"yes\"}"
    use stream = new TrickleStream(inner)
    LspTransport.readFrame stream |> should equal (Some "{\"split\":\"yes\"}")

[<Fact>]
let ``readFrame returns None on an empty stream`` () =
    use stream = new MemoryStream()
    Assert.True(LspTransport.readFrame stream |> Option.isNone)

[<Fact>]
let ``readFrame returns None when the body is truncated`` () =
    use stream = streamOf "Content-Length: 99\r\n\r\n{}"
    Assert.True(LspTransport.readFrame stream |> Option.isNone)

[<Fact>]
let ``readFrame returns None when the header block is cut off`` () =
    use stream = streamOf "Content-Le"
    Assert.True(LspTransport.readFrame stream |> Option.isNone)
