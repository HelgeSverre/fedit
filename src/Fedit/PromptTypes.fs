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

type PromptPendingConfirmation =
    { ItemId: string option
      ActionId: PickerActionId
      Key: Chord
      Label: string }
