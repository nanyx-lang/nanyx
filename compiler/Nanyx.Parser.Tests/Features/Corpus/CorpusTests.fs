/// The corpus every stage of the parser's rewrite is checked against, and the differential test that does the
/// checking (see the Work in progress page "The parser").
module Nanyx.Parser.Tests.CorpusTests

open Xunit
open Parser.Program
open Nanyx.Parser
open Nanyx.Parser.Corpus

/// Each piece of the corpus with its parse, parsed once, in parallel, for every test that reads it.
let private parses =
    lazy
        (Corpus.all.Value
         |> Array.ofList
         |> Array.Parallel.map (fun piece -> piece, parseModuleWithTrivia piece.Name piece.Source)
         |> List.ofArray)

[<Fact>]
let ``The corpus has the files, the docs' code blocks and the tests' sources`` () =
    let count origin = Corpus.all.Value |> List.filter (fun piece -> origin piece.Origin) |> List.length
    Assert.True(count (fun o -> o = SourceFile) > 150, "too few .nyx files")
    Assert.True(count (function DocBlock _ -> true | _ -> false) > 500, "too few docs code blocks")
    Assert.True(count (fun o -> o = TestSource) > 500, "too few test sources")
    Assert.Contains(Corpus.files.Value, fun piece -> piece.Name.Replace('\\', '/') = "stdlib/nanyx/prelude.nyx")

[<Fact>]
let ``A tree is written out with its spans and without its node ids`` () =
    let dump source = SyntaxDump.ofParse (parseModuleWithTrivia "t.nyx" source)
    Assert.Equal(dump "def x = 1", dump "def x = 1")
    Assert.NotEqual<string>(dump "def x = 1", dump "def x =  1")
    let written = dump "def x = 1 -- one"
    Assert.Contains("@1:1 [0..9) Def", written)
    Assert.Contains("LineComment", written)
    Assert.DoesNotContain("Id: ", written)

[<Fact>]
let ``The differential test finds a piece two parsers read differently`` () =
    let pieces = [ { Name = "t.nyx"; Source = "def x = 1"; Origin = TestSource } ]
    let withComment: Differential.ModuleParser = fun path source -> parseModuleWithTrivia path (source + " -- one")
    let found = Differential.differences parseModuleWithTrivia withComment pieces
    Assert.Equal(1, found.Length)
    Assert.Contains("t.nyx: differs at line", Differential.describe found.Head)

[<Fact>]
let ``The lexer's tokens and trivia give back every piece of the corpus exactly`` () =
    let failures =
        Corpus.all.Value
        |> List.filter (fun piece -> Lexer.write piece.Source (Lexer.lex piece.Name piece.Source).Tokens <> piece.Source)
        |> List.map _.Name
    Assert.Equal<string list>([], failures)

/// The comments in `lexed`, each with its text without trailing spaces.
let private lexedComments (source: string) (lexed: Lexer.Lexed) =
    lexed.Tokens
    |> Seq.collect (fun token -> Seq.append token.Leading token.Trailing)
    |> Seq.filter (fun trivia -> trivia.Kind = Lexer.LineComment || trivia.Kind = Lexer.DocComment || trivia.Kind = Lexer.BlockComment)
    |> Seq.map (fun trivia -> source.Substring(trivia.Start, trivia.End - trivia.Start).TrimEnd())
    |> List.ofSeq

/// The pieces whose block structure the layout pass reads differently from the tree on purpose, each named by its file
/// and its first line, with why. None is a program.
let private layoutDifferences =
    [ "SelectionRangeTests.fs", "def side = max(s, 2)", "a fragment whose top-level items are at different columns"
      "SelectionRangeTests.fs", "def side = s", "a fragment whose top-level items are at different columns"
      "ContextTests.fs", "def a = counted(3)", "a fragment whose top-level items are at different columns"
      "MemoryReportTests.fs", "Memory", "a program's output, not Nanyx"
      "RunHttpClientTests.fs", "200", "a program's output, not Nanyx"
      "InliningTests.fs", "def long: int -> int = { a0 ->", "a template whose block's lines are at different columns"
      "formatter.md", "-- before", "an arm indented further than the one before it, which the formatter lines up" ]

/// The entry of `layoutDifferences` that is `piece`, if there is one.
let private knownLayoutDifference (piece: Piece) =
    let file = System.IO.Path.GetFileName(piece.Name.Split(':')[0])
    let firstLine = (piece.Source.TrimStart().Split('\n')[0]).TrimEnd()
    layoutDifferences |> List.tryFind (fun (name, first, _) -> name = file && first = firstLine)

/// The corpus parsed once, in parallel, before any test that reads it starts: no test's time is the parse, which the
/// first to read it paid when it was parsed on demand (#2224).
type ParsedCorpus() =
    do parses.Force() |> ignore

/// The tests that read each piece of the corpus with its parse.
type CorpusParseTests(corpus: ParsedCorpus) =
    interface IClassFixture<ParsedCorpus>

    [<Fact>]
    member _.``Every file of the standard library and the packages parses`` () =
        let failures =
            parses.Value
            |> List.filter (fun (piece, _) -> piece.Origin = SourceFile && (piece.Name.StartsWith "stdlib" || piece.Name.StartsWith "packages"))
            |> List.choose (fun (piece, parse) ->
                match parse with
                | Result.Error error -> Some $"{piece.Name}: {error.Message}"
                | Result.Ok _ -> None)
        Assert.Equal<string list>([], failures)

    [<Fact>]
    member _.``The lexer reads every piece that parses without an error, and finds the comments the parser does`` () =
        let parsed =
            parses.Value
            |> List.choose (fun (piece, parse) ->
                match parse with
                | Result.Ok parsed -> Some(piece.Name, parsed)
                | Result.Error _ -> None)
        let failures =
            parsed
            |> List.choose (fun (name, parsed) ->
                let lexed = Lexer.lex name parsed.Source
                let comments =
                    parsed.Trivia
                    |> List.choose (function
                        | LineComment(text, _, _)
                        | DocLineComment(text, _) -> Some(text.TrimEnd())
                        | BlankLines _ -> None)
                if not lexed.Errors.IsEmpty then Some $"{name}: %A{lexed.Errors |> List.map _.Message}"
                elif lexedComments parsed.Source lexed <> comments then Some $"{name}: comments %A{lexedComments parsed.Source lexed}, not %A{comments}"
                else None)
        Assert.True(parsed.Length > 3500, $"Only {parsed.Length} pieces parse")
        Assert.Equal<string list>([], failures)

    [<Fact>]
    member _.``The parser reads the whole corpus the same way every time`` () =
        let pieces = Corpus.all.Value
        // The first reading of each piece is the one the other tests share, so the corpus is parsed once more here, not twice.
        let first = System.Collections.Generic.Dictionary<string * string, Result<SourceModule, ParseError>>()
        for piece, parse in parses.Value do
            first.TryAdd((piece.Name, piece.Source), parse) |> ignore
        let firstReading: Differential.ModuleParser = fun name source -> first[(name, source)]
        let found = Differential.differences firstReading parseModuleWithTrivia pieces
        Assert.True(found.IsEmpty, Differential.report 10 pieces.Length found)

    [<Fact>]
    member _.``The layout pass gives every piece that parses the block structure the parser does`` () =
        let compared =
            parses.Value
            |> Array.ofList
            |> Array.Parallel.choose (fun (piece, parse) ->
                match parse with
                | Result.Error _ -> None
                | Result.Ok parsed ->
                    let expected = BlockStructure.describe parsed.Source (BlockStructure.ofTree parsed)
                    let actual = BlockStructure.describe parsed.Source (BlockStructure.ofLayout parsed.Source)
                    Some(piece, (expected = actual), $"{piece.Name}:\n--- parser\n{expected}\n--- layout\n{actual}"))
        let differing = compared |> Array.filter (fun (piece, same, _) -> not same && (knownLayoutDifference piece).IsNone)
        let shown = differing |> Array.truncate 10 |> Array.map (fun (_, _, text) -> text) |> String.concat "\n\n"
        Assert.True(differing.Length = 0, $"{differing.Length} pieces differ:\n\n{shown}")
        // Each known difference is still one, so that the list is no longer than it needs to be.
        let stillDiffering =
            compared |> Array.choose (fun (piece, same, _) -> if same then None else knownLayoutDifference piece) |> Set.ofArray
        Assert.Equal<string list>([], layoutDifferences |> List.filter (stillDiffering.Contains >> not) |> List.map (fun (file, first, _) -> $"{file}: {first}"))
