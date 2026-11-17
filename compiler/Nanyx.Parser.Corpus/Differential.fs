/// Two parsers run over the same pieces of the corpus and their results compared: the check each stage of the
/// parser's rewrite is held to (see the Work in progress page "The parser").
module Nanyx.Parser.Differential

open Parser.Program
open Nanyx.Parser.Corpus

/// A parser under comparison: a file's path and source in, its items and trivia or its error out, as
/// `parseModuleWithTrivia` gives them.
type ModuleParser = string -> string -> Result<SourceModule, ParseError>

/// A piece the two parsers read differently, with each one's result written out (see `SyntaxDump.ofParse`).
type Difference = { Piece: Piece; Reference: string; Candidate: string }

/// The pieces of `pieces` that `candidate` reads differently from `reference`: a different tree, a span or a
/// comment in a different place, or a different error. The pieces are read in parallel, and the differences are in
/// the order of `pieces`.
let differences (reference: ModuleParser) (candidate: ModuleParser) (pieces: Piece list) : Difference list =
    pieces
    |> Array.ofList
    |> Array.Parallel.choose (fun piece ->
        let expected = SyntaxDump.ofParse (reference piece.Name piece.Source)
        let actual = SyntaxDump.ofParse (candidate piece.Name piece.Source)
        if expected = actual then None else Some { Piece = piece; Reference = expected; Candidate = actual })
    |> List.ofArray

/// Where `difference` starts: the first line the two results differ on, with the lines before it for context.
let describe (difference: Difference) =
    let expected = difference.Reference.Split '\n'
    let actual = difference.Candidate.Split '\n'
    let first =
        Seq.zip expected actual
        |> Seq.tryFindIndex (fun (e, a) -> e <> a)
        |> Option.defaultValue (min expected.Length actual.Length)
    let around (lines: string array) = lines[max 0 (first - 3) .. min (lines.Length - 1) (first + 1)] |> String.concat "\n"
    $"{difference.Piece.Name}: differs at line {first + 1} of its tree\n--- reference\n{around expected}\n--- candidate\n{around actual}"

/// A report of `found`, out of `total` pieces, showing the first `shown` of them.
let report (shown: int) (total: int) (found: Difference list) =
    let listed = found |> List.truncate shown |> List.map describe
    $"{found.Length} of {total} pieces differ:\n\n" + String.concat "\n\n" listed
