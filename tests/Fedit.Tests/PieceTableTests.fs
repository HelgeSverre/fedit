module Fedit.Tests.PieceTableTests

open Fedit
open Xunit
open FsUnit.Xunit
open FsCheck.Xunit

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

[<Property>]
let ``length is consistent with toString length`` (text: string) =
    let safe = if isNull text then "" else text
    let pt = PieceTable.ofString safe
    PieceTable.length pt = (PieceTable.toString pt).Length

[<Property>]
let ``insert grows length by the inserted string's length`` (text: string) (insert: string) (index: int) =
    let safeText = if isNull text then "" else text
    let safeInsert = if isNull insert then "" else insert
    let pt = PieceTable.ofString safeText
    let original = PieceTable.length pt
    let safeIdx = ((index % (original + 1)) + (original + 1)) % (original + 1)
    let inserted = PieceTable.insert safeIdx safeInsert pt
    PieceTable.length inserted = original + safeInsert.Length

[<Property>]
let ``append insert always concatenates`` (text: string) (suffix: string) =
    let safeText = if isNull text then "" else text
    let safeSuffix = if isNull suffix then "" else suffix
    let pt = PieceTable.ofString safeText
    let appended = PieceTable.insert (PieceTable.length pt) safeSuffix pt
    PieceTable.toString appended = safeText + safeSuffix
