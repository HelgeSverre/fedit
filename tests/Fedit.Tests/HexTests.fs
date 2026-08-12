module Fedit.Tests.HexTests

open Fedit
open Xunit
open FsUnit.Xunit

// Latin1-projected byte literals, escape-explicit so no editor/formatter
// can silently reinterpret them.
let private nul = "\u0000"
let private ff = "ÿ"

// ─────────────────────────────────────────────────────────────────────
// Pure Hex module: projection, detection, geometry, parsing, search.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``latin1 projection round-trips all 256 byte values`` () =
    let bytes = Array.init 256 byte
    let text = Hex.bytesToText bytes
    text.Length |> should equal 256
    Hex.textToBytes text |> should equal bytes

[<Fact>]
let ``looksBinary flags a NUL byte and passes plain text`` () =
    Hex.looksBinary [| 0x68uy; 0x00uy; 0x69uy |] |> should equal true

    Hex.looksBinary (System.Text.Encoding.UTF8.GetBytes "hello\nworld")
    |> should equal false

    Hex.looksBinary [||] |> should equal false

[<Fact>]
let ``layoutFor picks the widest classic row that fits`` () =
    (Hex.layoutFor 80).BytesPerRow |> should equal 16
    (Hex.layoutFor 76).BytesPerRow |> should equal 16
    (Hex.layoutFor 75).BytesPerRow |> should equal 8
    (Hex.layoutFor 43).BytesPerRow |> should equal 8
    (Hex.layoutFor 42).BytesPerRow |> should equal 4
    (Hex.layoutFor 10).BytesPerRow |> should equal 4

[<Fact>]
let ``hex and ascii columns round-trip through targetAt`` () =
    let layout = Hex.layoutFor 80

    for i in 0 .. layout.BytesPerRow - 1 do
        Hex.targetAt layout (Hex.hexColOf layout i)
        |> should equal (Some(i, HexBytes, true))

        Hex.targetAt layout (Hex.hexColOf layout i + 1)
        |> should equal (Some(i, HexBytes, false))

        Hex.targetAt layout (Hex.asciiColOf layout i)
        |> should equal (Some(i, HexAscii, true))

    // The offset column and the mid-row group gap are dead space.
    Hex.targetAt layout 0 |> should equal None
    Hex.targetAt layout (Hex.hexColOf layout 8 - 1) |> should equal None

[<Fact>]
let ``sixteen-byte rows carry the classic mid-row gap`` () =
    let layout = Hex.layoutFor 80
    // Crossing the 8-byte group boundary skips one extra column.
    (Hex.hexColOf layout 8) - (Hex.hexColOf layout 7) |> should equal 4
    (Hex.hexColOf layout 1) - (Hex.hexColOf layout 0) |> should equal 3

[<Fact>]
let ``rowCount always includes the append cell`` () =
    Hex.rowCount 16 0 |> should equal 1
    Hex.rowCount 16 15 |> should equal 1
    Hex.rowCount 16 16 |> should equal 2
    Hex.rowCount 16 17 |> should equal 2

[<Fact>]
let ``tryParseBytes accepts spaced and packed hex, rejects everything else`` () =
    Hex.tryParseBytes "1A 2C 78" |> should equal (Some "\u001a,x")
    Hex.tryParseBytes "1a2c78" |> should equal (Some "\u001a,x")
    Hex.tryParseBytes "ff" |> should equal (Some ff)
    Hex.tryParseBytes "1a2" |> should equal None // odd digit count
    Hex.tryParseBytes "xyz" |> should equal None
    Hex.tryParseBytes "" |> should equal None
    Hex.tryParseBytes "   " |> should equal None

[<Fact>]
let ``searchNeedle falls back to the literal query`` () =
    Hex.searchNeedle "cash" |> should equal "cash"
    Hex.searchNeedle "1a2c" |> should equal "\u001a,"

[<Fact>]
let ``toHexString renders the clipboard form`` () =
    Hex.toHexString "thi" |> should equal "74 68 69"
    Hex.toHexString (nul + ff) |> should equal "00 ff"

[<Fact>]
let ``findAllExact is byte-exact where text search is case-insensitive`` () =
    Buffer.findAllMatches "a" "aA" |> should equal [ 0; 1 ]
    Hex.findAllExact "a" "aA" |> should equal [ 0 ]
    Hex.findAllExact "ab" "abxab" |> should equal [ 0; 3 ]
    Hex.findNextExact "ab" 1 "abxab" |> should equal (Some 3)
    Hex.findNextExact "ab" 4 "abxab" |> should equal (Some 0) // wraps
    Hex.findPreviousExact "ab" 2 "abxab" |> should equal (Some 0)
    Hex.findPreviousExact "ab" -1 "abxab" |> should equal (Some 3) // wraps

[<Fact>]
let ``slice reads across cached line boundaries as 0x0A bytes`` () =
    let buffer = Buffer.fromText 1 None "bin" "AB\nCD" "\n"
    Hex.slice buffer 0 5 |> should equal "AB\nCD"
    Hex.slice buffer 1 3 |> should equal "B\nC"
    Hex.slice buffer 4 10 |> should equal "D" // clamped at the end
    Hex.byteAt buffer 2 |> should equal (Some 0x0A)
    Hex.byteAt buffer 5 |> should equal None

// ─────────────────────────────────────────────────────────────────────
// Disk round trip: the binary read/write pair must be byte-exact.
// ─────────────────────────────────────────────────────────────────────

[<Fact>]
let ``writeAllBytesAtomic round-trips raw bytes`` () =
    let path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    let bytes = Array.init 256 byte

    try
        File.writeAllBytesAtomic path bytes
        System.IO.File.ReadAllBytes path |> should equal bytes
    finally
        System.IO.File.Delete path

[<Fact>]
let ``backupOnce copies the original once and never overwrites the backup`` () =
    let path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    let bakPath = path + ".bak"
    let original = [| 0x00uy; 0x1Auy; 0xFFuy |]

    try
        // No file on disk yet (a fresh :writeas target): nothing to back up.
        File.backupOnce path |> should equal false
        System.IO.File.Exists bakPath |> should equal false

        System.IO.File.WriteAllBytes(path, original)
        File.backupOnce path |> should equal true
        System.IO.File.ReadAllBytes bakPath |> should equal original

        // A later save must not clobber the first backup.
        System.IO.File.WriteAllBytes(path, [| 0x42uy |])
        File.backupOnce path |> should equal false
        System.IO.File.ReadAllBytes bakPath |> should equal original
    finally
        for p in [ path; bakPath ] do
            if System.IO.File.Exists p then
                System.IO.File.Delete p

[<Fact>]
let ``backupOnce never copies a symbolic-link target into the link directory`` () =
    let directory =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    System.IO.Directory.CreateDirectory directory |> ignore
    let target = System.IO.Path.Combine(directory, "target.bin")
    let linkDirectory = System.IO.Path.Combine(directory, "workspace")
    System.IO.Directory.CreateDirectory linkDirectory |> ignore
    let link = System.IO.Path.Combine(linkDirectory, "asset.bin")

    try
        System.IO.File.WriteAllBytes(target, [| 0x41uy; 0x42uy |])
        System.IO.File.CreateSymbolicLink(link, target) |> ignore
        File.backupOnce link |> should equal false
        System.IO.File.Exists(link + ".bak") |> should equal false
    finally
        System.IO.Directory.Delete(directory, recursive = true)

[<Fact>]
let ``a binary file survives open → projection → save byte-for-byte`` () =
    let path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    // Every byte value, including NUL (which also trips auto-detection),
    // CR, LF, and CRLF pairs that text normalization would destroy.
    let bytes = Array.init 512 (fun i -> byte (i % 256))

    try
        System.IO.File.WriteAllBytes(path, bytes)

        match Runtime.readFileForOpen path with
        | Result.Ok(LoadedBinary latin1) -> Hex.textToBytes latin1 |> should equal bytes
        | other -> failwithf "expected a binary load, got %A" other
    finally
        System.IO.File.Delete path

// ─────────────────────────────────────────────────────────────────────
// Editor integration: binary open, nibble typing, panes, save, toggle,
// replace, search.
// ─────────────────────────────────────────────────────────────────────

let private initModel () =
    let model, _ =
        Editor.init "/root" { Width = 80; Height = 24 } (Config.defaults Themes.defaultTheme) []

    model

let private press chord m =
    fst (Editor.update (KeyPressed chord) m)

let private ck c : Chord =
    { Mods = Set.ofList [ Ctrl ]
      Key = Key.Char c }

let private chr c : Chord = { Mods = Set.empty; Key = Key.Char c }
let private nk n : Chord = { Mods = Set.empty; Key = Named n }

let private typeLine (text: string) m =
    text
    |> Seq.fold (fun acc c -> press (if c = ' ' then nk Space else chr c) acc) m

/// Open latin1-projected binary content as /root/save.dat and return the
/// model (the buffer is active, registered as a hex view).
let private openBinary (latin1: string) =
    Editor.update (FileOpened("/root/save.dat", OpenPermanent, None, Result.Ok(LoadedBinary latin1))) (initModel ())
    |> fst

let private activeBuffer (model: Model) =
    model.Editors.Buffers[model.Editors.ActiveBufferId]

let private activeText model = Buffer.text (activeBuffer model)

let private cursorOffset model =
    let buffer = activeBuffer model
    Buffer.positionToIndex buffer.Cursor buffer

[<Fact>]
let ``a binary FileOpened registers a hex view and keeps every byte`` () =
    let model = openBinary (nul + ff + "AB")
    let bufferId = model.Editors.ActiveBufferId
    model.HexViews.ContainsKey bufferId |> should equal true
    activeText model |> should equal (nul + ff + "AB")
    (activeBuffer model).Newline |> should equal "\n"

[<Fact>]
let ``a text FileOpened registers no hex view`` () =
    let model, _ =
        Editor.update (FileOpened("/root/a.txt", OpenPermanent, None, Result.Ok(LoadedText "hi"))) (initModel ())

    model.HexViews.IsEmpty |> should equal true

[<Fact>]
let ``typing hex digits overwrites one nibble at a time`` () =
    let model = openBinary (nul + nul)
    let typed = model |> press (chr '4')
    // High nibble written, caret parked on the same byte for the low one.
    activeText typed |> should equal ("@" + nul)
    cursorOffset typed |> should equal 0

    let completed = typed |> press (chr '1')
    activeText completed |> should equal ("A" + nul)
    cursorOffset completed |> should equal 1

[<Fact>]
let ``typing past the end of the document appends bytes`` () =
    let model = openBinary "" |> press (chr 'f') |> press (chr 'f')
    activeText model |> should equal ff

[<Fact>]
let ``non-hex characters are ignored in the bytes pane`` () =
    let model = openBinary nul |> press (chr 'z') |> press (chr 'g')
    activeText model |> should equal nul

[<Fact>]
let ``Tab flips to the ASCII pane where typing overwrites whole bytes`` () =
    let model = openBinary (nul + nul) |> press (nk Tab)
    (model.HexViews[model.Editors.ActiveBufferId]).Pane |> should equal HexAscii

    let typed = model |> press (chr 'H') |> press (chr 'i')
    activeText typed |> should equal "Hi"
    cursorOffset typed |> should equal 2

[<Fact>]
let ``arrows move by bytes and rows and re-arm the high nibble`` () =
    // 80-wide terminal with the sidebar showing → 8 bytes per hex row.
    let model = openBinary (String.replicate 32 "x")
    let halfTyped = model |> press (chr '4')

    (halfTyped.HexViews[halfTyped.Editors.ActiveBufferId]).HighNibble
    |> should equal false

    let moved = halfTyped |> press (nk Right)
    cursorOffset moved |> should equal 1
    (moved.HexViews[moved.Editors.ActiveBufferId]).HighNibble |> should equal true

    let down = moved |> press (nk Down)
    cursorOffset down |> should equal 9
    let home = down |> press (nk Home)
    cursorOffset home |> should equal 8
    let up = home |> press (nk Up)
    cursorOffset up |> should equal 0

[<Fact>]
let ``delete and backspace remove whole bytes`` () =
    let model = openBinary "ABC" |> press (nk Delete)
    activeText model |> should equal "BC"

    let after = model |> press (nk Right) |> press (nk Backspace)
    activeText after |> should equal "C"

[<Fact>]
let ``undo restores the overwritten byte and stays in hex view`` () =
    let model = openBinary nul |> press (chr '4') |> press (ck 'z')
    activeText model |> should equal nul
    model.HexViews.ContainsKey model.Editors.ActiveBufferId |> should equal true

[<Fact>]
let ``saving a hex view emits a binary SaveBuffer with the projection intact`` () =
    let model = openBinary (nul + ff) |> press (chr '4') |> press (chr '1')
    let _, effects = Editor.update (KeyPressed(ck 's')) model

    effects
    |> List.exists (fun e ->
        match e with
        | SaveBuffer(_, "/root/save.dat", _, contents, true) -> contents = "A" + ff
        | _ -> false)
    |> should equal true

[<Fact>]
let ``a backed-up save says so in the notification`` () =
    let saved, _ =
        Editor.update (BufferSaved(1, "/root/save.dat", 0, Result.Ok BackupCreated)) (initModel ())

    match saved.Notification with
    | Some n -> n.Message |> should equal "Saved save.dat (original kept as save.dat.bak)"
    | None -> failwith "expected a save notification"

[<Fact>]
let ``a symlink save warns that its backup was skipped`` () =
    let saved, _ =
        Editor.update (BufferSaved(1, "/root/save.dat", 0, Result.Ok BackupSkippedSymlink)) (initModel ())

    saved.Notification
    |> Option.get
    |> fun note -> note.Message |> should haveSubstring "symbolic link"

[<Fact>]
let ``search prompt typing in a hex view emits a hex-flagged RunSearch`` () =
    let inSearch = openBinary (nul + "\u001a") |> press (ck 'f') |> press (chr '1')
    let _, effects = Editor.update (KeyPressed(chr 'a')) inSearch

    effects
    |> List.exists (fun e ->
        match e with
        | RunSearch(_, "1a", _, true) -> true
        | _ -> false)
    |> should equal true

[<Fact>]
let ``hex command toggles a text buffer into a byte projection and back`` () =
    let model = initModel () |> typeLine "hi"
    let toHex = model |> press (ck 'p') |> typeLine "hex" |> press (nk Enter)
    let bufferId = toHex.Editors.ActiveBufferId
    toHex.HexViews.ContainsKey bufferId |> should equal true
    activeText toHex |> should equal "hi" // UTF-8 of "hi" is the same bytes
    toHex.Focus |> should equal Editor

    let backToText = toHex |> press (ck 'p') |> typeLine "hex" |> press (nk Enter)
    backToText.HexViews.ContainsKey bufferId |> should equal false
    activeText backToText |> should equal "hi"

[<Fact>]
let ``hex toggle keeps the dirty flag but resets undo`` () =
    let model = initModel () |> typeLine "hi"
    (activeBuffer model).Dirty |> should equal true

    let toHex = model |> press (ck 'p') |> typeLine "hex" |> press (nk Enter)
    (activeBuffer toHex).Dirty |> should equal true
    (activeBuffer toHex).Undo |> List.isEmpty |> should equal true

[<Fact>]
let ``a lossy hex-to-text flip marks the buffer modified and warns`` () =
    // 0xFF alone is invalid UTF-8: the text view decodes it to U+FFFD, so
    // a save from that view would rewrite the byte. The flip must not
    // leave the buffer reading as clean.
    let model = openBinary ff
    let toText = model |> press (ck 'p') |> typeLine "hex off" |> press (nk Enter)

    toText.HexViews.IsEmpty |> should equal true
    (activeBuffer toText).Dirty |> should equal true

    match toText.Notification with
    | Some { Severity = Severity.Warning } -> ()
    | other -> failwithf "expected a warning notification, got %A" other

[<Fact>]
let ``a lossless hex-to-text flip keeps a clean buffer clean`` () =
    let model = openBinary "hi"
    let toText = model |> press (ck 'p') |> typeLine "hex off" |> press (nk Enter)

    toText.HexViews.IsEmpty |> should equal true
    (activeBuffer toText).Dirty |> should equal false

[<Fact>]
let ``replace command rewrites every occurrence as one undo step`` () =
    let model = openBinary (nul + "A" + nul + "B" + nul)

    let replaced =
        model |> press (ck 'p') |> typeLine "replace 00 ff" |> press (nk Enter)

    activeText replaced |> should equal (ff + "A" + ff + "B" + ff)
    cursorOffset replaced |> should equal 0

    let undone = replaced |> press (ck 'z')
    activeText undone |> should equal (nul + "A" + nul + "B" + nul)

[<Fact>]
let ``replace with a longer sequence grows the document`` () =
    let model = openBinary "\u001a,x"

    let replaced =
        model
        |> press (ck 'p')
        |> typeLine "replace 1a2c78 ffffffff"
        |> press (nk Enter)

    activeText replaced |> should equal (ff + ff + ff + ff)

[<Fact>]
let ``replace works on a text buffer with exact literal matching`` () =
    let model = initModel () |> typeLine "aA aA"

    let replaced =
        model |> press (ck 'p') |> typeLine "replace aA bb" |> press (nk Enter)

    activeText replaced |> should equal "bb bb"

    // One undo step restores the whole rewrite.
    activeText (replaced |> press (ck 'z')) |> should equal "aA aA"

[<Fact>]
let ``replace on a text buffer is case-sensitive unlike search`` () =
    // Search is deliberately case-insensitive; replace-all is destructive,
    // so it matches ordinally.
    let model = initModel () |> typeLine "aA"
    let replaced = model |> press (ck 'p') |> typeLine "replace a b" |> press (nk Enter)
    activeText replaced |> should equal "bA"

[<Fact>]
let ``replace on a text buffer takes hex-looking arguments literally`` () =
    let model = initModel () |> typeLine "hi 68"

    let replaced =
        model |> press (ck 'p') |> typeLine "replace 68 69" |> press (nk Enter)

    activeText replaced |> should equal "hi 69"

[<Fact>]
let ``replace in a hex view falls back to literal bytes like search`` () =
    // "cash" is not a hex byte sequence, so it matches literally; "00" is,
    // so it writes a NUL — the `/` search rule applied to both arguments.
    let model = openBinary "cash"

    let replaced =
        model |> press (ck 'p') |> typeLine "replace cash 00" |> press (nk Enter)

    activeText replaced |> should equal nul

[<Fact>]
let ``replace reports the non-overlapping count`` () =
    // "aaa" holds two overlapping "aa" matches but only one rewrite.
    let model = openBinary "aaa"

    let replaced =
        model |> press (ck 'p') |> typeLine "replace 6161 6262" |> press (nk Enter)

    activeText replaced |> should equal "bba"

    match replaced.Notification with
    | Some notification -> notification.Message |> should equal "Replaced 1 occurrence(s)."
    | None -> failwith "expected a notification"

[<Fact>]
let ``closing a hex buffer drops its view state`` () =
    let model = openBinary nul
    let bufferId = model.Editors.ActiveBufferId
    let closed = model |> press (ck 'w')
    closed.HexViews.ContainsKey bufferId |> should equal false

[<Fact>]
let ``line-reordering actions are inert in a hex view`` () =
    let model = openBinary "AB\nCD"
    let before = activeText model
    let after, _ = Editor.runAction (MoveLinesDown 1) model
    activeText after |> should equal before

[<Fact>]
let ``expand-selection is inert in a hex view even for a source file`` () =
    // A hex view of an .fs file must not feed its byte projection to the
    // tree-sitter parser: languageFor still matches the extension, so only
    // the hex intercept keeps the ladder out.
    let opened, _ =
        Editor.update (FileOpened("/root/x.fs", OpenPermanent, None, Result.Ok(LoadedText "let x = 1"))) (initModel ())

    let toHex = opened |> press (ck 'p') |> typeLine "hex" |> press (nk Enter)
    toHex.HexViews.ContainsKey toHex.Editors.ActiveBufferId |> should equal true

    let after, effects = Editor.runAction ExpandSelection toHex
    activeText after |> should equal (activeText toHex)

    effects
    |> List.exists (function
        | ComputeSelectionLadder _ -> true
        | _ -> false)
    |> should equal false

[<Fact>]
let ``rendered hex view shows offset, hex cells, and ASCII column`` () =
    let model = openBinary "this is a test, "
    let screen = Layout.render model
    let metrics = Dock.metrics model

    let rowText (row: int) =
        System.String(
            [| for col in metrics.EditorX .. metrics.EditorX + metrics.EditorWidth - 1 -> screen.Cells[row, col].Glyph |]
        )

    let firstRow = rowText 0
    firstRow.StartsWith "00000000  74 68 69 73" |> should equal true
    firstRow.Contains "this is " |> should equal true

[<Fact>]
let ``status line reads HEX with byte-space position tokens`` () =
    let model = openBinary "«Í" |> press (nk Right)
    let status = Status.render 120 model
    status.Contains "HEX" |> should equal true
    status.Contains "0x00000001" |> should equal true
    status.Contains "cd" |> should equal true
    status.Contains "BIN" |> should equal true
