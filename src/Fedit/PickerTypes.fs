namespace Fedit.PickerTypes

open System
open Fedit

/// Identifies the kind of picker (domain-specific).
type PickerKind =
    | PluginPicker
    | MacroPicker
    | KeyBindingPicker
    | MessagePicker
    /// LSP locations (definitions, references, diagnostics): one row per
    /// location, Enter jumps. Rows come from `Model.Lsp.Locations`.
    | LocationPicker
    /// The `:lsp` manager: one row per configured language server.
    | LanguageServerPicker

/// Presentation style for a picker kind.
type PickerLayout =
    | ListWithInspector
    | SearchResults

/// Semantic role for a badge, determining its styling.
type PickerBadgeRole =
    | Neutral
    | Success
    | Warning
    | Danger
    | Muted

/// A small metadata label attached to a picker item.
type PickerBadge =
    { Label: string; Role: PickerBadgeRole }

/// Small semantic metadata attached to a picker item (trailing).
type PickerAccessory =
    | TextAccessory of string
    | CountAccessory of label: string * value: int
    | ShortcutAccessory of Chord

/// One line in a picker item's inspector detail panel.
type PickerInspectorLine =
    | TextLine of string
    | PathLine of string
    | ErrorLine of string

/// Structured detail content for the selected item in ListWithInspector layout.
type PickerInspector =
    { Title: string
      Subtitle: string option
      Lines: PickerInspectorLine list }

/// Unique identifier for a picker action.
type PickerActionId =
    | PluginEnable
    | PluginDisable
    | PluginReloadAll
    | PluginUninstall
    | MacroReplay
    | MacroRecord
    | MacroMarkLast
    | MacroClear
    | MacroEdit
    | MessagesClear
    | LocationJump
    | LanguageServerRestart
    | LanguageServerToggle
    | LanguageServerLog
    | PickerClose

/// Semantic role for a picker action, determining its styling.
type PickerActionRole =
    | Primary
    | Secondary
    | Destructive

/// Current enabled/disabled state of a picker action.
type PickerActionState =
    | Enabled
    | Disabled of reason: string

/// Optional confirmation requirement for a destructive action.
type PickerConfirmation = { Label: string }

/// What should happen to the picker after an action executes.
type PickerDismissal =
    | KeepOpen
    | Close
    | Refresh

/// A selectable action available for the selected item or picker.
type PickerAction =
    { Id: PickerActionId
      Key: Chord
      Label: string
      Role: PickerActionRole
      State: PickerActionState
      Confirmation: PickerConfirmation option
      Dismissal: PickerDismissal }

/// A selectable item in the picker.
type PickerItem =
    { Id: string
      Title: string
      Subtitle: string option
      Badge: PickerBadge option
      Accessories: PickerAccessory list
      Inspector: PickerInspector option
      SearchTerms: string list
      Actions: PickerAction list }

/// Pending confirmation state for a destructive action.
type PickerPendingConfirmation =
    { ItemId: string option
      ActionId: PickerActionId
      Key: Chord
      Label: string }

/// Interaction state for a picker, excluding generated content.
type PickerState =
    { Kind: PickerKind
      SelectedItemId: string option
      Filter: string
      PendingConfirmation: PickerPendingConfirmation option }

/// Footer content for the picker view.
type PickerFooter =
    | ActionFooter of PickerAction list
    | ConfirmationFooter of label: string * key: Chord

/// Render-ready semantic data for a picker, produced from Model + PickerState.
type PickerView =
    { Title: string
      Layout: PickerLayout
      Filter: string
      Items: PickerItem list
      SelectedIndex: int
      EmptyText: string
      Footer: PickerFooter }
