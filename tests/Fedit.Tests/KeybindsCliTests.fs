module Fedit.Tests.KeybindsCliTests

open Fedit
open Fedit.Cli.Commands
open System.Text.Json
open Xunit
open FsUnit.Xunit

let private parsed () =
    use doc = JsonDocument.Parse(Keybinds.toJson Keymap.defaults)
    doc.RootElement.EnumerateArray() |> Seq.map (fun e -> e.Clone()) |> Seq.toList

[<Fact>]
let ``toJson emits one row per default binding with a concrete action`` () =
    let expected =
        Keymap.defaults |> List.filter (fun b -> b.Action.IsSome) |> List.length

    parsed () |> List.length |> should equal expected

[<Fact>]
let ``every emitted row has all five fields as non-empty strings`` () =
    for row in parsed () do
        for name in [ "stroke"; "action"; "context"; "category"; "description" ] do
            let mutable prop = Unchecked.defaultof<JsonElement>
            row.TryGetProperty(name, &prop) |> should equal true
            prop.ValueKind |> should equal JsonValueKind.String
            prop.GetString() |> String.length |> should be (greaterThan 0)

[<Fact>]
let ``every default action yields a non-empty actionName`` () =
    Keymap.defaults
    |> List.choose (fun b -> b.Action)
    |> List.iter (fun a -> Keybinds.actionName a |> String.length |> should be (greaterThan 0))
