namespace Fedit

open System

[<Struct>]
type Size = { Width: int; Height: int }

[<Struct>]
type Position = { Line: int; Column: int }

type Severity =
    | Info
    | Warning
    | Error

type Notification = { Severity: Severity; Message: string }

type FocusTarget =
    | Sidebar
    | Editor
    | Prompt

type KeyInput =
    | Character of char
    | Enter
    | Escape
    | Backspace
    | Delete
    | Tab
    | ShiftTab
    | Left
    | Right
    | Up
    | Down
    | Home
    | End
    | PageUp
    | PageDown
    | Ctrl of char
    | AltLeft
    | AltRight
    | AltUp
    | AltDown
    | CtrlBackspace
    | CtrlDelete
    | CtrlPageUp
    | CtrlPageDown
    /// Ctrl + a digit row key (0..9). Used for buffer jumps.
    | CtrlDigit of int
    | ShiftLeft
    | ShiftRight
    | ShiftUp
    | ShiftDown
    | ShiftHome
    | ShiftEnd

type IconMode =
    /// Default — ASCII `[+] / [-] / 4-space` markers.
    | IconsOff
    /// Nerd Font PUA glyphs (requires a Nerd Font in the user's terminal).
    | IconsNerd

type CompletionKind =
    | Command
    | PathItem

type CompletionItem =
    { Label: string
      ApplyText: string
      Detail: string
      Kind: CompletionKind }

type DockPanel =
    | NoDock
    | DockInfo of title: string * lines: string list
    | DockCompletions of title: string * items: CompletionItem list * selectedIndex: int

module Position =
    let zero = { Line = 0; Column = 0 }

module Notification =
    let info message = { Severity = Info; Message = message }

    let warning message =
        { Severity = Warning
          Message = message }

    let error message = { Severity = Error; Message = message }
