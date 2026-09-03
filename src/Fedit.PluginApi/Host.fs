namespace Fedit.PluginApi

/// The capability surface a plugin sees during `register`. The fedit host
/// supplies an implementation that collects the plugin's registrations
/// into a per-plugin registry.
type IPluginHost =
    /// Register a named command. First registration wins on name collision;
    /// duplicates are logged as conflicts.
    abstract member RegisterCommand: command: PluginCommand -> unit

    /// Bind a keyboard chord to a command name. Reserved chords (basic
    /// character input) are rejected with a logged warning. The command
    /// referenced must exist by the time the chord fires — typically a
    /// command the same plugin registered above.
    abstract member RegisterKeybinding: chord: KeyChord * commandName: string -> unit

    /// Append a line to the plugin host's log. Useful for debugging.
    abstract member Log: message: string -> unit

    /// Register an asynchronous command (see `PluginAsyncCommand`). Shares
    /// the command namespace with `RegisterCommand`.
    abstract member RegisterAsyncCommand: command: PluginAsyncCommand -> unit

    /// Run `commandName` (a command this plugin registered) whenever
    /// `event` happens. The command receives the usual snapshot with
    /// `PluginContext.Event = Some event`.
    abstract member RegisterHook: event: PluginEvent * commandName: string -> unit

    /// Offer a language server. Merged like config.json's `languageServers`;
    /// the user's own entry of the same name takes precedence, and the user
    /// can disable it from `:lsp` like any other server.
    abstract member RegisterLanguageServer: server: LanguageServerSpec -> unit

    /// Ship a tree-sitter grammar and its queries. Loaded lazily on the
    /// first file of that language; a user `languages` entry of the same
    /// name wins.
    abstract member RegisterLanguage: grammar: GrammarSpec -> unit

    /// Read the system clipboard. Usable from inside a command's `Run`
    /// (it asks the editor and waits); throws when the editor cannot
    /// answer, e.g. outside a running editor.
    abstract member ReadClipboard: unit -> string
