namespace Fedit

open System

/// Path handling is canonical forward-slash everywhere inside fedit — the
/// git/LSP convention. Paths are normalized to `/` at every OS boundary (tree
/// scan, file open, workspace root) so comparisons, the plugin API, the file
/// picker, and tests behave identically on Windows, macOS, and Linux. .NET's
/// file APIs accept `/` on Windows, so normalized paths still do real I/O.
[<RequireQualifiedAccess>]
module Paths =
    /// Canonical separator: collapse `\` to `/`. A no-op on Unix.
    let norm (path: string) : string =
        if String.IsNullOrEmpty path then
            path
        else
            path.Replace('\\', '/')

    /// Parent directory using the canonical `/` separator (unlike
    /// Path.GetDirectoryName, which emits the OS separator). None at the root.
    let parent (path: string) : string option =
        match path.LastIndexOf '/' with
        | i when i > 0 -> Some(path.Substring(0, i))
        | _ -> None

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
            // The write gets its own scope so the handle is closed before the
            // move. `use` disposes at the end of its enclosing block, so writing
            // and moving in one block would rename a file we still hold open —
            // which POSIX permits but Windows rejects with "used by another
            // process" (doubly so at FileShare.None).
            do
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

    /// Safety net for binary overwrites: copy `path` to `path + ".bak"`
    /// unless a backup already exists, so the first hex-view save keeps
    /// the original bytes recoverable. Never clobbers an existing `.bak`.
    /// Returns true when a backup was created by this call.
    let backupOnce (path: string) =
        let fullPath = Path.GetFullPath path
        let bakPath = fullPath + ".bak"

        if File.Exists fullPath && not (File.Exists bakPath) then
            File.Copy(fullPath, bakPath)
            true
        else
            false

    /// Byte-exact sibling of `writeAllTextAtomic` — hex-view buffers save
    /// through here so binary files round-trip without an encoding pass.
    let writeAllBytesAtomic (path: string) (bytes: byte[]) =
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
            // Own scope so the handle closes before the move — see
            // `writeAllTextAtomic` for why Windows requires it.
            do
                use stream =
                    new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                stream.Write(bytes, 0, bytes.Length)
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
    /// Cap for the notification ring buffer (`Model.NotificationLog`):
    /// the `:messages` picker reviews the most recent entries only.
    let logLimit = 100

    let info message = { Severity = Info; Message = message }

    let warning message =
        { Severity = Warning
          Message = message }

    let error message = { Severity = Error; Message = message }
