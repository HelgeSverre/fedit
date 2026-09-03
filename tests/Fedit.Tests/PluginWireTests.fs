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
      MoveLinesDown 2
      ShowPanel(
          "Panel",
          [ [ { Text = "plain"
                Style = TextStyle.Plain }
              { Text = "accent"
                Style = TextStyle.Accent }
              { Text = "muted"
                Style = TextStyle.Muted }
              { Text = "err"
                Style = TextStyle.Error }
              { Text = "warn"
                Style = TextStyle.Warning }
              { Text = "kw"
                Style = TextStyle.Keyword }
              { Text = "str"
                Style = TextStyle.String } ]
            [] ]
      )
      SetStatusItem(Some "3 todos")
      SetStatusItem None
      ShowPicker(
          "Pick",
          [ { Id = "a"
              Title = "A"
              Subtitle = None }
            { Id = "b"
              Title = "B"
              Subtitle = Some "second" } ],
          "picked"
      )
      PromptInput("Name", "draft", "answer")
      SetDecorations(
          1,
          [ { Line = 3
              Gutter = Some "!"
              Text = Some "todo"
              Style = TextStyle.Warning }
            { Line = 5
              Gutter = None
              Text = None
              Style = TextStyle.Plain } ]
      ) ]

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
              Selection = Some({ Line = 1; Column = 1 }, { Line = 1; Column = 4 })
              Language = Some "fsharp"
              Dirty = true
              EditTick = 7
              Diagnostics =
                [ { Severity = Warning
                    Message = "unused"
                    Source = Some "fsac"
                    Start = { Line = 1; Column = 5 }
                    End = { Line = 1; Column = 6 } } ] }
          AllBuffers =
            [ { Id = 2
                Name = "scratch"
                FilePath = None
                Text = ""
                Cursor = { Line = 1; Column = 1 }
                Selection = None
                Language = None
                Dirty = false
                EditTick = 0
                Diagnostics = [] } ]
          Workspace =
            { RootPath = "/tmp"
              SelectedPath = None
              Files = [ "a.fs"; "b/c.fs" ] }
          Event = Some BufferSaved
          Config = Map.ofList [ "indent", "4"; "strict", "true" ]
          Argument = Some "foo bar" }

    // The host reads what the editor wrote: full round trip, every field.
    let json = PluginWire.contextToJson ctx
    use parsed = System.Text.Json.JsonDocument.Parse json
    PluginWire.readContext parsed.RootElement |> should equal ctx

    use doc = System.Text.Json.JsonDocument.Parse json
    let root = doc.RootElement

    root.GetProperty("activeBuffer").GetProperty("filePath").GetString()
    |> should equal "/tmp/a.fs"

    root.GetProperty("activeBuffer").GetProperty("selection").ValueKind
    |> should equal System.Text.Json.JsonValueKind.Object

    root.GetProperty("allBuffers").GetArrayLength() |> should equal 1
