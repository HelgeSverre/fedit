module Fedit.Tests.ConfigTests

open System.IO
open Fedit
open Xunit
open FsUnit.Xunit

// ConfigIO.loadFrom/saveTo against a throwaway path — the real user config
// in ~/.config/fedit is never touched.

let private tempConfigPath () =
    Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "config.json")

let private loadJson (json: string) : Config =
    let configPath = tempConfigPath ()
    Directory.CreateDirectory(Path.GetDirectoryName configPath) |> ignore
    File.WriteAllText(configPath, json)

    try
        let config, error = ConfigIO.loadFrom configPath []
        error |> should equal None
        config
    finally
        File.Delete configPath

let private serverNames (config: Config) =
    config.LanguageServers |> List.map (fun server -> server.Name)

// -- languageServers parsing ------------------------------------------------

[<Fact>]
let ``built-in language servers are present when config has no languageServers key`` () =
    let config = loadJson "{}"

    serverNames config |> should equal [ "sema"; "typescript"; "rust" ]

    let sema = config.LanguageServers |> List.find (fun server -> server.Name = "sema")
    sema.Command |> should equal "sema"
    sema.Args |> should equal [ "lsp" ]
    sema.FileTypes |> should equal [ "sema" ]
    sema.RootMarkers |> should equal [ "sema.toml" ]

[<Fact>]
let ``a user languageServers entry named like a default replaces it entirely`` () =
    let config =
        loadJson
            """{ "languageServers": { "sema": { "command": "/opt/sema/bin/sema", "args": ["lsp", "--verbose"], "fileTypes": ["sema", "sm"], "roots": ["sema.toml", ".git"] } } }"""

    serverNames config |> should equal [ "sema"; "typescript"; "rust" ]

    let sema = config.LanguageServers |> List.find (fun server -> server.Name = "sema")
    sema.Command |> should equal "/opt/sema/bin/sema"
    sema.Args |> should equal [ "lsp"; "--verbose" ]
    sema.FileTypes |> should equal [ "sema"; "sm" ]
    sema.RootMarkers |> should equal [ "sema.toml"; ".git" ]

[<Fact>]
let ``a replacing user entry does not inherit fields from the default`` () =
    // Replacement is wholesale: omitting "args" means no args, not ["lsp"].
    let config =
        loadJson """{ "languageServers": { "sema": { "command": "my-sema" } } }"""

    let sema = config.LanguageServers |> List.find (fun server -> server.Name = "sema")
    sema.Command |> should equal "my-sema"
    sema.Args |> should equal List.empty<string>
    sema.FileTypes |> should equal List.empty<string>
    sema.RootMarkers |> should equal List.empty<string>

[<Fact>]
let ``an extra user language server extends the built-in set`` () =
    let config =
        loadJson
            """{ "languageServers": { "gopls": { "command": "gopls", "fileTypes": ["go"], "roots": ["go.mod"] } } }"""

    serverNames config |> should equal [ "sema"; "typescript"; "rust"; "gopls" ]

    let gopls =
        config.LanguageServers |> List.find (fun server -> server.Name = "gopls")

    gopls.Command |> should equal "gopls"
    gopls.Args |> should equal List.empty<string>
    gopls.FileTypes |> should equal [ "go" ]
    gopls.RootMarkers |> should equal [ "go.mod" ]

[<Fact>]
let ``malformed languageServers entries are skipped`` () =
    let config =
        loadJson
            """{ "languageServers": { "broken": "not an object", "nocommand": { "args": ["x"] }, "blankcommand": { "command": "  " }, "good": { "command": "good-ls", "fileTypes": ["g"] } } }"""

    serverNames config |> should equal [ "sema"; "typescript"; "rust"; "good" ]

[<Fact>]
let ``a languageServers value that is not an object leaves the defaults intact`` () =
    let config = loadJson """{ "languageServers": [ "sema" ] }"""

    serverNames config |> should equal [ "sema"; "typescript"; "rust" ]

// -- disabledLanguageServers persistence ------------------------------------

[<Fact>]
let ``disabledLanguageServers round-trips through save`` () =
    let configPath = tempConfigPath ()

    let config =
        { Config.defaults Themes.defaultTheme with
            DisabledLanguageServers = Set.ofList [ "typescript"; "sema" ] }

    try
        ConfigIO.saveTo configPath config
        let loaded, error = ConfigIO.loadFrom configPath []

        error |> should equal None

        loaded.DisabledLanguageServers
        |> should equal (Set.ofList [ "sema"; "typescript" ])
    finally
        File.Delete configPath

[<Fact>]
let ``save preserves a user's languageServers block`` () =
    // The editor never writes "languageServers"; read-modify-write must
    // carry it through a save untouched.
    let configPath = tempConfigPath ()
    Directory.CreateDirectory(Path.GetDirectoryName configPath) |> ignore

    File.WriteAllText(
        configPath,
        """{ "languageServers": { "gopls": { "command": "gopls", "fileTypes": ["go"], "roots": ["go.mod"] } } }"""
    )

    try
        let loaded, _ = ConfigIO.loadFrom configPath []
        ConfigIO.saveTo configPath loaded
        let reloaded, error = ConfigIO.loadFrom configPath []

        error |> should equal None
        serverNames reloaded |> should equal [ "sema"; "typescript"; "rust"; "gopls" ]
    finally
        File.Delete configPath

// -- serverForFile ----------------------------------------------------------

[<Fact>]
let ``serverForFile matches the extension case-insensitively`` () =
    let matched =
        LanguageServers.serverForFile LanguageServers.defaults "/work/app/Component.TSX"

    matched
    |> Option.map (fun server -> server.Name)
    |> should equal (Some "typescript")

[<Fact>]
let ``serverForFile is None for unknown or missing extensions`` () =
    LanguageServers.serverForFile LanguageServers.defaults "/work/notes.txt"
    |> should equal (None: LanguageServerConfig option)

    LanguageServers.serverForFile LanguageServers.defaults "/work/README"
    |> should equal (None: LanguageServerConfig option)

    // A dotfile's name is not an extension.
    LanguageServers.serverForFile LanguageServers.defaults "/work/.sema"
    |> should equal (None: LanguageServerConfig option)

// -- findWorkspaceRoot ------------------------------------------------------

let private markerIn (existing: string list) = fun path -> List.contains path existing

[<Fact>]
let ``findWorkspaceRoot picks the directory with a marker beside the file`` () =
    LanguageServers.findWorkspaceRoot
        (markerIn [ "/work/project/src/sema.toml" ])
        [ "sema.toml" ]
        "/work/project/src/main.sema"
        "/fallback"
    |> should equal "/work/project/src"

[<Fact>]
let ``findWorkspaceRoot walks up to a marker in a parent directory`` () =
    LanguageServers.findWorkspaceRoot
        (markerIn [ "/work/project/sema.toml" ])
        [ "sema.toml" ]
        "/work/project/src/deep/main.sema"
        "/fallback"
    |> should equal "/work/project"

[<Fact>]
let ``findWorkspaceRoot prefers the nearest marker`` () =
    LanguageServers.findWorkspaceRoot
        (markerIn [ "/work/sema.toml"; "/work/project/sema.toml" ])
        [ "sema.toml" ]
        "/work/project/src/main.sema"
        "/fallback"
    |> should equal "/work/project"

[<Fact>]
let ``findWorkspaceRoot matches any of several markers`` () =
    LanguageServers.findWorkspaceRoot
        (markerIn [ "/work/app/package.json" ])
        [ "tsconfig.json"; "package.json" ]
        "/work/app/src/index.ts"
        "/fallback"
    |> should equal "/work/app"

[<Fact>]
let ``findWorkspaceRoot falls back to the workspace root when no marker exists`` () =
    LanguageServers.findWorkspaceRoot (fun _ -> false) [ "sema.toml" ] "/work/project/src/main.sema" "/work"
    |> should equal "/work"
