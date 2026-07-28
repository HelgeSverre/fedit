module Fedit.Tests.CommandReferenceCliTests

open System.Text.Json
open Fedit
open Fedit.Cli.Commands
open FsUnit.Xunit
open Xunit

let private parsed () =
    use doc = JsonDocument.Parse(CommandReference.toJson Commands.specs)

    doc.RootElement.EnumerateArray()
    |> Seq.map (fun element -> element.Clone())
    |> Seq.toList

[<Fact>]
let ``toJson emits one row per built-in command spec`` () =
    parsed () |> List.length |> should equal Commands.specs.Length

[<Fact>]
let ``command rows carry the parser-owned reference fields`` () =
    for row in parsed () do
        for name in [ "name"; "usage"; "summary" ] do
            let value = row.GetProperty(name).GetString()
            value |> String.length |> should be (greaterThan 0)

        let hiddenKind = row.GetProperty("hidden").ValueKind

        (hiddenKind = JsonValueKind.True || hiddenKind = JsonValueKind.False)
        |> should equal true

[<Fact>]
let ``current command surface includes language and binary tools`` () =
    let names =
        parsed ()
        |> List.map (fun row -> row.GetProperty("name").GetString())
        |> Set.ofList

    for expected in [ "lsp"; "diagnostics"; "hex"; "replace"; "messages"; "close"; "reveal" ] do
        names |> Set.contains expected |> should equal true
