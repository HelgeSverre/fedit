module Fedit.Tests.BufferTests

open Fedit
open Xunit
open FsUnit.Xunit

let private newBuffer () =
    Buffer.fromText 1 None "test" "hello\nworld\n" "\n"

[<Fact>]
let ``createEmpty starts at line 0 col 0`` () =
    let buffer = Buffer.createEmpty 1
    buffer.Cursor.Line |> should equal 0
    buffer.Cursor.Column |> should equal 0
    buffer.Dirty |> should equal false

[<Fact>]
let ``fromText caches Lines array`` () =
    let buffer = newBuffer ()
    Buffer.lineCount buffer |> should equal 3
    Buffer.line 0 buffer |> should equal "hello"
    Buffer.line 1 buffer |> should equal "world"

[<Fact>]
let ``insertText appends at cursor and moves cursor`` () =
    let buffer = newBuffer () |> Buffer.insertText "X"
    Buffer.line 0 buffer |> should equal "Xhello"
    buffer.Cursor.Column |> should equal 1
    buffer.Dirty |> should equal true

[<Fact>]
let ``moveRight advances cursor`` () =
    let buffer = newBuffer () |> Buffer.moveRight
    buffer.Cursor.Column |> should equal 1

[<Fact>]
let ``moveDown moves to next line preserving preferred column`` () =
    let buffer = newBuffer () |> Buffer.moveEnd |> Buffer.moveDown
    buffer.Cursor.Line |> should equal 1
    buffer.Cursor.Column |> should equal 5

[<Fact>]
let ``moveEnd goes to end of current line`` () =
    let buffer = newBuffer () |> Buffer.moveEnd
    buffer.Cursor.Column |> should equal 5

[<Fact>]
let ``backspace at start of buffer is a no-op`` () =
    let buffer = newBuffer ()
    let backspaced = buffer |> Buffer.backspace
    Buffer.text backspaced |> should equal "hello\nworld\n"

[<Fact>]
let ``backspace removes the char before the cursor`` () =
    // Start at col 0, advance twice (col 2), backspace removes the char at index 1.
    let buffer =
        newBuffer () |> Buffer.moveRight |> Buffer.moveRight |> Buffer.backspace

    Buffer.line 0 buffer |> should equal "hllo"

[<Fact>]
let ``undo restores prior text`` () =
    let buffer = newBuffer () |> Buffer.insertText "X" |> Buffer.undo
    Buffer.line 0 buffer |> should equal "hello"
    buffer.Dirty |> should equal false

[<Fact>]
let ``redo reapplies the change`` () =
    let buffer = newBuffer () |> Buffer.insertText "X" |> Buffer.undo |> Buffer.redo
    Buffer.line 0 buffer |> should equal "Xhello"

[<Fact>]
let ``indent inserts four spaces at cursor`` () =
    let buffer = newBuffer () |> Buffer.indent
    Buffer.line 0 buffer |> should equal "    hello"

[<Fact>]
let ``unindent removes leading spaces`` () =
    let buffer = newBuffer () |> Buffer.indent |> Buffer.unindent
    Buffer.line 0 buffer |> should equal "hello"

[<Fact>]
let ``serialize round-trips through Newline preference`` () =
    let buffer = Buffer.fromText 1 None "test" "a\nb\n" "\r\n"
    Buffer.serialize buffer |> should equal "a\r\nb\r\n"

[<Fact>]
let ``serialize for LF buffer is identity`` () =
    let buffer = Buffer.fromText 1 None "test" "a\nb\n" "\n"
    Buffer.serialize buffer |> should equal "a\nb\n"

[<Fact>]
let ``gutterWidth grows with line count`` () =
    let small = Buffer.fromText 1 None "test" "a" "\n"
    let large = Buffer.fromText 1 None "test" (String.replicate 1000 "x\n") "\n"
    Buffer.gutterWidth small |> should equal 5
    Buffer.gutterWidth large |> should be (greaterThanOrEqualTo 6)

[<Fact>]
let ``moveLeftWord skips over a word`` () =
    let buffer =
        Buffer.fromText 1 None "test" "hello world" "\n"
        |> Buffer.moveEnd
        |> Buffer.moveLeftWord

    buffer.Cursor.Column |> should equal 6

[<Fact>]
let ``moveRightWord stops at the end of the current word`` () =
    let buffer =
        Buffer.fromText 1 None "test" "hello world" "\n" |> Buffer.moveRightWord

    buffer.Cursor.Column |> should equal 5

[<Fact>]
let ``selectAll covers the whole buffer`` () =
    let buffer = newBuffer () |> Buffer.selectAll
    let selected = Buffer.selectionText buffer
    selected |> should equal "hello\nworld\n"

[<Fact>]
let ``findAll returns all positions of a needle`` () =
    let buffer = Buffer.fromText 1 None "test" "ababab" "\n"
    Buffer.findAll "ab" buffer |> should equal [ 0; 2; 4 ]

[<Fact>]
let ``findAll empty needle returns no matches`` () =
    let buffer = Buffer.fromText 1 None "test" "hello" "\n"
    Buffer.findAll "" buffer |> should be Empty
