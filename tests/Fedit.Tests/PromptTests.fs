module Fedit.Tests.PromptTests

open Fedit
open Xunit
open FsUnit.Xunit

[<Fact>]
let ``modeOf returns FilePicker for empty text`` () =
    Prompt.modeOf "" |> should equal FilePicker

[<Fact>]
let ``modeOf maps prefix characters to their modes`` () =
    Prompt.modeOf ":theme" |> should equal PromptMode.Command
    Prompt.modeOf ":42" |> should equal PromptMode.Command
    Prompt.modeOf "/foo" |> should equal PromptMode.Search
    Prompt.modeOf "@1" |> should equal PromptMode.Buffers

[<Fact>]
let ``modeOf treats > as a FilePicker query (no longer reserved)`` () =
    Prompt.modeOf ">theme" |> should equal FilePicker

[<Fact>]
let ``modeOf falls back to FilePicker for non-prefix characters`` () =
    Prompt.modeOf "Model" |> should equal FilePicker
    Prompt.modeOf "42" |> should equal FilePicker

[<Fact>]
let ``argumentOf strips the prefix character`` () =
    Prompt.argumentOf ":theme green" |> should equal "theme green"
    Prompt.argumentOf ":100:6" |> should equal "100:6"
    Prompt.argumentOf "/needle" |> should equal "needle"
    Prompt.argumentOf "@buf" |> should equal "buf"

[<Fact>]
let ``argumentOf is identity for empty or non-prefix input`` () =
    Prompt.argumentOf "" |> should equal ""
    Prompt.argumentOf "Model.fs" |> should equal "Model.fs"
    Prompt.argumentOf ">stuff" |> should equal ">stuff"
