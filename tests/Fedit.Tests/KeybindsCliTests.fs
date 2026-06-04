module Fedit.Tests.KeybindsCliTests

open Fedit
open Fedit.Cli.Commands
open System.Text.Json
open FSharp.Reflection
open Xunit
open FsUnit.Xunit

let private parsed () =
    use doc = JsonDocument.Parse(Keybinds.toJson Keymap.defaults)
    doc.RootElement.EnumerateArray() |> Seq.map (fun e -> e.Clone()) |> Seq.toList

let private boundActionNames =
    Keymap.defaults
    |> List.choose (fun b -> b.Action)
    |> List.map Keybinds.actionName
    |> Set.ofList

let private unboundActions =
    Keybinds.allActions
    |> List.filter (fun a -> not (boundActionNames.Contains(Keybinds.actionName a)))

[<Fact>]
let ``toJson emits a row per default binding plus every unbound action`` () =
    let bound = Keymap.defaults |> List.filter (fun b -> b.Action.IsSome) |> List.length

    parsed () |> List.length |> should equal (bound + unboundActions.Length)

[<Fact>]
let ``bound rows carry stroke and context; unbound rows leave them empty`` () =
    for row in parsed () do
        // action / category / description are present and non-empty on every row.
        for name in [ "action"; "category"; "description" ] do
            let mutable prop = Unchecked.defaultof<JsonElement>
            row.TryGetProperty(name, &prop) |> should equal true
            prop.ValueKind |> should equal JsonValueKind.String
            prop.GetString() |> String.length |> should be (greaterThan 0)

        let bound = row.GetProperty("bound").GetBoolean()
        let stroke = row.GetProperty("stroke").GetString()
        let context = row.GetProperty("context").GetString()

        if bound then
            stroke |> String.length |> should be (greaterThan 0)
            context |> String.length |> should be (greaterThan 0)
        else
            stroke |> should equal ""
            context |> should equal ""

[<Fact>]
let ``json includes at least one unbound action`` () =
    parsed ()
    |> List.filter (fun row -> not (row.GetProperty("bound").GetBoolean()))
    |> List.length
    |> should be (greaterThan 0)

[<Fact>]
let ``every default action yields a non-empty actionName`` () =
    Keymap.defaults
    |> List.choose (fun b -> b.Action)
    |> List.iter (fun a -> Keybinds.actionName a |> String.length |> should be (greaterThan 0))

[<Fact>]
let ``allActions covers every Action case with a unique name`` () =
    let caseTag (a: Action) =
        let info, _ = FSharpValue.GetUnionFields(a, typeof<Action>)
        info.Tag

    let coveredTags = Keybinds.allActions |> List.map caseTag |> Set.ofList

    let allTags =
        FSharpType.GetUnionCases(typeof<Action>)
        |> Array.map (fun c -> c.Tag)
        |> Set.ofArray

    coveredTags |> should equal allTags

    Keybinds.allActions
    |> List.map Keybinds.actionName
    |> List.distinct
    |> List.length
    |> should equal Keybinds.allActions.Length
