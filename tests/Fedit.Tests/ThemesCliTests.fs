module Fedit.Tests.ThemesCliTests

open Fedit
open Fedit.Cli.Commands
open System.Text.Json
open Xunit
open FsUnit.Xunit

let private parsed () =
    use doc = JsonDocument.Parse(Themes.toJson Themes.all)
    doc.RootElement.EnumerateArray() |> Seq.map (fun e -> e.Clone()) |> Seq.toList

let private byName name =
    parsed () |> List.find (fun e -> e.GetProperty("name").GetString() = name)

[<Fact>]
let ``toJson emits one object per bundled theme`` () =
    parsed () |> List.length |> should equal (List.length Themes.all)

[<Fact>]
let ``every theme carries name, accent and a syntax object`` () =
    for row in parsed () do
        row.GetProperty("name").GetString()
        |> String.length
        |> should be (greaterThan 0)

        row.GetProperty("accent").ValueKind |> should equal JsonValueKind.String
        row.GetProperty("syntax").ValueKind |> should equal JsonValueKind.Object

[<Fact>]
let ``github-light is a light theme with an opaque white editor surface`` () =
    let gh = byName "github-light"
    gh.GetProperty("appearance").GetString() |> should equal "light"
    gh.GetProperty("surfaceBg").GetString() |> should equal "#FFFFFF"

[<Fact>]
let ``dark themes leave the editor background at the terminal default (null)`` () =
    let green = byName "green"
    green.GetProperty("appearance").GetString() |> should equal "dark"
    green.GetProperty("surfaceBg").ValueKind |> should equal JsonValueKind.Null
    // but concrete chrome the terminal renders is present
    green.GetProperty("surfaceFg").ValueKind |> should equal JsonValueKind.String
