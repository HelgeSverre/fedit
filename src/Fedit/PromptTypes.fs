namespace Fedit.PromptTypes

open Fedit
open Fedit.PickerTypes

type PromptSessionKind =
    | FileOpenSession
    | CommandSession
    | SearchSession
    | BufferSwitchSession
    | PluginsSession
    | MacrosSession
    | KeybindingsSession
    | MessagesSession
    | LocationsSession
    | LanguageServersSession
    /// A plugin picker (`ShowPicker`).
    | PluginItemsSession
    /// A plugin's free-text prompt (`PromptInput`): no completions, no
    /// mode switching; Enter submits the text to the plugin.
    | PluginInputSession

type PromptPendingConfirmation =
    { ItemId: string option
      ActionId: PickerActionId
      Key: Chord
      Label: string }
