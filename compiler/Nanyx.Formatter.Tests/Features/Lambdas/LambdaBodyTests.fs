/// Where the formatter puts a lambda's body that is one expression, and when it brackets it.
module Nanyx.Formatter.Tests.LambdaBodyTests

open Xunit
open Parser.Program
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

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
let ``A body of or or and goes on a line of its own, without parentheses`` () =
    Assert.Equal(
        lines [ "def isWhitespace: int -> bool = { b ->"; "  b == 32 or b == 9 or b == 10 or b == 13"; "}" ],
        format "def isWhitespace: int -> bool = { b -> b == 32 or b == 9 or b == 10 or b == 13 }")
    Assert.Equal(lines [ "def both: bool, bool -> bool = { a, b ->"; "  a and b"; "}" ], format "def both: bool, bool -> bool = { a, b -> a and b }")
    Assert.Equal(
        lines [ "def isWhitespace: int -> bool = { b ->"; "  b == 32 or b == 9"; "}" ],
        format (lines [ "def isWhitespace: int -> bool = { b ->"; "  (b == 32 or b == 9)"; "}" ]))

[<Fact>]
let ``A body of or or and that does not fit on the line starts on a line of its own`` () =
    let condition = String.concat " or " [ for n in 1 .. 12 -> $"value == {n * 1000}" ]
    let formatted = format $"def isOneOfThem: int -> bool = {{ value -> {condition} }}\n"
    Assert.StartsWith("def isOneOfThem: int -> bool = { value ->\n  value == 1000 or", formatted)
    roundTrips formatted

[<Fact>]
let ``A range stays on the arrow's line in an annotated def`` () =
    unchanged "def upTo: int -> list(int) = { x -> 1..x }\n"
    Assert.Equal("def upTo: int -> list(int) = { x -> 1..x }\n", format (lines [ "def upTo: int -> list(int) = { x ->"; "  (1..x)"; "}" ]))

[<Fact>]
let ``A block written with its body in parentheses keeps them only for a range`` () =
    unchanged (lines [ "def f = { x ->"; "  (1..x)"; "}" ])
    Assert.Equal(lines [ "def f = { x ->"; "  x % 2"; "}" ], format (lines [ "def f = { x ->"; "  (x % 2)"; "}" ]))
    Assert.Equal(lines [ "def f = { x ->"; "  x or y"; "}" ], format (lines [ "def f = { x ->"; "  (x or y)"; "}" ]))

[<Fact>]
let ``A block with a comment in it keeps its lines`` () =
    Assert.Equal(
        lines [ "def f: bool -> bool = { x ->"; "  -- either"; "  x or y"; "}" ],
        format (lines [ "def f: bool -> bool = { x ->"; "  -- either"; "  (x or y)"; "}" ]))

[<Fact>]
let ``A body of or, and, % or not parses back to the same tree`` () =
    roundTrips "def isWhitespace: int -> bool = { b -> b == 32 or b == 9 or b == 10 or b == 13 }\n"
    roundTrips (lines [ "def isWhitespace: int -> bool = { b ->"; "  (b == 32 or b == 9)"; "}" ])
    roundTrips "def both: bool, bool -> bool = { a, b -> a and b }\n"
    roundTrips "def f: int -> bool = { x -> x % 2 == 0 }\n"
    roundTrips "def f: bool -> bool = { x -> not x }\n"
    roundTrips "def f: bool, bool -> bool = { x, y -> not x or y }\n"
    roundTrips (lines [ "def g = { x, y ->"; "  match x"; "    | 0 -> x and y"; "    | _ -> not y"; "}" ])
