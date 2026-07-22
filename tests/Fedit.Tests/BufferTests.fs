module Fedit.Tests.BufferTests

open Fedit
open Xunit
open FsUnit.Xunit
open FsCheck

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
let ``moveLinesUp moves the current line and clamps at the top`` () =
    let original =
        Buffer.fromText 1 None "test" "alpha\nbravo\ncharlie\ndelta" "\n"
        |> Buffer.moveToOffset 13

    let moved = original |> Buffer.moveLinesUp 10

    Buffer.text moved |> should equal "charlie\nalpha\nbravo\ndelta"
    moved.Cursor |> should equal ({ Line = 0; Column = 1 }: Position)
    moved.Undo.Length |> should equal 1
    moved |> Buffer.undo |> Buffer.text |> should equal (Buffer.text original)

[<Fact>]
let ``moveLinesDown excludes a trailing line reached only at column zero`` () =
    let original =
        Buffer.fromText 1 None "test" "alpha\nbravo\ncharlie\ndelta\necho" "\n"

    let anchor = Buffer.positionToIndex { Line = 1; Column = 2 } original
    let head = Buffer.positionToIndex { Line = 3; Column = 0 } original
    let selected = Buffer.selectRange anchor head original
    let moved = selected |> Buffer.moveLinesDown 1

    Buffer.text moved |> should equal "alpha\ndelta\nbravo\ncharlie\necho"
    Buffer.selectionText moved |> should equal "avo\ncharlie\n"
    moved.Cursor |> should equal ({ Line = 4; Column = 0 }: Position)

[<Fact>]
let ``moveLinesDown preserves a backward selection`` () =
    let original =
        Buffer.fromText 1 None "test" "alpha\nbravo\ncharlie\ndelta\necho" "\n"

    let anchor = Buffer.positionToIndex { Line = 3; Column = 0 } original
    let head = Buffer.positionToIndex { Line = 1; Column = 2 } original
    let selected = Buffer.selectRange anchor head original
    let moved = selected |> Buffer.moveLinesDown 1

    let span = moved.Selection |> Option.get

    Buffer.indexToPosition span.Anchor moved
    |> should equal ({ Line = 4; Column = 0 }: Position)

    Buffer.indexToPosition span.Head moved
    |> should equal ({ Line = 2; Column = 2 }: Position)

    moved.Cursor |> should equal ({ Line = 2; Column = 2 }: Position)

[<Fact>]
let ``moveLines at a document boundary is a true no-op`` () =
    let original = Buffer.fromText 1 None "test" "alpha\nbravo" "\n"
    let moved = original |> Buffer.moveLinesUp 3

    moved |> should equal original
    original |> Buffer.moveLinesDown 0 |> should equal original

[<Fact>]
let ``moveLinesDown moves the current line and clamps at the bottom`` () =
    let original = Buffer.fromText 1 None "test" "alpha\nbravo\ncharlie" "\n"
    let moved = original |> Buffer.moveLinesDown 10

    Buffer.text moved |> should equal "bravo\ncharlie\nalpha"
    moved.Cursor |> should equal ({ Line = 2; Column = 0 }: Position)
    moved.Undo.Length |> should equal 1

[<Fact>]
let ``moveLinesUp preserves a final line without a trailing newline`` () =
    let original =
        Buffer.fromText 1 None "test" "alpha\nbravo\ncharlie" "\n"
        |> Buffer.moveToOffset 12

    let moved = original |> Buffer.moveLinesUp 1

    Buffer.text moved |> should equal "alpha\ncharlie\nbravo"

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
let ``undo is a text revision and clears stale selection`` () =
    let edited = newBuffer () |> Buffer.insertText "X"

    let withSelection =
        { edited with
            Selection =
                Some
                    { Anchor = PieceTable.length edited.Document
                      Head = 0 } }

    let undone = Buffer.undo withSelection
    Buffer.line 0 undone |> should equal "hello"
    undone.EditTick |> should equal (edited.EditTick + 1)
    undone.Selection |> should equal None

[<Fact>]
let ``selectionRange clamps stale anchors`` () =
    let buffer =
        { Buffer.fromText 1 None "test" "ab" "\n" with
            Selection = Some { Anchor = 99; Head = 0 }
            Cursor = Position.zero }

    Buffer.selectionText buffer |> should equal "ab"

[<Fact>]
let ``selectRange places the cursor at head`` () =
    let buffer = newBuffer () |> Buffer.selectRange 1 4
    buffer.Cursor |> should equal (Buffer.indexToPosition 4 buffer)
    Buffer.selectionRange buffer |> should equal (Some(1, 4))

[<Fact>]
let ``extendWith keeps the anchor across consecutive extends`` () =
    let buffer =
        newBuffer ()
        |> Buffer.extendWith Buffer.moveRight
        |> Buffer.extendWith Buffer.moveRight

    Buffer.selectionRange buffer |> should equal (Some(0, 2))

[<Fact>]
let ``extendWith after a detached cursor move snaps back to head`` () =
    let selected = newBuffer () |> Buffer.selectRange 0 1

    // A viewport-led scroll may move the cursor without touching the span;
    // simulate the drift by force-setting the cursor away from Head.
    let drifted =
        { selected with
            Cursor = Buffer.indexToPosition 8 selected }

    let extended = drifted |> Buffer.extendWith Buffer.moveRight
    Buffer.selectionRange extended |> should equal (Some(0, 2))
    extended.Cursor |> should equal (Buffer.indexToPosition 2 extended)

[<Fact>]
let ``moveToOffset leaves the selection span untouched`` () =
    // Search nav / :goto are span-neutral — only selection ops move the span.
    let buffer = newBuffer () |> Buffer.selectRange 1 4 |> Buffer.moveToOffset 8
    Buffer.selectionRange buffer |> should equal (Some(1, 4))

[<Fact>]
let ``stale save completion keeps buffer dirty`` () =
    let buffer = newBuffer () |> Buffer.insertText "X"
    let saved = Buffer.markSaved 0 "/tmp/test.txt" buffer
    saved.Dirty |> should equal true

[<Fact>]
let ``save preserves undo history`` () =
    let edited = newBuffer () |> Buffer.insertText "X"
    let saved = Buffer.markSaved edited.EditTick "/tmp/test.txt" edited
    saved.Dirty |> should equal false

    let undone = Buffer.undo saved
    Buffer.line 0 undone |> should equal "hello"
    undone.Dirty |> should equal true

[<Fact>]
let ``redo back to the saved revision is clean`` () =
    let edited = newBuffer () |> Buffer.insertText "X"
    let saved = Buffer.markSaved edited.EditTick "/tmp/test.txt" edited
    let redone = saved |> Buffer.undo |> Buffer.redo
    Buffer.line 0 redone |> should equal "Xhello"
    redone.Dirty |> should equal false

[<Fact>]
let ``redo reapplies the change`` () =
    let buffer = newBuffer () |> Buffer.insertText "X" |> Buffer.undo |> Buffer.redo
    Buffer.line 0 buffer |> should equal "Xhello"
    buffer.Selection |> should equal None

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

// --- findNextMatch / findPreviousMatch: repeat-search core ---

[<Fact>]
let ``findNextMatch returns the first match at or after fromIndex`` () =
    Buffer.findNextMatch "ab" 1 "ababab" |> should equal (Some 2)
    Buffer.findNextMatch "ab" 2 "ababab" |> should equal (Some 2)

[<Fact>]
let ``findNextMatch wraps to the first match when nothing follows`` () =
    Buffer.findNextMatch "ab" 5 "ababab" |> should equal (Some 0)

[<Fact>]
let ``findNextMatch is case-insensitive like the search prompt`` () =
    Buffer.findNextMatch "AB" 1 "xxabxx" |> should equal (Some 2)

[<Fact>]
let ``findNextMatch returns None for an empty or absent needle`` () =
    Buffer.findNextMatch "" 0 "hello" |> should equal None
    Buffer.findNextMatch "zz" 0 "hello" |> should equal None

[<Fact>]
let ``findPreviousMatch returns the last match at or before fromIndex`` () =
    Buffer.findPreviousMatch "ab" 3 "ababab" |> should equal (Some 2)
    Buffer.findPreviousMatch "ab" 2 "ababab" |> should equal (Some 2)

[<Fact>]
let ``findPreviousMatch wraps to the last match when nothing precedes`` () =
    Buffer.findPreviousMatch "ab" -1 "ababab" |> should equal (Some 4)

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

// --- Line-cache splice: the cache must always equal a fresh full split ---

let private linesInvariant buffer =
    Buffer.lines buffer = (Buffer.text buffer).Split('\n')

/// One random edit through the public API (so the splice path is what runs):
/// a non-negative `pick` inserts `payload` at a clamped offset; a negative
/// one selects a clamped range and deletes it.
let private editStep (buffer: BufferState) (pick: int, offset: int, count: int, payload: string) =
    let len = PieceTable.length buffer.Document
    let at = ((offset % (len + 1)) + (len + 1)) % (len + 1)

    if pick >= 0 then
        let text = if isNull payload then "" else payload
        buffer |> Buffer.moveToOffset at |> Buffer.insertText text
    else
        let hi = min len (at + ((count % 7 + 7) % 7))
        buffer |> Buffer.selectRange at hi |> Buffer.deleteSelection

[<Fact>]
let ``the line cache equals a fresh split after every edit`` () =
    // Driven through Check from a plain fact: FsCheck.Xunit's
    // [<Property>] discoverer is silently skipped by this project's
    // runner combination (xunit 2.9.3 + xunit.runner.visualstudio 3.x).
    Check.QuickThrowOnFailure(fun (ops: (int * int * int * string) list) ->
        ops
        |> List.truncate 12
        |> List.scan editStep (newBuffer ())
        |> List.forall linesInvariant)

[<Fact>]
let ``the line cache survives undo redo and divergent edits`` () =
    Check.QuickThrowOnFailure(fun (ops: (int * int * int * string) list) ->
        let edited = ops |> List.truncate 8 |> List.fold editStep (newBuffer ())

        let undoStates =
            [ 1 .. edited.Undo.Length ] |> List.scan (fun b _ -> Buffer.undo b) edited

        let unwound = List.last undoStates

        let redoStates =
            [ 1 .. unwound.Redo.Length ] |> List.scan (fun b _ -> Buffer.redo b) unwound

        // A divergent edit off the unwound state appends to the same shared
        // add buffer the redo stack still references.
        let divergent = unwound |> Buffer.moveToOffset 0 |> Buffer.insertText "x\ny"

        undoStates @ redoStates @ [ divergent ] |> List.forall linesInvariant)

[<Fact>]
let ``backspace at column zero joins lines in the cache`` () =
    let buffer =
        Buffer.fromText 1 None "test" "ab\ncd" "\n"
        |> Buffer.moveToOffset 3
        |> Buffer.backspace

    Buffer.lines buffer |> should equal [| "abcd" |]

[<Fact>]
let ``deleteForward at end of line joins lines in the cache`` () =
    let buffer =
        Buffer.fromText 1 None "test" "ab\ncd" "\n"
        |> Buffer.moveToOffset 2
        |> Buffer.deleteForward

    Buffer.lines buffer |> should equal [| "abcd" |]

[<Fact>]
let ``inserting a multi-line string splices new rows into the cache`` () =
    let buffer =
        Buffer.fromText 1 None "test" "ab" "\n"
        |> Buffer.moveToOffset 1
        |> Buffer.insertText "x\ny\nz"

    Buffer.lines buffer |> should equal [| "ax"; "y"; "zb" |]

[<Fact>]
let ``deleting a multi-line selection collapses cache rows`` () =
    let buffer =
        Buffer.fromText 1 None "test" "one\ntwo\nthree" "\n"
        |> Buffer.selectRange 2 9
        |> Buffer.deleteSelection

    Buffer.lines buffer |> should equal [| "onhree" |]
