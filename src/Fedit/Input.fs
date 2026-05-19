namespace Fedit

open System

[<RequireQualifiedAccess>]
module Input =
    let private hasModifier modifier (keyInfo: ConsoleKeyInfo) = keyInfo.Modifiers.HasFlag modifier

    let tryMap (keyInfo: ConsoleKeyInfo) =
        let hasAlt = hasModifier ConsoleModifiers.Alt keyInfo
        let hasCtrl = hasModifier ConsoleModifiers.Control keyInfo
        let hasShift = hasModifier ConsoleModifiers.Shift keyInfo

        if hasShift && not hasCtrl && not hasAlt then
            match keyInfo.Key with
            | ConsoleKey.LeftArrow -> Some ShiftLeft
            | ConsoleKey.RightArrow -> Some ShiftRight
            | ConsoleKey.UpArrow -> Some ShiftUp
            | ConsoleKey.DownArrow -> Some ShiftDown
            | ConsoleKey.Home -> Some ShiftHome
            | ConsoleKey.End -> Some ShiftEnd
            | ConsoleKey.Tab -> Some ShiftTab
            | _ -> None
        elif hasAlt && not hasCtrl then
            match keyInfo.Key with
            | ConsoleKey.LeftArrow -> Some AltLeft
            | ConsoleKey.RightArrow -> Some AltRight
            | ConsoleKey.UpArrow -> Some AltUp
            | ConsoleKey.DownArrow -> Some AltDown
            | _ -> None
        elif hasCtrl then
            match keyInfo.Key with
            | ConsoleKey.A -> Some(Ctrl 'a')
            | ConsoleKey.B -> Some(Ctrl 'b')
            | ConsoleKey.C -> Some(Ctrl 'c')
            | ConsoleKey.E -> Some(Ctrl 'e')
            | ConsoleKey.F -> Some(Ctrl 'f')
            | ConsoleKey.P -> Some(Ctrl 'p')
            | ConsoleKey.Q -> Some(Ctrl 'q')
            | ConsoleKey.R -> Some(Ctrl 'r')
            | ConsoleKey.S -> Some(Ctrl 's')
            | ConsoleKey.V -> Some(Ctrl 'v')
            | ConsoleKey.X -> Some(Ctrl 'x')
            | ConsoleKey.Y -> Some(Ctrl 'y')
            | ConsoleKey.Z -> Some(Ctrl 'z')
            | ConsoleKey.Backspace -> Some CtrlBackspace
            | ConsoleKey.Delete -> Some CtrlDelete
            | _ -> None
        else
            match keyInfo.Key with
            | ConsoleKey.Enter -> Some Enter
            | ConsoleKey.Escape -> Some Escape
            | ConsoleKey.Backspace -> Some Backspace
            | ConsoleKey.Delete -> Some Delete
            | ConsoleKey.Tab when hasModifier ConsoleModifiers.Shift keyInfo -> Some ShiftTab
            | ConsoleKey.Tab -> Some Tab
            | ConsoleKey.LeftArrow -> Some Left
            | ConsoleKey.RightArrow -> Some Right
            | ConsoleKey.UpArrow -> Some Up
            | ConsoleKey.DownArrow -> Some Down
            | ConsoleKey.Home -> Some Home
            | ConsoleKey.End -> Some End
            | ConsoleKey.PageUp -> Some PageUp
            | ConsoleKey.PageDown -> Some PageDown
            | _ when Char.IsControl keyInfo.KeyChar -> None
            | _ -> Some(Character keyInfo.KeyChar)
