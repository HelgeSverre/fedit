namespace Journal

open Fedit.PluginApi

/// Inserts a local-time timestamp at the cursor.
/// Demonstrates: side-effect via `InsertText` + a follow-up `Notify`.
module Plugin =
    let register (host: IPluginHost) =
        host.RegisterCommand
            { Name = "journal"
              Usage = "journal"
              Summary = "Insert the current local timestamp at the cursor."
              Run =
                fun _ctx ->
                    let stamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                    [ InsertText $"[{stamp}] "; Notify(Info, $"Stamped {stamp}") ] }
