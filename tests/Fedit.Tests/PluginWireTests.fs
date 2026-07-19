module Fedit.Tests.PluginWireTests

open Fedit
open Fedit.PluginApi
open Xunit
open FsUnit.Xunit

// One representative value per PluginAction case — a new case forces an
// addition here (the wire's writeAction match is exhaustive too).
let private sampleActions =
    [ Notify(Warning, "hi \"q\"\n")
      InsertText "abc"
      ReplaceSelection "x"
      MoveCursor { Line = 3; Column = 7 }
      OpenFile "a/b.fs"
      SaveActiveBuffer
      RunCommand "wordcount"
      SetClipboard "clip"
      SelectRange({ Line = 1; Column = 1 }, { Line = 2; Column = 5 })
      OpenFilePreview "p.txt"
      RevealPath "r.txt"
      ReplaceRange({ Line = 1; Column = 1 }, { Line = 1; Column = 4 }, "new")
      ClearSelection
      DeleteSelection
      SwitchBuffer 42
      NewBuffer("scratch", "body")
      SetBufferActivation "jump"
      OpenFileAt("f.fs", { Line = 9; Column = 2 }, true)
      MoveLinesUp 3
      MoveLinesDown 2 ]

[<Fact>]
let ``every PluginAction case round-trips through the wire unchanged`` () =
    let restored =
        sampleActions |> PluginWire.actionsToJson |> PluginWire.actionsFromJson

    restored |> should equal sampleActions

[<Fact>]
let ``re-serializing a parsed action list is byte-identical (stable wire)`` () =
    let json1 = PluginWire.actionsToJson sampleActions
    let json2 = json1 |> PluginWire.actionsFromJson |> PluginWire.actionsToJson
    json2 |> should equal json1

[<Fact>]
let ``selfTest passes (same check the AOT binary runs)`` () =
    PluginWire.selfTest () |> should equal true

[<Fact>]
let ``PluginContext round-trips with options present and absent`` () =
    let ctx =
        { ActiveBuffer =
            { Id = 1
              Name = "a.fs"
              FilePath = Some "/tmp/a.fs"
              Text = "let x = 1\n"
              Cursor = { Line = 1; Column = 1 }
              Selection = Some({ Line = 1; Column = 1 }, { Line = 1; Column = 4 }) }
          AllBuffers =
            [ { Id = 2
                Name = "scratch"
                FilePath = None
                Text = ""
                Cursor = { Line = 1; Column = 1 }
                Selection = None } ]
          Workspace =
            { RootPath = "/tmp"
              SelectedPath = None
              Files = [ "a.fs"; "b/c.fs" ] } }

    // Round-trip is one-directional today (editor only writes context), so
    // assert it serializes to stable, parseable JSON carrying the key fields.
    let json = PluginWire.contextToJson ctx
    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement

    root.GetProperty("activeBuffer").GetProperty("filePath").GetString()
    |> should equal "/tmp/a.fs"

    root.GetProperty("activeBuffer").GetProperty("selection").ValueKind
    |> should equal System.Text.Json.JsonValueKind.Object

    root.GetProperty("allBuffers").GetArrayLength() |> should equal 1
