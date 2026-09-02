namespace Showcase

open System.Threading
open System.Threading.Tasks
open Fedit.PluginApi

/// Reference plugin for the extension surface. Each command exercises one
/// capability so the end-to-end tests (PluginHostTests) can drive them
/// through the real out-of-process host.
module Plugin =
    let register (host: IPluginHost) =
        // A styled dock panel plus a status-bar item.
        host.RegisterCommand
            { Name = "showcase-panel"
              Usage = "showcase-panel"
              Summary = "Show a styled panel and a status item."
              Run =
                fun ctx ->
                    let lines =
                        [ [ { Text = ctx.ActiveBuffer.Name
                              Style = TextStyle.Accent }
                            { Text = "  active"
                              Style = TextStyle.Muted } ]
                          [ { Text = "keyword"
                              Style = TextStyle.Keyword }
                            { Text = " "; Style = TextStyle.Plain }
                            { Text = "\"string\""
                              Style = TextStyle.String } ]
                          [ { Text = "error"
                              Style = TextStyle.Error }
                            { Text = " warning"
                              Style = TextStyle.Warning } ] ]

                    [ ShowPanel("Showcase", lines)
                      SetStatusItem(Some $"{ctx.AllBuffers.Length} buffers") ] }

        // An async command: waits for the delay in the argument-free
        // context text ("sleep <ms>"), honouring cancellation.
        host.RegisterAsyncCommand
            { Name = "showcase-slow"
              Usage = "showcase-slow"
              Summary = "Sleep for the milliseconds named in the buffer text, then report."
              RunAsync =
                fun ctx (token: CancellationToken) ->
                    task {
                        let delay =
                            match System.Int32.TryParse(ctx.ActiveBuffer.Text.Trim()) with
                            | true, ms -> ms
                            | _ -> 50

                        do! Task.Delay(delay, token)
                        return [ Notify(Info, $"slept {delay}ms") ]
                    } }

        host.RegisterCommand
            { Name = "showcase-fast"
              Usage = "showcase-fast"
              Summary = "Return immediately."
              Run = fun _ -> [ Notify(Info, "fast") ] }
