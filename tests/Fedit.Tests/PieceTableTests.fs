module Fedit.Tests.PieceTableTests

open Fedit
open Xunit
open FsUnit.Xunit
open FsCheck

[<Fact>]
let ``empty piece table has length zero`` () =
    PieceTable.empty |> PieceTable.length |> should equal 0

[<Fact>]
let ``ofString builds a piece table whose toString roundtrips`` () =
    let pt = PieceTable.ofString "hello world"
    PieceTable.toString pt |> should equal "hello world"
    PieceTable.length pt |> should equal 11

[<Fact>]
let ``ofString empty string yields empty table`` () =
    let pt = PieceTable.ofString ""
    PieceTable.toString pt |> should equal ""
    PieceTable.length pt |> should equal 0

[<Fact>]
let ``insert at zero prepends`` () =
    let pt = PieceTable.ofString "world" |> PieceTable.insert 0 "hello "
    PieceTable.toString pt |> should equal "hello world"

[<Fact>]
let ``insert at end appends`` () =
    let pt = PieceTable.ofString "hello" |> PieceTable.insert 5 " world"
    PieceTable.toString pt |> should equal "hello world"

[<Fact>]
let ``insert in the middle splits the piece`` () =
    let pt = PieceTable.ofString "hello world" |> PieceTable.insert 5 " cruel"
    PieceTable.toString pt |> should equal "hello cruel world"

[<Fact>]
let ``insert empty string is a no-op`` () =
    let pt = PieceTable.ofString "hello" |> PieceTable.insert 3 ""
    PieceTable.toString pt |> should equal "hello"

[<Fact>]
let ``deleteRange removes a contiguous span`` () =
    let pt = PieceTable.ofString "hello world" |> PieceTable.deleteRange 5 6
    PieceTable.toString pt |> should equal "hello"

[<Fact>]
let ``deleteRange in the middle preserves both sides`` () =
    let pt = PieceTable.ofString "hello cruel world" |> PieceTable.deleteRange 5 6
    PieceTable.toString pt |> should equal "hello world"

[<Fact>]
let ``deleteRange with zero count is a no-op`` () =
    let pt = PieceTable.ofString "hello" |> PieceTable.deleteRange 2 0
    PieceTable.toString pt |> should equal "hello"

[<Fact>]
let ``deleteRange clamps past end`` () =
    let pt = PieceTable.ofString "hello" |> PieceTable.deleteRange 3 100
    PieceTable.toString pt |> should equal "hel"

[<Fact>]
let ``insert then delete the same span is identity`` () =
    let original = PieceTable.ofString "abcdef"
    let inserted = original |> PieceTable.insert 3 "XYZ"
    let restored = inserted |> PieceTable.deleteRange 3 3
    PieceTable.toString restored |> should equal "abcdef"

[<Fact>]
let ``sequential appends coalesce into one added piece`` () =
    let pt =
        "abc"
        |> Seq.fold (fun t (c: char) -> PieceTable.insert (PieceTable.length t) (string c) t) PieceTable.empty

    PieceTable.toString pt |> should equal "abc"
    pt.Pieces.Length |> should equal 1

[<Fact>]
let ``sequential mid-document typing coalesces into one added piece`` () =
    let pt =
        PieceTable.ofString "xy"
        |> PieceTable.insert 1 "a"
        |> PieceTable.insert 2 "b"
        |> PieceTable.insert 3 "c"

    PieceTable.toString pt |> should equal "xabcy"
    pt.Pieces.Length |> should equal 3

[<Fact>]
let ``insert away from the added tail does not coalesce`` () =
    // "a" is the Added tail, but the second insert lands at index 0 —
    // not adjacent to it — so it must become its own piece.
    let pt = PieceTable.empty |> PieceTable.insert 0 "a" |> PieceTable.insert 0 "b"

    PieceTable.toString pt |> should equal "ba"
    pt.Pieces.Length |> should equal 2

// FsCheck properties are driven through Check.QuickThrowOnFailure from
// plain facts: FsCheck.Xunit's [<Property>] discoverer is silently
// skipped by this project's runner combination (xunit 2.9.3 +
// xunit.runner.visualstudio 3.x) — attribute-marked properties compile
// but never execute.

[<Fact>]
let ``length is consistent with toString length`` () =
    Check.QuickThrowOnFailure(fun (text: string) ->
        let safe = if isNull text then "" else text
        let pt = PieceTable.ofString safe
        PieceTable.length pt = (PieceTable.toString pt).Length)

[<Fact>]
let ``insert grows length by the inserted string's length`` () =
    Check.QuickThrowOnFailure(fun (text: string) (insert: string) (index: int) ->
        let safeText = if isNull text then "" else text
        let safeInsert = if isNull insert then "" else insert
        let pt = PieceTable.ofString safeText
        let original = PieceTable.length pt
        let safeIdx = ((index % (original + 1)) + (original + 1)) % (original + 1)
        let inserted = PieceTable.insert safeIdx safeInsert pt
        PieceTable.length inserted = original + safeInsert.Length)

[<Fact>]
let ``append insert always concatenates`` () =
    Check.QuickThrowOnFailure(fun (text: string) (suffix: string) ->
        let safeText = if isNull text then "" else text
        let safeSuffix = if isNull suffix then "" else suffix
        let pt = PieceTable.ofString safeText
        let appended = PieceTable.insert (PieceTable.length pt) safeSuffix pt
        PieceTable.toString appended = safeText + safeSuffix)

// --- Model-based properties: a plain .NET string is the reference ---

[<Fact>]
let ``insert matches String.Insert at the clamped index`` () =
    Check.QuickThrowOnFailure(fun (text: string) (insert: string) (index: int) ->
        let safeText = if isNull text then "" else text
        let safeInsert = if isNull insert then "" else insert
        // `insert` clamps the index into [0, length] before splicing.
        let clamped = max 0 (min index safeText.Length)
        let pt = PieceTable.ofString safeText |> PieceTable.insert index safeInsert
        PieceTable.toString pt = safeText.Insert(clamped, safeInsert))

/// `deleteRange`'s documented clamping, reproduced with plain string ops:
/// non-positive counts are a no-op, the start clamps to >= 0, the end
/// clamps to the text length, and an empty clamped span is a no-op.
let private referenceDelete (text: string) start count =
    if count <= 0 then
        text
    else
        let startIndex = max 0 start
        let endIndex = min text.Length (startIndex + count)

        if endIndex <= startIndex then
            text
        else
            text.Remove(startIndex, endIndex - startIndex)

[<Fact>]
let ``deleteRange matches String.Remove with the same clamping`` () =
    Check.QuickThrowOnFailure(fun (text: string) (start: int) (count: int) ->
        let safeText = if isNull text then "" else text
        let pt = PieceTable.ofString safeText |> PieceTable.deleteRange start count
        PieceTable.toString pt = referenceDelete safeText start count)

[<Fact>]
let ``a random op sequence stays in sync with a plain-string model`` () =
    Check.QuickThrowOnFailure(fun (initial: string) (ops: (bool * int * int * string) list) ->
        let safeInitial = if isNull initial then "" else initial

        let step (pt, (reference: string)) (isInsert, index, count, payload) =
            if isInsert then
                let text = if isNull payload then "" else payload
                let clamped = max 0 (min index reference.Length)
                PieceTable.insert index text pt, reference.Insert(clamped, text)
            else
                PieceTable.deleteRange index count pt, referenceDelete reference index count

        let pt, reference =
            ops
            |> List.truncate 10
            |> List.fold step (PieceTable.ofString safeInitial, safeInitial)

        PieceTable.toString pt = reference && PieceTable.length pt = reference.Length)

// --- Shared append-only add buffer: snapshots must be immune to later edits ---

[<Fact>]
let ``tail coalescing does not disturb the snapshot it extends from`` () =
    // t2's insert coalesces into the Added tail piece that t1 also holds —
    // the shared buffer grows, but t1's (Start, Length) range is untouched.
    let t1 = PieceTable.empty |> PieceTable.insert 0 "ab"
    let t2 = t1 |> PieceTable.insert 2 "c"

    PieceTable.toString t1 |> should equal "ab"
    PieceTable.toString t2 |> should equal "abc"
    t2.Pieces.Length |> should equal 1

[<Fact>]
let ``divergent inserts from a shared snapshot keep both branches intact`` () =
    // Both branches append to the one shared add buffer; each addresses only
    // its own ranges, so neither sees the other's text.
    let shared = PieceTable.ofString "core" |> PieceTable.insert 4 "-x"
    let branchA = shared |> PieceTable.insert 6 "-aaa"
    let branchB = shared |> PieceTable.insert 0 "b-" |> PieceTable.insert 2 "B"

    PieceTable.toString shared |> should equal "core-x"
    PieceTable.toString branchA |> should equal "core-x-aaa"
    PieceTable.toString branchB |> should equal "b-Bcore-x"

[<Fact>]
let ``every historical snapshot still renders after later edits on any branch`` () =
    // Driven through Check from a plain fact: FsCheck.Xunit's
    // [<Property>] discoverer is silently skipped by this project's
    // runner combination (xunit 2.9.3 + xunit.runner.visualstudio 3.x).
    Check.QuickThrowOnFailure(fun (initial: string) (ops: (bool * int * int * string) list) ->
        let safeInitial = if isNull initial then "" else initial

        let step (pt, (reference: string)) (isInsert, index, count, payload) =
            if isInsert then
                let text = if isNull payload then "" else payload
                let clamped = max 0 (min index reference.Length)
                PieceTable.insert index text pt, reference.Insert(clamped, text)
            else
                PieceTable.deleteRange index count pt, referenceDelete reference index count

        let snapshots =
            ops
            |> List.truncate 10
            |> List.scan step (PieceTable.ofString safeInitial, safeInitial)

        // Branch a divergent insert off EVERY snapshot — each appends to the
        // lineage's shared add buffer without disturbing any other snapshot.
        let branches =
            snapshots
            |> List.mapi (fun i (pt, reference) ->
                let marker = sprintf "<%d>" i
                PieceTable.insert 0 marker pt, marker + reference)

        snapshots @ branches
        |> List.forall (fun (pt, reference) -> PieceTable.toString pt = reference))
