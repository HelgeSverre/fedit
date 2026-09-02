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

    serverNames config |> should equal [ "sema"; "typescript"; "rust"; "pyright" ]

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

    serverNames config |> should equal [ "sema"; "typescript"; "rust"; "pyright" ]

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

    serverNames config
    |> should equal [ "sema"; "typescript"; "rust"; "pyright"; "gopls" ]

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

    serverNames config
    |> should equal [ "sema"; "typescript"; "rust"; "pyright"; "good" ]

[<Fact>]
let ``a languageServers value that is not an object leaves the defaults intact`` () =
    let config = loadJson """{ "languageServers": [ "sema" ] }"""

    serverNames config |> should equal [ "sema"; "typescript"; "rust"; "pyright" ]

[<Fact>]
let ``resource limits use defaults and allow explicit unlimited values`` () =
    let defaults = loadJson "{}"
    defaults.ResourceLimits |> should equal ResourceLimits.defaults

    let configured =
        loadJson
            """{ "resourceLimits": { "lspIncomingMessageBytes": null, "lspDocumentChars": 123, "lspLocationCount": null, "lspPreviewScanBytes": 456, "lspPreviewChars": 789, "lspPreviewConcurrency": 99, "lspPreviewTimeoutMs": null } }"""

    configured.ResourceLimits.LspIncomingMessageBytes |> should equal None
    configured.ResourceLimits.LspDocumentChars |> should equal (Some 123)
    configured.ResourceLimits.LspLocationCount |> should equal None
    configured.ResourceLimits.LspPreviewScanBytes |> should equal (Some 456)
    configured.ResourceLimits.LspPreviewChars |> should equal 789
    configured.ResourceLimits.LspPreviewConcurrency |> should equal 16
    configured.ResourceLimits.LspPreviewTimeoutMs |> should equal None

[<Fact>]
let ``save preserves the user's resource limits block`` () =
    let configPath = tempConfigPath ()
    Directory.CreateDirectory(Path.GetDirectoryName configPath) |> ignore
    File.WriteAllText(configPath, """{ "resourceLimits": { "lspDocumentChars": null } }""")

    try
        let loaded, _ = ConfigIO.loadFrom configPath []
        ConfigIO.saveTo configPath loaded
        let reloaded, error = ConfigIO.loadFrom configPath []
        error |> should equal None
        reloaded.ResourceLimits.LspDocumentChars |> should equal None
    finally
        File.Delete configPath

[<Fact>]
let ``invalid resource limits warn and fall back to defaults`` () =
    let configPath = tempConfigPath ()
    Directory.CreateDirectory(Path.GetDirectoryName configPath) |> ignore
    File.WriteAllText(configPath, """{ "resourceLimits": { "lspDocumentChars": -1 } }""")

    try
        let loaded, warning = ConfigIO.loadFrom configPath []

        loaded.ResourceLimits.LspDocumentChars
        |> should equal ResourceLimits.defaults.LspDocumentChars

        warning |> Option.get |> should haveSubstring "lspDocumentChars"
    finally
        File.Delete configPath

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

        serverNames reloaded
        |> should equal [ "sema"; "typescript"; "rust"; "pyright"; "gopls" ]
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

[<Theory>]
[<InlineData("/work/app.py")>]
[<InlineData("/work/stub.pyi")>]
[<InlineData("/work/gui.pyw")>]
let ``serverForFile routes python sources to pyright`` (path: string) =
    LanguageServers.serverForFile LanguageServers.defaults path
    |> Option.map (fun server -> server.Name)
    |> should equal (Some "pyright")

// -- languageIdFor ----------------------------------------------------------

[<Theory>]
// Python: every extension the server owns reports the one spec id, not `py`.
[<InlineData("/work/app.py", "python")>]
[<InlineData("/work/stub.pyi", "python")>]
[<InlineData("/work/gui.pyw", "python")>]
// The id follows the document, so a multi-language server labels each file
// correctly rather than tagging them all with its first configured extension.
[<InlineData("/work/util.js", "javascript")>]
[<InlineData("/work/mod.mjs", "javascript")>]
[<InlineData("/work/main.ts", "typescript")>]
[<InlineData("/work/View.tsx", "typescriptreact")>]
[<InlineData("/work/View.jsx", "javascriptreact")>]
[<InlineData("/work/lib.rs", "rust")>]
// Extensions with no mapping pass through unchanged — what a user-configured
// server for an unknown language expects.
[<InlineData("/work/main.sema", "sema")>]
[<InlineData("/work/query.nim", "nim")>]
let ``languageIdFor derives the id from the document`` (path: string) (expected: string) =
    let server =
        { Name = "irrelevant"
          Command = "irrelevant"
          Args = []
          FileTypes = [ "zz" ]
          RootMarkers = [] }

    LanguageServers.languageIdFor path server |> should equal expected

[<Fact>]
let ``languageIdFor falls back to the server name without an extension`` () =
    let server =
        { Name = "somelang"
          Command = "somelang-lsp"
          Args = []
          FileTypes = [ "sl" ]
          RootMarkers = [] }

    LanguageServers.languageIdFor "/work/README" server |> should equal "somelang"

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

// -- statusFormat migration -------------------------------------------------

[<Fact>]
let ``a persisted pre-LSP default statusFormat migrates to the current default`` () =
    // Every pre-LSP build persisted statusFormat unconditionally, so
    // upgraded configs carry the exact old default — without migration the
    // [DIAGNOSTICS] segment would stay hidden forever.
    let config =
        loadJson
            """{ "statusFormat": "[MODE]  [CURRENT_FILE:short][DIRTY] <EXPAND> [NOTIFICATION]  [LINE]:[COLUMN]  [LINE_ENDING]  [BUFFER]" }"""

    config.StatusFormat
    |> should equal (Config.defaults Themes.defaultTheme).StatusFormat

    config.StatusFormat |> should haveSubstring "[DIAGNOSTICS]"

[<Fact>]
let ``a customized statusFormat is preserved verbatim on load`` () =
    let config = loadJson """{ "statusFormat": "[MODE] <EXPAND> [LINE]" }"""
    config.StatusFormat |> should equal "[MODE] <EXPAND> [LINE]"

[<Fact>]
let ``ignore rules round-trip through save and default sensibly`` () =
    let configPath = tempConfigPath ()
    Directory.CreateDirectory(Path.GetDirectoryName configPath) |> ignore

    try
        File.WriteAllText(configPath, "{}")
        let defaults, _ = ConfigIO.loadFrom configPath []
        defaults.Ignore |> should equal Ignore.defaults

        let custom =
            { defaults with
                Ignore =
                    { Names = [ ".git"; "target" ]
                      UseGitignore = false } }

        ConfigIO.saveTo configPath custom
        let reloaded, error = ConfigIO.loadFrom configPath []
        error |> should equal None
        reloaded.Ignore |> should equal custom.Ignore
    finally
        File.Delete configPath

[<Fact>]
let ``languages block parses grammars and query overrides`` () =
    let config =
        loadJson
            """{ "languages": {
                   "vue": { "extensions": [".vue"], "library": "/g/libtree-sitter-vue.dylib", "queries": "/g/vue" },
                   "json": { "queries": "/g/json" },
                   "bad": "not an object",
                   " ": { "library": "/x" } } }"""

    config.Languages
    |> should
        equal
        [ { Name = "vue"
            Extensions = [ ".vue" ]
            Library = Some "/g/libtree-sitter-vue.dylib"
            Symbol = None
            Queries = Some "/g/vue" }
          { Name = "json"
            Extensions = []
            Library = None
            Symbol = None
            Queries = Some "/g/json" } ]

    Config.languageExtensions config |> should equal (Map.ofList [ ".vue", "vue" ])

[<Fact>]
let ``languageExtensions normalizes case and the leading dot`` () =
    let config =
        { Config.defaults Themes.defaultTheme with
            Languages =
                [ { Name = "vue"
                    Extensions = [ "VUE"; ".Vuex" ]
                    Library = None
                    Symbol = None
                    Queries = None } ] }

    Config.languageExtensions config
    |> should equal (Map.ofList [ ".vue", "vue"; ".vuex", "vue" ])

[<Fact>]
let ``plugins block gives each plugin its own stringified settings`` () =
    let config =
        loadJson
            """{ "plugins": { "showcase": { "greeting": "hi", "limit": 3, "strict": true, "nested": { "x": 1 } },
                              "bad": "not an object" } }"""

    config.PluginSettings
    |> should equal (Map.ofList [ "showcase", Map.ofList [ "greeting", "hi"; "limit", "3"; "strict", "true" ] ])
