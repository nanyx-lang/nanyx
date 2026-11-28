/// `[]` where nothing says what its elements are is a `list(undefined)` (#3129), and a top-level value nothing could make
/// a value of a variable of is `undefined` there: such a list's head is `#emptyList`, and it is still given where a list
/// of any type is expected.
module Nanyx.Compiler.Tests.Features.Types.EmptyList

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The types of `names` in `source`, which must compile with no diagnostics, as the editor writes them in ASCII.
let private typesIn (source: string) (names: string list) =
    let typed = compileClean (normalized source)
    names |> List.map (asciiTypeIn typed)

[<Fact>]
let ``An empty list nothing says the elements of is a list of undefined, whose head is #emptyList`` () =
    Assert.Equal<string list>(
        [ "list(undefined)"; "#emptyList"; "() -> list(undefined)" ],
        typesIn "def items = []\ndef first = items \\head\ndef empty = { [] }\n" [ "items"; "first"; "empty" ])

[<Fact>]
let ``A list of undefined is given where a list of any type is expected`` () =
    Assert.Equal<string list>(
        [ "list(int)"; "list(string)" ],
        typesIn "def items = []\ndef numbers: list(int) = items\ndef words: list(string) = items\n" [ "numbers"; "words" ])

[<Fact>]
let ``An empty list joined with a list, or folded into one, has that list's elements`` () =
    let source =
        "def xs: list(int) = [1, 2]\ndef c = true\ndef picked = if c then xs else []\n"
        + "def folded = list.fold(xs, [], { acc, x -> acc ++ [x] })\n"
    Assert.Equal<string list>([ "list(int)"; "list(int)" ], typesIn source [ "picked"; "folded" ])

[<Fact>]
let ``An empty list in a function's input is left generic`` () =
    Assert.Equal<string list>(
        [ "list(a) -> list(a)" ],
        typesIn "def keep = { ys -> ys ++ [] }\n" [ "keep" ])

[<Fact>]
let ``A value the module has an error in keeps its variables, which its message names`` () =
    let _, message, _ = onlyDiagnostic "def f = { x -> x.name }\ndef y = f(5)\n"
    Assert.Equal("'int' vs '(name: α, …)'", message)
