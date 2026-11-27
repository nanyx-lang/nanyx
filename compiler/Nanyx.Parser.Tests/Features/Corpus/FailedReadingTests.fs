/// How many readings the parser tries that don't match: each is an exception, which costs far more than reading a token,
/// so the parser asks the token at the cursor first, where it decides (#2219). Counted rather than timed.
module ParserTests.Features.FailedReadings

open Xunit
open Parser.Program
open Nanyx.Parser

/// How many readings `source` tried that didn't match (see `ParseState.NoMatches`), and how many lines it has.
let private failedReadings (name: string) (source: string) =
    match DescentTopLevel.tryReadModuleWithState name source with
    | Ok(_, state) -> state.NoMatches, source.Split('\n').Length
    | Error error -> failwith $"{name}: {error.Message} at {error.Span.StartLine + 1}:{error.Span.StartColumn + 1}"

[<Fact>]
let ``A def whose every part has one reading is read with no reading that fails`` () =
    // Each line tried 85 readings that failed: a name as a call, a context's member and a pattern, a type's name as each
    // other type, and every `.field`, `,` and `->` that wasn't there.
    let source = [ for i in 1..200 -> $"def f{i}: int -> int = {{ x -> g(x, 1) + h(x) }}" ] |> String.concat "\n"
    Assert.Equal((0, 200), failedReadings "t.nyx" source)

[<Fact>]
let ``The standard library and the packages are read with fewer readings that fail than lines`` () =
    // prelude-lists.nyx tried 31 a line.
    let files =
        Corpus.files.Value
        |> List.filter (fun piece -> piece.Name.StartsWith "stdlib" || piece.Name.StartsWith "packages")
        |> List.map (fun piece -> failedReadings piece.Name piece.Source)
    let failed = files |> List.sumBy fst
    let lines = files |> List.sumBy snd
    Assert.InRange(failed, 0, lines - 1)

[<Fact>]
let ``The prelude's lists are read with fewer readings that fail than lines`` () =
    let piece = Corpus.files.Value |> List.find (fun piece -> piece.Name.Replace('\\', '/') = "stdlib/nanyx/prelude-lists.nyx")
    let failed, lines = failedReadings piece.Name piece.Source
    Assert.InRange(failed, 0, lines - 1)
