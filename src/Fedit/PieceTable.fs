namespace Fedit

open System
open System.Text

/// Append-only text storage shared by every PieceTable snapshot in a
/// document's lineage. Pieces address immutable (Start, Length) ranges:
/// appends only ever extend the end, so ranges captured by older snapshots
/// (undo/redo stacks, in-flight effects) stay valid forever — including
/// ranges abandoned by an undo branch, which simply become dead space.
/// The lock makes reads safe from effect tasks (`RunSearch` and
/// `ParseHighlight` call `PieceTable.toString` on pool threads) while the
/// single writer (the update thread, via `insert`) appends.
[<Sealed>]
type AddBuffer() =
    let builder = StringBuilder()
    let gate = obj ()
    member _.Length = lock gate (fun () -> builder.Length)

    member _.Append(text: string) =
        lock gate (fun () -> builder.Append text |> ignore)

    member _.Substring(start: int, length: int) =
        lock gate (fun () -> builder.ToString(start, length))

type PieceSource =
    | Original
    | Added

[<Struct>]
type Piece =
    { Source: PieceSource
      Start: int
      Length: int }

type PieceTable =
    {
        Original: string
        /// The shared append-only add buffer — `None` until the first
        /// insert. Created lazily per lineage so the module-level `empty`
        /// value is never mutated; divergent undo/redo branches share one
        /// builder, and because it is append-only every branch's
        /// (Start, Length) ranges stay valid.
        Added: AddBuffer option
        Pieces: Piece list
    }

[<RequireQualifiedAccess>]
module PieceTable =
    let empty =
        { Original = ""
          Added = None
          Pieces = [] }

    let ofString (text: string) =
        if String.IsNullOrEmpty text then
            empty
        else
            { Original = text
              Added = None
              Pieces =
                [ { Source = Original
                    Start = 0
                    Length = text.Length } ] }

    let private slice table piece =
        match piece.Source with
        | Original -> table.Original.Substring(piece.Start, piece.Length)
        | Added ->
            match table.Added with
            | Some added -> added.Substring(piece.Start, piece.Length)
            | None -> "" // unreachable: Added pieces only exist after `insert` set the buffer

    let toString table =
        table.Pieces |> List.map (slice table) |> String.concat ""

    let length table = table.Pieces |> List.sumBy _.Length

    let private trim piece startOffset endOffset =
        let nextLength = piece.Length - startOffset - endOffset

        if nextLength <= 0 then
            None
        else
            Some
                { piece with
                    Start = piece.Start + startOffset
                    Length = nextLength }

    let insert index (text: string) table =
        if String.IsNullOrEmpty text then
            table
        else
            let index = max 0 (min index (length table))

            // Get-or-create the lineage's shared add buffer (lazy so the
            // module-level `empty` value is never mutated).
            let added =
                match table.Added with
                | Some added -> added
                | None -> AddBuffer()

            // Captured BEFORE the append below: every piece built here
            // addresses the pre-append end of the buffer.
            let addStart = added.Length

            let insertedPiece =
                { Source = Added
                  Start = addStart
                  Length = text.Length }

            // Sequential typing inserts at the end of the piece written by the
            // previous keystroke. When that piece is also the tail of `Added`,
            // the appended text is contiguous with it — grow the piece in
            // place instead of adding one piece per keystroke.
            let extendsAddedTail piece =
                piece.Source = Added && piece.Start + piece.Length = addStart

            let withExtendedHead acc =
                match acc with
                | last :: before when extendsAddedTail last ->
                    Some(
                        { last with
                            Length = last.Length + text.Length }
                        :: before
                    )
                | _ -> None

            let rec loop remaining acc pieces =
                match pieces with
                | [] ->
                    match withExtendedHead acc with
                    | Some extended -> List.rev extended
                    | None -> List.rev acc @ [ insertedPiece ]
                | piece :: rest when remaining = 0 ->
                    match withExtendedHead acc with
                    | Some extended -> List.rev extended @ (piece :: rest)
                    | None -> List.rev acc @ (insertedPiece :: piece :: rest)
                | piece :: rest when remaining < piece.Length ->
                    let left = trim piece 0 (piece.Length - remaining)
                    let right = trim piece remaining 0

                    List.rev acc
                    @ (left |> Option.toList)
                    @ [ insertedPiece ]
                    @ (right |> Option.toList)
                    @ rest
                | piece :: rest -> loop (remaining - piece.Length) (piece :: acc) rest

            let nextPieces = loop index [] table.Pieces

            // Deliberate benign mutation of the shared buffer: appending
            // never disturbs ranges held by other snapshots, and if this
            // result is discarded the appended text is merely dead space.
            added.Append text

            { table with
                Added = Some added
                Pieces = nextPieces }

    let deleteRange index count table =
        if count <= 0 then
            table
        else
            let startIndex = max 0 index
            let endIndex = min (length table) (startIndex + count)

            if endIndex <= startIndex then
                table
            else
                let rec loop position pieces =
                    match pieces with
                    | [] -> []
                    | piece :: rest ->
                        let pieceStart = position
                        let pieceEnd = position + piece.Length
                        let tail = loop pieceEnd rest

                        if pieceEnd <= startIndex || pieceStart >= endIndex then
                            piece :: tail
                        else
                            let left =
                                let keep = max 0 (startIndex - pieceStart)

                                if keep > 0 then
                                    trim piece 0 (piece.Length - keep) |> Option.toList
                                else
                                    []

                            let right =
                                let keep = max 0 (pieceEnd - endIndex)

                                if keep > 0 then
                                    trim piece (piece.Length - keep) 0 |> Option.toList
                                else
                                    []

                            left @ right @ tail

                { table with
                    Pieces = loop 0 table.Pieces }
