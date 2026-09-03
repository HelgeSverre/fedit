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

        // A language server offered to the editor: merged like a config.json
        // entry, so `:lsp` lists it and the user can disable it.
        host.RegisterLanguageServer
            { Name = "showcase-ls"
              Command = "showcase-language-server"
              Args = [ "--stdio" ]
              FileTypes = [ "showcase" ]
              RootMarkers = [ ".showcase-root" ] }

        // A grammar shipped in the plugin folder (paths are folder-relative).
        // Loads lazily on the first `.showcase` file; missing files just
        // leave those files unstyled.
        host.RegisterLanguage
            { Name = "showcase"
              Extensions = [ ".showcase" ]
              Library = "grammars/libtree-sitter-showcase.dylib"
              Symbol = Some "tree_sitter_toml"
              Queries = Some "grammars/showcase" }

        // Decorations: mark every line containing TODO with a gutter glyph
        // and virtual text; run again on a change to keep them in step.
        host.RegisterCommand
            { Name = "showcase-decorate"
              Usage = "showcase-decorate"
              Summary = "Mark TODO lines in the active buffer."
              Run =
                fun ctx ->
                    let lines = ctx.ActiveBuffer.Text.Split('\n')

                    let decorations =
                        [ for index in 0 .. lines.Length - 1 do
                              if lines[index].Contains "TODO" then
                                  { Line = index + 1
                                    Gutter = Some "!"
                                    Text = Some "todo here"
                                    Style = TextStyle.Warning } ]

                    [ SetDecorations(ctx.ActiveBuffer.Id, decorations) ] }

        // A read-back: ask the editor for the clipboard mid-run.
        host.RegisterCommand
            { Name = "showcase-clipboard"
              Usage = "showcase-clipboard"
              Summary = "Report the clipboard contents."
              Run =
                fun _ ->
                    let clipboard = host.ReadClipboard()
                    [ Notify(Info, $"clipboard: {clipboard}") ] }

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
