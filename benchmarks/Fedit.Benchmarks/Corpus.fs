module Fedit.Benchmarks.Corpus

open System.Text

/// Deterministic source-like text shared by every benchmark so before/after
/// runs compare identical work. No randomness: line `i` renders the bank
/// template at `i % bank.Length` with the line number substituted in.
let lineBank =
    [| "module Generated.Block%d ="
       "    let value%d = compute %d + offset"
       "    // walks the cache and folds line %d into the accumulator"
       "    let mutable counter%d = 0"
       "    match lookup %d table with"
       "    | Some hit -> hit.Length + %d"
       "    | None -> fallback (%d * 31)"
       "    let formatted%d = sprintf \"item %d of %d\" idx"
       "    for index in 0 .. %d do"
       "        counter%d <- counter%d + index"
       "    type Record%d = { Id: int; Name: string; Tick: int }"
       "    let private helper%d input = input |> List.map ((+) %d)" |]

/// The `i`-th corpus line (without trailing newline).
let line (i: int) =
    lineBank[i % lineBank.Length].Replace("%d", string i)

/// Exactly `size` chars of '\n'-separated pseudo-F#. The final line may be
/// cut mid-token; both the line splitter and tree-sitter tolerate that.
let generate (size: int) : string =
    let builder = StringBuilder(size + 128)
    let mutable i = 0

    while builder.Length < size do
        builder.Append(line i).Append('\n') |> ignore
        i <- i + 1

    builder.ToString(0, size)
