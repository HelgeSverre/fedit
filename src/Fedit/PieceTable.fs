namespace Fedit

open System

type PieceSource =
    | Original
    | Added

[<Struct>]
type Piece =
    { Source: PieceSource
      Start: int
      Length: int }

type PieceTable =
    { Original: string
      Added: string
      Pieces: Piece list }

[<RequireQualifiedAccess>]
module PieceTable =
    let empty =
        { Original = ""
          Added = ""
          Pieces = [] }

    let ofString (text: string) =
        if String.IsNullOrEmpty text then
            empty
        else
            { Original = text
              Added = ""
              Pieces =
                [ { Source = Original
                    Start = 0
                    Length = text.Length } ] }

    let private slice table piece =
        let source =
            match piece.Source with
            | Original -> table.Original
            | Added -> table.Added

        source.Substring(piece.Start, piece.Length)

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

            let insertedPiece =
                { Source = Added
                  Start = table.Added.Length
                  Length = text.Length }

            let rec loop remaining acc pieces =
                match pieces with
                | [] -> List.rev acc @ [ insertedPiece ]
                | piece :: rest when remaining = 0 -> List.rev acc @ (insertedPiece :: piece :: rest)
                | piece :: rest when remaining < piece.Length ->
                    let left = trim piece 0 (piece.Length - remaining)
                    let right = trim piece remaining 0

                    List.rev acc
                    @ (left |> Option.toList)
                    @ [ insertedPiece ]
                    @ (right |> Option.toList)
                    @ rest
                | piece :: rest -> loop (remaining - piece.Length) (piece :: acc) rest

            { table with
                Added = table.Added + text
                Pieces = loop index [] table.Pieces }

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
