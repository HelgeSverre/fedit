namespace Wordcount

open Fedit.PluginApi

/// Counts whitespace-separated tokens in the active buffer.
/// Demonstrates: reading `ActiveBuffer.Text`; returning a single `Notify` action.
module Plugin =
    let private countWords (text: string) =
        if System.String.IsNullOrWhiteSpace text then
            0
        else
            text.Split([| ' '; '\t'; '\n'; '\r' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.length

    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "wc"
              Usage = "wc"
              Summary = "Count words in the active buffer."
              Run =
                fun ctx ->
                    let n = countWords ctx.ActiveBuffer.Text
                    [ Notify(Info, $"{n} words") ] }
