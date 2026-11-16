module ParserTests.Features.Directives

open Xunit
open Parser.Program

/// The one directive at the top of `source`.
let private directiveOf (source: string) =
    match Directives.read "t.nyx" source with
    | [ directive ] -> directive
    | other -> failwith $"Expected one directive, got %A{other}"

/// The words of the one directive at the top of `source`.
let private wordsOf (source: string) = (directiveOf source).Words |> List.map _.Text

[<Fact>]
let ``A directive is its name and the words after it`` () =
    Assert.Equal<string list>([ "dependency"; "acme.util"; "../util" ], wordsOf "::dependency acme.util ../util\n")
    let directive = directiveOf "::lint   unusedBindings #error\n"
    Assert.Equal("lint", directive.Name)
    Assert.Equal<string list>([ "unusedBindings"; "#error" ], directive.Arguments |> List.map _.Text)

[<Fact>]
let ``Each word has its own span`` () =
    let source = "-- A script.\n::host acme.server ../server\n"
    let words = (directiveOf source).Words
    Assert.Equal<string list>([ "host"; "acme.server"; "../server" ], words |> List.map (fun word -> source.Substring(word.Span.Start, word.Span.End - word.Span.Start)))
    Assert.Equal<int list>([ 2; 7; 19 ], words |> List.map _.Span.StartColumn)
    Assert.All(words, fun word -> Assert.Equal(1, word.Span.StartLine))

[<Fact>]
let ``A comment after a directive's words is not one of them`` () =
    Assert.Equal<string list>([ "linter"; "nanyx.lint" ], wordsOf "::linter nanyx.lint  -- the official linter\n")

[<Fact>]
let ``A string in quotes is one word, whatever it holds`` () =
    Assert.Equal<string list>([ "format"; "name"; "\"a b\"" ], wordsOf "::format name \"a b\"\n")

[<Fact>]
let ``A line written as directives were, a key and a TOML value, is in the old form`` () =
    Assert.True((directiveOf "::linters.default\n").IsOldForm)
    Assert.True((directiveOf "::formatter.lineWidth = 80\n").IsOldForm)
    Assert.True((directiveOf "::host = \"0.28.0\"\n").IsOldForm)
    Assert.False((directiveOf "::host nanyx.console\n").IsOldForm)

[<Fact>]
let ``A line with nothing after its :: has no words`` () =
    Assert.Empty((directiveOf "::\n").Words)
    Assert.Equal("", (directiveOf "::\n").Name)
