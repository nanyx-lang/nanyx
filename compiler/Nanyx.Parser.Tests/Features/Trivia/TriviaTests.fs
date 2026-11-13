module ParserTests.Features.Trivia

open System.IO
open Xunit
open Parser.Program

let private lines (ls: string list) = String.concat "\n" ls

/// `source` with `\n` line breaks, whatever the line endings of this file's checkout.
let private normalized (source: string) = source.Replace("\r\n", "\n")

let private trivia source =
    match parseModuleWithTrivia "" source with
    | Result.Ok parsed -> parsed.Trivia
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// Each piece of trivia as its kind, text and the line and column it starts at.
let private describe (items: Trivia list) =
    items
    |> List.map (function
        | LineComment(text, trailing, span) -> (if trailing then "trailing" else "own-line"), text, span.StartLine, span.StartColumn
        | DocLineComment(text, span) -> "doc", text, span.StartLine, span.StartColumn
        | BlankLines(count, span) -> "blank", string count, span.StartLine, span.StartColumn)

[<Fact>]
let ``A comment after code on its line is trailing`` () =
    Assert.Equal<_ list>([ "trailing", "-- four", 0, 14 ], describe (trivia "def a = 5 - 1 -- four"))

[<Fact>]
let ``A comment on a line of its own before a definition is not trailing`` () =
    Assert.Equal<_ list>([ "own-line", "-- the answer", 0, 0 ], describe (trivia "-- the answer\ndef a = 42"))

[<Fact>]
let ``A doc comment is a doc comment, its continuation lines included`` () =
    let source = """
--| One
  two
def a = 42
"""
    Assert.Equal<_ list>([ "doc", "--| One\n  two", 0, 0 ], describe (trivia (source.ReplaceLineEndings("\n").TrimStart '\n')))

[<Fact>]
let ``A --| after code on its line is an ordinary trailing comment`` () =
    Assert.Equal<_ list>([ "trailing", "--| not a doc comment", 0, 11 ], describe (trivia "def a = 42 --| not a doc comment"))

[<Fact>]
let ``A comment's span runs from -- to the end of its line`` () =
    match trivia "def a = 1 -- one\ndef b = 2" with
    | [ LineComment(_, _, span) ] ->
        Assert.Equal(10, span.Start)
        Assert.Equal(16, span.End)
    | other -> failwith $"Unexpected trivia: %A{other}"

[<Fact>]
let ``Comments inside a lambda are kept`` () =
    let source = lines [ "def f = { x ->"; "  -- add one"; "  def y = x + 1"; "  y * 2 -- doubled"; "}" ]
    Assert.Equal<_ list>([ "own-line", "-- add one", 1, 2; "trailing", "-- doubled", 3, 8 ], describe (trivia source))

[<Fact>]
let ``Comments inside a match are kept`` () =
    let source = lines [ "def g = { | 0 -> 1 -- zero"; "          | _ -> 2 }" ]
    Assert.Equal<_ list>([ "trailing", "-- zero", 0, 19 ], describe (trivia source))

[<Fact>]
let ``Comments and blank lines inside a record are kept, each once`` () =
    let source = lines [ "def x = ("; "  a = 1"; ""; "  -- the second field"; "  b = 2 -- trailing"; ")" ]

    Assert.Equal<_ list>(
        [ "blank", "1", 2, 0; "own-line", "-- the second field", 3, 2; "trailing", "-- trailing", 4, 8 ],
        describe (trivia source)
    )

[<Fact>]
let ``A comment at the end of the file is kept`` () =
    Assert.Equal<_ list>([ "own-line", "-- the end", 1, 0 ], describe (trivia "def a = 1\n-- the end"))

[<Fact>]
let ``A comment at the end of a module block is kept`` () =
    let source =
        lines [ "module app"; "module geometry ="; "  def origin = 0"; "  -- the end of the block"; "def p = geometry.origin" ]

    Assert.Equal<_ list>([ "own-line", "-- the end of the block", 3, 2 ], describe (trivia source))

[<Fact>]
let ``Consecutive blank lines make one run`` () =
    let source = lines [ "def a = 1"; ""; "   "; ""; "def b = 2"; ""; "def c = 3" ]
    Assert.Equal<_ list>([ "blank", "3", 1, 0; "blank", "1", 5, 0 ], describe (trivia source))

[<Fact>]
let ``A run of blank lines spans from the start of the first to the end of the last`` () =
    match trivia "def a = 1\n\n  \ndef b = 2" with
    | [ BlankLines(2, span) ] ->
        Assert.Equal(10, span.Start)
        Assert.Equal(13, span.End)
    | other -> failwith $"Unexpected trivia: %A{other}"

[<Fact>]
let ``Blank lines inside a string literal are part of the string`` () =
    Assert.Empty(trivia "def s = \"one\n\ntwo\"")
    Assert.Empty(trivia "def s = 'one\n\ntwo'")
    Assert.Empty(trivia "def n = 1\ndef s = \"one {n}\n\ntwo\"")

[<Fact>]
let ``-- inside a string literal is not a comment`` () =
    Assert.Empty(trivia "def s = \"a -- b\"")

[<Fact>]
let ``Trailing whitespace at the end of the file is not a blank line`` () =
    Assert.Empty(trivia "def a = 1\n\n\n")
    Assert.Empty(trivia "")

[<Fact>]
let ``Spans index the normalised source`` () =
    match parseModuleWithTrivia "" "def a = 1\r\n\r\n-- note\r\ndef b = 2\r\n" with
    | Result.Ok parsed ->
        Assert.Equal("def a = 1\n\n-- note\ndef b = 2", parsed.Source)

        match parsed.Trivia with
        | [ BlankLines(1, _); LineComment(text, false, span) ] ->
            Assert.Equal(text, parsed.Source.Substring(span.Start, span.End - span.Start))
        | other -> failwith $"Unexpected trivia: %A{other}"
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

[<Fact>]
let ``Module blocks are kept as written`` () =
    match parseModuleWithTrivia "" (lines [ "module geometry ="; "  def origin = 0"; "def p = geometry.origin" ]) with
    | Result.Ok parsed ->
        match parsed.Items |> List.map (fun item -> item.Kind) with
        | [ ModuleBlock("geometry", [ _ ]); Def(ValueDef(_, "p", _, _)) ] -> ()
        | other -> failwith $"Unexpected items: %A{other}"
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

[<Fact>]
let ``A line of three or more dashes opens a block comment, closed by a line of the same dashes`` () =
    let first = normalized """---
This is a multi-line comment.
---"""
    let second = normalized """----------------------------------
This is also a multi-line comment.
----------------------------------"""
    let source = $"{first}\n\n{second}\ndef a = 1"
    Assert.Equal<_ list>(
        [ "own-line", first, 0, 0; "blank", "1", 3, 0; "own-line", second, 4, 0 ],
        describe (trivia source))

[<Fact>]
let ``A block comment opened with three dashes is not closed by four`` () =
    let comment = normalized """---
def a = 1
----
  ---"""
    Assert.Equal<_ list>([ "own-line", comment, 0, 0 ], describe (trivia $"{comment}\ndef b = 2"))

[<Fact>]
let ``A block comment may be indented, and its blank lines are part of it`` () =
    let source = normalized """def f = { x ->
  ---
  one

  two
  ---
  x + 1
}"""
    Assert.Equal<_ list>([ "own-line", "---\n  one\n\n  two\n  ---", 1, 2 ], describe (trivia source))

[<Fact>]
let ``Dashes after code, or with text after them, are a -- comment`` () =
    let source = normalized """def a = 1 ---
--- not a block
def b = 2"""
    Assert.Equal<_ list>([ "trailing", "---", 0, 10; "own-line", "--- not a block", 1, 0 ], describe (trivia source))

[<Fact>]
let ``A block comment that is never closed is an error at its opening line`` () =
    let source = normalized """def a = 1
---
never closed
----
def b = 2"""
    match parseModuleWithTrivia "" source with
    | Result.Error error ->
        Assert.Contains("This block comment is never closed", error.Message)
        Assert.Equal(1, error.Span.StartLine)
        Assert.Equal(0, error.Span.StartColumn)
    | Result.Ok parsed -> failwith $"Unexpected parse: %A{parsed.Items}"

/// Every `.nyx` file the parser tests use that parses: each comment is recorded once, its text is the source at its
/// span, and the trivia is in source order.
[<Fact>]
let ``Every comment in the test files is recorded once, where it is written`` () =
    let files = Directory.GetFiles(Path.Combine(__SOURCE_DIRECTORY__, ".."), "*.nyx", SearchOption.AllDirectories)
    Assert.NotEmpty(files)

    for file in files do
        match parseModuleWithTrivia file (File.ReadAllText file) with
        | Result.Error _ -> ()
        | Result.Ok parsed ->
            let starts = parsed.Trivia |> List.map (fun item -> item.Span.Start)
            Assert.True((starts = List.sort starts), $"{file}: trivia out of order")
            Assert.True((starts = List.distinct starts), $"{file}: trivia recorded twice")

            for item in parsed.Trivia do
                match item with
                | LineComment(text, _, span) ->
                    Assert.StartsWith("--", text)
                    Assert.Equal(text, parsed.Source.Substring(span.Start, span.End - span.Start))
                    // Only a block comment, which starts with its line of dashes, spans lines.
                    if text.Contains "\n" then
                        Assert.Matches("^---+\n", text)
                | DocLineComment(text, span) ->
                    Assert.StartsWith("--|", text)
                    Assert.Equal(text, parsed.Source.Substring(span.Start, span.End - span.Start))
                | BlankLines _ -> ()
