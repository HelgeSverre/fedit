namespace Journal

open Fedit.PluginApi

/// Inserts a local-time timestamp at the cursor, then reveals the stamped
/// file in the sidebar so the tree follows the work.
/// Demonstrates: `InsertText` + `RevealPath` + a follow-up `Notify`.
/// `RevealPath` is skipped for scratch buffers (no file path to reveal).
module Plugin =
    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "journal"
              Usage = "journal"
              Summary = "Insert the current local timestamp at the cursor."
              Run =
                fun ctx ->
                    let stamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")

                    [ yield InsertText $"[{stamp}] "
                      match ctx.ActiveBuffer.FilePath with
                      | Some path -> yield RevealPath path
                      | None -> ()
                      yield Notify(Info, $"Stamped {stamp}") ] }
