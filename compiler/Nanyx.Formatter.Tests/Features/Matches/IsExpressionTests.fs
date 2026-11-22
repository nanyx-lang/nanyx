module Nanyx.Formatter.Tests.Features.Matches.IsExpressions

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A test is kept as written, as a lambda's body, under not and in and and or chains`` () =
    unchanged (
        lines
            [ "def f = { x -> x is #quux }"
              "def g = { o -> o is #some(v) and v > 3 }"
              "def h = { a, o -> a and o is #some(v) and v > 3 or a }"
              "def k = { a, o -> a or o is #some(v) and (v > 3 or v < 0) }"
              "def n = { o -> not (o is #some(_)) }"
              "def p = { xs -> xs \\list.head is #some(h) and h == 1 }"
              "def q = { a, b -> a == b is #true }"
              "def r = { a, b -> if a is #some(x) and b is #some(y) then x + y else 0 }" ])

[<Fact>]
let ``A test is spaced as an operator is, and loses brackets it doesn't need`` () =
    "def f = {a, o->(o  is   #some(v) and v > 3) or a}\ndef g = { o -> (o is #a) or (o is #b) }\n"
    |> formatsTo "def f = { a, o -> o is #some(v) and v > 3 or a }\ndef g = { o -> o is #a or o is #b }\n"

[<Fact>]
let ``A test keeps the brackets an operand of a tighter operator needs`` () =
    unchanged (lines [ "def f = { o -> (o is #a) == true }"; "def g = { a, b -> (a and b) is #true }" ])
