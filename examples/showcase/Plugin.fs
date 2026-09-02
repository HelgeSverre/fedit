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

        // An event hook: after every save, report what was saved. The
        // context's Event says why the command ran; ActiveBuffer is the
        // saved buffer even when another one has focus.
        host.RegisterCommand
            { Name = "showcase-on-save"
              Usage = "showcase-on-save"
              Summary = "Hook: note the last saved file in the status bar."
              Run =
                fun ctx ->
                    match ctx.Event with
                    | Some BufferSaved -> [ SetStatusItem(Some $"saved {ctx.ActiveBuffer.Name}") ]
                    | _ -> [ Notify(Info, "not a save event") ] }

        host.RegisterHook(BufferSaved, "showcase-on-save")

        // A picker: choosing a row runs `showcase-picked` with the row id.
        host.RegisterCommand
            { Name = "showcase-pick"
              Usage = "showcase-pick"
              Summary = "Open a picker of the open buffers."
              Run =
                fun ctx ->
                    let rows =
                        [ for buffer in ctx.AllBuffers ->
                              { Id = string buffer.Id
                                Title = buffer.Name
                                Subtitle = buffer.FilePath } ]

                    [ ShowPicker("Buffers", rows, "showcase-picked") ] }

        host.RegisterCommand
            { Name = "showcase-picked"
              Usage = "showcase-picked <id>"
              Summary = "Report the chosen picker row (or the typed argument)."
              Run =
                fun ctx ->
                    let picked = defaultArg ctx.Argument "nothing"
                    [ Notify(Info, $"picked {picked}") ] }

        // A text prompt: Enter runs `showcase-answer` with the typed text.
        host.RegisterCommand
            { Name = "showcase-ask"
              Usage = "showcase-ask"
              Summary = "Ask for a name."
              Run = fun _ -> [ PromptInput("Name", "", "showcase-answer") ] }

        host.RegisterCommand
            { Name = "showcase-answer"
              Usage = "showcase-answer <text>"
              Summary = "Greet the submitted name."
              Run =
                fun ctx ->
                    let name = defaultArg ctx.Argument "stranger"
                    [ Notify(Info, $"hello {name}") ] }

        // The enriched snapshot: language, dirty flag, diagnostics, and the
        // plugin's own `plugins.showcase` settings from config.json.
        host.RegisterCommand
            { Name = "showcase-context"
              Usage = "showcase-context"
              Summary = "Report what the snapshot knows about the active buffer."
              Run =
                fun ctx ->
                    let buffer = ctx.ActiveBuffer
                    let language = defaultArg buffer.Language "?"
                    let greeting = defaultArg (Map.tryFind "greeting" ctx.Config) "hello"

                    [ Notify(
                          Info,
                          $"{greeting}: {language} dirty={buffer.Dirty} tick={buffer.EditTick} diagnostics={buffer.Diagnostics.Length}"
                      ) ] }
