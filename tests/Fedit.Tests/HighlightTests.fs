module Fedit.Tests.HighlightTests

open Xunit

[<Fact>]
let ``TreeSitter.DotNet types are reachable`` () =
    let parserType = typeof<TreeSitter.Parser>
    Assert.Equal("Parser", parserType.Name)
