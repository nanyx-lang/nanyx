/// The formatter over every piece of Nanyx in the repository: the examples, the standard library, the compiler's
/// test sources and the docs' code blocks.
module Nanyx.Formatter.Tests.CorpusTests

open System
open System.IO
open Xunit
open Parser.Program
open Nanyx.Formatter
open Nanyx.Parser

let private repositoryRoot = Corpus.repositoryRoot

/// Each `.nyx` file and each `nanyx` code block in the docs, with a name to report it by.
let private corpus =
    lazy ((Corpus.files.Value @ Corpus.docBlocks.Value) |> List.map (fun piece -> piece.Name, piece.Source))

/// Each piece, as its name, source and parse or the error that stops it parsing.
let private parses =
    lazy
        corpus.Value
        |> Array.ofList
        |> Array.Parallel.map (fun (name, source) -> name, source, parseModuleWithTrivia name source)
        |> List.ofArray

/// The pieces that parse, as their name, source and parse. Not every piece is meant to: a test's input may be wrong on
/// purpose. The examples always are (see `Every example parses`).
let private parsed =
    lazy
        parses.Value
        |> List.choose (fun (name, source, parse) ->
            match parse with
            | Result.Ok parsed -> Some(name, source, parsed)
            | Result.Error _ -> None)

let private format (parsed: SourceModule) = Formatter.format Formatter.Options.defaults parsed

/// A piece that parses, formatted: its name, its parse, the formatted code and the parse of that. Each piece is
/// formatted and parsed again once, in parallel, for every test that looks at it.
type private Formatted = { Name: string; Tree: SourceModule; Formatted: string; Again: Result<SourceModule, ParseError> }

let private formatted =
    lazy
        parsed.Value
        |> Array.ofList
        |> Array.Parallel.map (fun (name, _, tree) ->
            let formatted = format tree
            { Name = name; Tree = tree; Formatted = formatted; Again = parseModuleWithTrivia name formatted })
        |> List.ofArray

/// Fails listing every piece `check` finds a problem with.
let private everyPiece (check: Formatted -> string option) =
    let failures = formatted.Value |> List.choose (fun piece -> check piece |> Option.map (fun problem -> $"{piece.Name}: {problem}"))
    if not failures.IsEmpty then
        failwith ($"{failures.Length} of {formatted.Value.Length} pieces:\n\n" + String.concat "\n\n" failures)

[<Fact>]
let ``The corpus has Nanyx in it, most of which parses`` () =
    Assert.True(corpus.Value.Length > 100, $"Only {corpus.Value.Length} pieces found under {repositoryRoot}")
    Assert.True(parsed.Value.Length > corpus.Value.Length / 2, $"Only {parsed.Value.Length} of {corpus.Value.Length} pieces parse")

[<Fact>]
let ``Every example parses`` () =
    let examples = "examples" + string Path.DirectorySeparatorChar
    let failures =
        parses.Value
        |> List.choose (fun (name, _, parse) ->
            match parse with
            | Result.Error error when name.StartsWith examples ->
                Some $"{name}:{error.Span.StartLine + 1}:{error.Span.StartColumn + 1}: {error.Message}"
            | _ -> None)
    Assert.True(failures.IsEmpty, $"{failures.Length} examples don't parse:\n\n" + String.concat "\n" failures)

[<Fact>]
let ``Formatted code parses to the same tree`` () =
    everyPiece (fun piece ->
        match piece.Again with
        | Result.Error error -> Some $"the formatted code does not parse: {error.Message}\n---\n{piece.Formatted}"
        | Result.Ok again when not (Syntax.same piece.Tree.Items again.Items) -> Some $"the formatted code parses differently:\n---\n{piece.Formatted}"
        | Result.Ok _ -> None)

[<Fact>]
let ``Formatting keeps every comment, in order`` () =
    everyPiece (fun piece ->
        let commentsOf = FormatCommand.commentsOf
        match piece.Again with
        | Result.Ok again when commentsOf again <> commentsOf piece.Tree ->
            Some $"comments changed from %A{commentsOf piece.Tree} to %A{commentsOf again}\n---\n{piece.Formatted}"
        | _ -> None)

/// The docs' code blocks that are whole programs, as their name and source: those fenced ```nanyx alone, not
/// ```nanyx fragment, ```nanyx skip=#<issue> or ```nanyx unformatted (the Formatter page's before-and-after examples),
/// and with no `-- error` comment, which marks code that is wrong on purpose and is laid out to show where.
let private docsPrograms =
    lazy
        Corpus.docBlocks.Value
        |> List.filter (fun piece -> piece.Origin = Corpus.DocBlock "" && not (piece.Source.Contains "-- error"))
        |> List.map (fun piece -> piece.Name, piece.Source)

[<Fact>]
/// The examples, the docs site's home page's examples (docs/src/home) and the docs' code blocks.
let ``The examples and the docs' code blocks are formatted`` () =
    let examples =
        [ "examples"; Path.Combine("docs", "src", "home") ]
        |> List.collect (fun directory ->
            Directory.EnumerateFiles(Path.Combine(repositoryRoot, directory), "*.nyx", SearchOption.AllDirectories) |> List.ofSeq)
    let unformattedExamples =
        FormatCommand.run Settings.forFile FormatCommand.Check examples
        |> List.choose (fun (file, result) ->
            match result with
            | FormatCommand.Changed -> Some $"{Path.GetRelativePath(repositoryRoot, file)}: not formatted; run nanyx format on it"
            | FormatCommand.Failed (FormatCommand.NotParsed _)
            | FormatCommand.Unchanged -> None
            | FormatCommand.Failed failure -> Some $"{Path.GetRelativePath(repositoryRoot, file)}: %A{failure}")
    let unformattedBlocks =
        docsPrograms.Value
        |> List.choose (fun (name, source) ->
            match FormatCommand.formatText Formatter.Options.defaults name source with
            | FormatCommand.Formatted formatted when formatted <> source ->
                Some $"{name}: not formatted; it should be\n---\n{formatted}---"
            | FormatCommand.Formatted _
            | FormatCommand.NotParsed _ -> None
            | failure -> Some $"{name}: %A{failure}")
    let failures = unformattedExamples @ unformattedBlocks
    Assert.True(failures.IsEmpty, $"{failures.Length} pieces are not formatted:\n\n" + String.concat "\n\n" failures)

[<Fact>]
let ``Formatting formatted code changes nothing`` () =
    everyPiece (fun piece ->
        match piece.Again with
        | Result.Ok again ->
            let twice = format again
            if twice <> piece.Formatted then Some $"formatting again changed it:\n---\n{piece.Formatted}\n---\n{twice}" else None
        | Result.Error _ -> None)
