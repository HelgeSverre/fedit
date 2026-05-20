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
