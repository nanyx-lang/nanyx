/// Where the formatter puts a lambda's body that is one expression, and when it brackets it.
module Nanyx.Formatter.Tests.LambdaBodyTests

open Xunit
open Parser.Program
open Nanyx.Formatter

let private lines (ls: string list) = String.concat "\n" ls + "\n"

let private format (source: string) =
    match Formatter.formatSource Formatter.Options.defaults "" source with
    | Result.Ok formatted -> formatted
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// Formatting `source` gives it back unchanged.
let private unchanged (source: string) = Assert.Equal(source, format source)

/// The formatted `source` parses to the same tree as `source`, and formatting it again changes nothing.
let private roundTrips (source: string) =
    let tree =
        match parseModuleWithTrivia "" source with
        | Result.Ok tree -> tree
        | Result.Error error -> failwith $"Parse failed: {error.Message}"
    let formatted = Formatter.format Formatter.Options.defaults tree
    match parseModuleWithTrivia "" formatted with
    | Result.Error error -> failwith $"The formatted code does not parse: {error.Message}\n---\n{formatted}"
    | Result.Ok again ->
        Assert.True(Syntax.same tree.Items again.Items, $"The formatted code parses differently:\n---\n{formatted}")
        Assert.Equal(formatted, Formatter.format Formatter.Options.defaults again)

[<Fact>]
let ``An annotated def's lambda body goes on a line of its own`` () =
    Assert.Equal(lines [ "def f: int -> int = { x ->"; "  x * 2 + 1"; "}" ], format "def f: int -> int = { x -> x * 2 + 1 }")

[<Fact>]
let ``A body of or or and stays on the arrow's line, without parentheses`` () =
    unchanged "def isWhitespace: int -> bool = { b -> b == 32 or b == 9 or b == 10 or b == 13 }\n"
    unchanged "def both: bool, bool -> bool = { a, b -> a and b }\n"
    Assert.Equal("def isWhitespace: int -> bool = { b -> b == 32 or b == 9 }\n", format "def isWhitespace: int -> bool = { b ->\n  (b == 32 or b == 9)\n}")

[<Fact>]
let ``A body of or or and that does not fit on the line still starts on the arrow's line`` () =
    let condition = String.concat " or " [ for n in 1 .. 12 -> $"value == {n * 1000}" ]
    let formatted = format $"def isOneOfThem: int -> bool = {{ value -> {condition} }}\n"
    Assert.StartsWith("def isOneOfThem: int -> bool = { value -> value == 1000 or", formatted)
    roundTrips formatted

[<Fact>]
let ``Any operator a statement cannot take stays on the arrow's line in an annotated def`` () =
    unchanged "def odd: int -> int = { x -> x % 2 }\n"
    Assert.Equal("def odd: int -> int = { x -> x % 2 }\n", format (lines [ "def odd: int -> int = { x ->"; "  (x % 2)"; "}" ]))

[<Fact>]
let ``A block written with its body in parentheses keeps them where the def has no annotation`` () =
    unchanged (lines [ "def f = { x ->"; "  (x % 2)"; "}" ])
    unchanged (lines [ "def f = { x ->"; "  (x or y)"; "}" ])

[<Fact>]
let ``A block with a comment in it keeps its lines and parentheses`` () =
    unchanged (lines [ "def f: bool -> bool = { x ->"; "  -- either"; "  (x or y)"; "}" ])

[<Fact>]
let ``A body of or or and parses back to the same tree`` () =
    roundTrips "def isWhitespace: int -> bool = { b -> b == 32 or b == 9 or b == 10 or b == 13 }\n"
    roundTrips "def isWhitespace: int -> bool = { b ->\n  (b == 32 or b == 9)\n}\n"
    roundTrips "def both: bool, bool -> bool = { a, b -> a and b }\n"
    roundTrips "def f: int -> bool = { x -> x % 2 == 0 }\n"
