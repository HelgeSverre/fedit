namespace Fedit

open System.IO

/// File location + load for the user keybinds file. Sibling of `ConfigIO`;
/// named `KeymapIO` so it doesn't collide with the `Keymap` type/module.
/// Always returns a working keymap (floored on `Keymap.defaults`) plus an
/// error list, so the editor boots on a missing or broken file.
[<RequireQualifiedAccess>]
module KeymapIO =

    let path () =
        Path.Combine(ConfigIO.directory (), "keybinds")

    /// Read the file if present, parse each line, and overlay the valid user
    /// bindings on `Keymap.defaults`. A missing file is not an error (§7).
    let load () : Keymap * string list =
        try
            let p = path ()

            if not (File.Exists p) then
                Keymap.defaults, []
            else
                Keymap.parse (File.ReadAllLines p)
        with ex ->
            Keymap.defaults, [ $"keybinds: {ex.Message}" ]
