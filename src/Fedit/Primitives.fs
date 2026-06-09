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

module Text =
    let optStr (s: string | null) =
        match s with
        | null -> None
        | value -> Some value

module File =
    open System.IO
    open System.Text

    let writeAllTextAtomic (path: string) (contents: string) =
        let fullPath = Path.GetFullPath path

        let directory =
            Path.GetDirectoryName fullPath
            |> Text.optStr
            |> Option.defaultValue Environment.CurrentDirectory

        Directory.CreateDirectory directory |> ignore

        let fileName =
            Path.GetFileName fullPath |> Text.optStr |> Option.defaultValue "file"

        let tempPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp")

        try
            use stream =
                new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

            use writer = new StreamWriter(stream, UTF8Encoding false)
            writer.Write contents
            writer.Flush()
            stream.Flush true
            File.Move(tempPath, fullPath, overwrite = true)
        with _ ->
            try
                if File.Exists tempPath then
                    File.Delete tempPath
            with _ ->
                ()

            reraise ()

module Notification =
    let info message = { Severity = Info; Message = message }

    let warning message =
        { Severity = Warning
          Message = message }

    let error message = { Severity = Error; Message = message }
