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
let ``indent inserts configured spaces at cursor`` () =
    let buffer = newBuffer () |> Buffer.indent 4
    Buffer.line 0 buffer |> should equal "    hello"

[<Fact>]
let ``indent honours custom tab width`` () =
    let buffer = newBuffer () |> Buffer.indent 2
    Buffer.line 0 buffer |> should equal "  hello"

[<Fact>]
let ``unindent removes leading spaces`` () =
    let buffer = newBuffer () |> Buffer.indent 4 |> Buffer.unindent 4
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
        Buffer.fromText 1 None "test" "hello world" "\n" |> Buffer.moveRightWord WordEnd

    buffer.Cursor.Column |> should equal 5

// 3-class word-motion (CharClass: Whitespace / WordChar / Punctuation / Other).
// Each test sets up a buffer, optionally moves the cursor, then asserts the
// column after a single word-motion call. Together these prove the
// class-transition boundaries.

let private wordBuf text = Buffer.fromText 1 None "test" text "\n"

let private advanceBy n buffer =
    let mutable b = buffer

    for _ in 1..n do
        b <- Buffer.moveRight b

    b

[<Fact>]
let ``moveRightWord stops at punctuation boundary`` () =
    let buffer = wordBuf "hello.world" |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 5

[<Fact>]
let ``moveRightWord treats a single-char punctuation run as its own stop`` () =
    let buffer = wordBuf "hello.world" |> advanceBy 5 |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 6

[<Fact>]
let ``moveRightWord skips to the end of the following word`` () =
    let buffer = wordBuf "hello.world" |> advanceBy 6 |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 11

[<Fact>]
let ``moveRightWord WordEnd does NOT consume trailing whitespace`` () =
    let buffer = wordBuf "foo  bar" |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 3

[<Fact>]
let ``moveRightWord NextWordStart consumes trailing whitespace`` () =
    let buffer = wordBuf "foo  bar" |> Buffer.moveRightWord NextWordStart
    buffer.Cursor.Column |> should equal 5

[<Fact>]
let ``moveRightWord treats a multi-char punctuation run as one stop`` () =
    let buffer = wordBuf "foo->bar" |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 3

[<Fact>]
let ``moveRightWord advances over the punctuation run as one move`` () =
    let buffer = wordBuf "foo->bar" |> advanceBy 3 |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 5

[<Fact>]
let ``moveLeftWord from end of (foo) lands at start of closing-paren run`` () =
    let buffer = wordBuf "(foo)" |> Buffer.moveEnd |> Buffer.moveLeftWord
    buffer.Cursor.Column |> should equal 4

[<Fact>]
let ``moveLeftWord from closing paren lands at start of word run`` () =
    let buffer = wordBuf "(foo)" |> advanceBy 4 |> Buffer.moveLeftWord
    buffer.Cursor.Column |> should equal 1

[<Fact>]
let ``moveLeftWord from end of foo bar lands at start of bar`` () =
    let buffer = wordBuf "foo bar" |> Buffer.moveEnd |> Buffer.moveLeftWord
    buffer.Cursor.Column |> should equal 4

[<Fact>]
let ``moveRightWord eats leading whitespace then the next word`` () =
    let buffer = wordBuf "   hello" |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 8

[<Fact>]
let ``moveRightWord treats underscore as part of the word`` () =
    let buffer = wordBuf "hello_world" |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 11

[<Fact>]
let ``moveRightWord treats Unicode letters as part of the word`` () =
    let buffer = wordBuf "Café.txt" |> Buffer.moveRightWord WordEnd
    buffer.Cursor.Column |> should equal 4

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

// --- Viewport: scrolloff margin (cursor-led) + viewport-led scroll (wheel) ---

let private linesBuffer n =
    let text = String.concat "\n" [ for i in 0 .. n - 1 -> sprintf "line%d" i ]
    Buffer.fromText 1 None "test" text "\n"

[<Fact>]
let ``ensureViewport keeps scrolloff lines below the cursor`` () =
    let buffer =
        { linesBuffer 50 with
            Cursor = { Line = 20; Column = 0 }
            ViewportTop = 0 }

    // height 10, scrolloff 3: cursor pinned 3 from the bottom -> top = 20 - 10 + 1 + 3
    (Buffer.ensureViewport 3 10 80 buffer).ViewportTop |> should equal 14

[<Fact>]
let ``ensureViewport keeps scrolloff lines above the cursor`` () =
    let buffer =
        { linesBuffer 50 with
            Cursor = { Line = 20; Column = 0 }
            ViewportTop = 18 }

    // cursor only 2 below top with scrolloff 3 -> slide up to keep the 3-line margin
    (Buffer.ensureViewport 3 10 80 buffer).ViewportTop |> should equal 17

[<Fact>]
let ``ensureViewport relaxes the margin at the top of the file`` () =
    let buffer =
        { linesBuffer 50 with
            Cursor = { Line = 1; Column = 0 }
            ViewportTop = 0 }

    // can't scroll above line 0, so the top margin is relaxed
    (Buffer.ensureViewport 3 10 80 buffer).ViewportTop |> should equal 0

[<Fact>]
let ``scrollViewport moves the viewport down by delta`` () =
    let buffer =
        { linesBuffer 100 with
            Cursor = { Line = 0; Column = 0 }
            ViewportTop = 0 }

    (Buffer.scrollViewport 5 10 3 buffer).ViewportTop |> should equal 3

[<Fact>]
let ``scrollViewport drags the cursor to honour scrolloff`` () =
    let buffer =
        { linesBuffer 100 with
            Cursor = { Line = 1; Column = 0 }
            ViewportTop = 0 }

    // scroll down 5 (height 10, scrolloff 4) -> top 5, cursor dragged to top+4
    let v = Buffer.scrollViewport 4 10 5 buffer
    v.ViewportTop |> should equal 5
    v.Cursor.Line |> should equal 9

[<Fact>]
let ``scrollViewport leaves the cursor on its line when it stays in band`` () =
    let buffer =
        { linesBuffer 100 with
            Cursor = { Line = 7; Column = 0 }
            ViewportTop = 0 }

    let v = Buffer.scrollViewport 4 10 2 buffer
    v.ViewportTop |> should equal 2
    v.Cursor.Line |> should equal 7

[<Fact>]
let ``scrollViewport up at the top of the file is a no-op`` () =
    let buffer =
        { linesBuffer 100 with
            Cursor = { Line = 0; Column = 0 }
            ViewportTop = 0 }

    let v = Buffer.scrollViewport 4 10 -3 buffer
    v.ViewportTop |> should equal 0
    v.Cursor.Line |> should equal 0

[<Fact>]
let ``scrollViewport clamps the viewport at the bottom of the document`` () =
    let buffer =
        { linesBuffer 100 with
            Cursor = { Line = 90; Column = 0 }
            ViewportTop = 88 }

    (Buffer.scrollViewport 4 10 20 buffer).ViewportTop |> should equal 90
