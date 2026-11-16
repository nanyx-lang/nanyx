/// The formatter over every piece of Nanyx in the repository: the examples, the standard library, the compiler's
/// test sources and the docs' code blocks.
module Nanyx.Formatter.Tests.CorpusTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Parser.Program
open Nanyx.Formatter

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))

let private skipped = [ "bin"; "obj"; "node_modules"; ".git"; ".claude" ]

/// Each `.nyx` file and each `nanyx` code block in the docs, with a name to report it by.
let private corpus =
    lazy
        let isSkipped (path: string) =
            path.Split([| '/'; '\\' |]) |> Array.exists (fun part -> List.contains part skipped)
        let files =
            Directory.EnumerateFiles(repositoryRoot, "*.nyx", SearchOption.AllDirectories)
            |> Seq.filter (fun path -> not (isSkipped (Path.GetRelativePath(repositoryRoot, path))))
            |> Seq.map (fun path -> Path.GetRelativePath(repositoryRoot, path), File.ReadAllText path)
        let codeBlock = Regex(@"```nanyx[^\n]*\n(.*?)```", RegexOptions.Singleline)
        let docs =
            Directory.EnumerateFiles(Path.Combine(repositoryRoot, "docs", "src", "content"), "*.md", SearchOption.AllDirectories)
            |> Seq.collect (fun path ->
                let name = Path.GetRelativePath(repositoryRoot, path)
                codeBlock.Matches(File.ReadAllText path) |> Seq.mapi (fun index m -> $"{name} (block {index + 1})", m.Groups[1].Value))
        Seq.append files docs |> List.ofSeq

/// The pieces that parse, as their name, source and parse.
let private parsed =
    lazy
        corpus.Value
        |> List.choose (fun (name, source) ->
            match parseModuleWithTrivia name source with
            | Result.Ok parsed -> Some(name, source, parsed)
            | Result.Error _ -> None)

let private format (parsed: SourceModule) = Formatter.format Formatter.Options.defaults parsed

/// Fails listing every piece `check` finds a problem with.
let private everyPiece (check: string -> string -> SourceModule -> string option) =
    let failures = parsed.Value |> List.choose (fun (name, source, tree) -> check name source tree |> Option.map (fun problem -> $"{name}: {problem}"))
    if not failures.IsEmpty then
        failwith ($"{failures.Length} of {parsed.Value.Length} pieces:\n\n" + String.concat "\n\n" failures)

[<Fact>]
let ``The corpus has Nanyx in it, most of which parses`` () =
    Assert.True(corpus.Value.Length > 100, $"Only {corpus.Value.Length} pieces found under {repositoryRoot}")
    Assert.True(parsed.Value.Length > corpus.Value.Length / 2, $"Only {parsed.Value.Length} of {corpus.Value.Length} pieces parse")

[<Fact>]
let ``Formatted code parses to the same tree`` () =
    everyPiece (fun name _ tree ->
        let formatted = format tree
        match parseModuleWithTrivia name formatted with
        | Result.Error error -> Some $"the formatted code does not parse: {error.Message}\n---\n{formatted}"
        | Result.Ok again when not (Syntax.same tree.Items again.Items) -> Some $"the formatted code parses differently:\n---\n{formatted}"
        | Result.Ok _ -> None)

[<Fact>]
let ``Formatting keeps every comment, in order`` () =
    everyPiece (fun name _ tree ->
        let formatted = format tree
        let commentsOf = FormatCommand.commentsOf
        match parseModuleWithTrivia name formatted with
        | Result.Ok again when commentsOf again <> commentsOf tree ->
            Some $"comments changed from %A{commentsOf tree} to %A{commentsOf again}\n---\n{formatted}"
        | _ -> None)

/// The docs' code blocks that are whole programs, as their name and source: those fenced ```nanyx alone, not
/// ```nanyx fragment, ```nanyx skip=#<issue> or ```nanyx unformatted (the Formatter page's before-and-after examples),
/// and with no `-- error` comment, which marks code that is wrong on purpose and is laid out to show where. A block
/// indented in a list loses its fence's indent.
let private docsPrograms =
    lazy
        let codeBlock = Regex(@"^([ \t]*)```nanyx([^\n]*)\n(.*?)^[ \t]*```", RegexOptions.Singleline ||| RegexOptions.Multiline)
        Directory.EnumerateFiles(Path.Combine(repositoryRoot, "docs", "src", "content"), "*.md", SearchOption.AllDirectories)
        |> Seq.collect (fun path ->
            let text = (File.ReadAllText path).ReplaceLineEndings "\n"
            codeBlock.Matches text
            |> Seq.filter (fun m -> m.Groups[2].Value.Trim() = "")
            |> Seq.map (fun m ->
                let indent = m.Groups[1].Value.Length
                let line = text.Substring(0, m.Index).Split('\n').Length
                let source =
                    m.Groups[3].Value.Split('\n')
                    |> Array.map (fun l -> if l.Length >= indent && String.IsNullOrWhiteSpace(l.Substring(0, indent)) then l.Substring indent else l.TrimStart())
                    |> String.concat "\n"
                $"{Path.GetRelativePath(repositoryRoot, path)}:{line}", source))
        |> Seq.filter (fun (_, source) -> not (source.Contains "-- error"))
        |> List.ofSeq

[<Fact>]
let ``The examples and the docs' code blocks are formatted`` () =
    let examples =
        Directory.EnumerateFiles(Path.Combine(repositoryRoot, "examples"), "*.nyx", SearchOption.AllDirectories) |> List.ofSeq
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
    everyPiece (fun name _ tree ->
        let formatted = format tree
        match parseModuleWithTrivia name formatted with
        | Result.Ok again ->
            let twice = format again
            if twice <> formatted then Some $"formatting again changed it:\n---\n{formatted}\n---\n{twice}" else None
        | Result.Error _ -> None)
