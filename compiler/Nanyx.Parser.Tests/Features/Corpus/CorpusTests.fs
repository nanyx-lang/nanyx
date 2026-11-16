/// The corpus every stage of the parser's rewrite is checked against, and the differential test that does the
/// checking (see the Work in progress page "The parser").
module Nanyx.Parser.Tests.CorpusTests

open Xunit
open Parser.Program
open Nanyx.Parser
open Nanyx.Parser.Corpus

[<Fact>]
let ``The corpus has the files, the docs' code blocks and the tests' sources`` () =
    let count origin = Corpus.all.Value |> List.filter (fun piece -> origin piece.Origin) |> List.length
    Assert.True(count (fun o -> o = SourceFile) > 150, "too few .nyx files")
    Assert.True(count (function DocBlock _ -> true | _ -> false) > 500, "too few docs code blocks")
    Assert.True(count (fun o -> o = TestSource) > 500, "too few test sources")
    Assert.Contains(Corpus.files.Value, fun piece -> piece.Name.Replace('\\', '/') = "stdlib/nanyx/prelude.nyx")

[<Fact>]
let ``Every file of the standard library and the packages parses`` () =
    let failures =
        Corpus.files.Value
        |> List.filter (fun piece -> piece.Name.StartsWith "stdlib" || piece.Name.StartsWith "packages")
        |> List.choose (fun piece ->
            match parseModuleWithTrivia piece.Name piece.Source with
            | Result.Error error -> Some $"{piece.Name}: {error.Message}"
            | Result.Ok _ -> None)
    Assert.Equal<string list>([], failures)

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
let ``The parser reads the whole corpus the same way every time`` () =
    let pieces = Corpus.all.Value
    let found = Differential.differences parseModuleWithTrivia parseModuleWithTrivia pieces
    Assert.True(found.IsEmpty, Differential.report 10 pieces.Length found)
