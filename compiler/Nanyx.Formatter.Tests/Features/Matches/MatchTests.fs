module Nanyx.Formatter.Tests.Features.Matches.Matchs

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A match that is not the last arm's body is bracketed`` () =
    unchanged (lines [ "def f = { x, y ->"; "  match x"; "    | 0 -> (match y"; "      | 0 -> 1"; "      | _ -> 2)"; "    | _ -> 3"; "}" ])

[<Fact>]
let ``A match on the lines after an arm ends at the arm indented back to the outer match`` () =
    let source = """
def f: list(int) -> string = { xs ->
  match xs
    | [a] ->
        match a
          | 0 -> "zero"
          | _ -> "one"
    | _ -> "other"
}
"""
    let expected = """def f: list(int) -> string = { xs ->
  match xs
    | [a] ->
      match a
        | 0 -> "zero"
        | _ -> "one"
    | _ -> "other"
}
"""
    Assert.Equal(expected.ReplaceLineEndings "\n", (format source).ReplaceLineEndings "\n")
    unchanged (expected.ReplaceLineEndings "\n")

[<Fact>]
let ``Guard patterns are printed as written`` () =
    unchanged (lines [ "def f = { n ->"; "  match n"; "    | { < 0 } -> 0"; "    | { % 2 == 0 } -> 1"; "    | { x -> x > limit * 2 } -> 2"; "    | > 5 -> 3"; "    | _ -> 4"; "}" ])
    unchanged (lines [ "def g = {"; "  | (0, { .length > 5 }) -> 1"; "  | #some({ >= 2 }) -> 2"; "  | [{ % 2 == 0 }, ...] -> 3"; "  | _ -> 4"; "}" ])
    Assert.Equal(
        lines [ "def h = {"; "  | { .length > 5 } -> 1"; "  | >= 2 -> 2"; "  | _ -> 3"; "}" ],
        format (lines [ "def h = {"; "  | {.length > 5} -> 1"; "  | >=   2 -> 2"; "  | _ -> 3"; "}" ]))

[<Fact>]
let ``And-patterns are printed as written`` () =
    unchanged (lines [ "def f = { n ->"; "  match n"; "    | { < 0 } & m -> m"; "    | m & { > 100 } & _ -> m"; "    | _ -> 0"; "}" ])
    unchanged (lines [ "def g = {"; "  | #some(n & { > 1 }) -> n"; "  | (a & 1, b) -> b"; "  | #error e & whole -> 0"; "  | _ -> 4"; "}" ])
    Assert.Equal("def h = { | a & b -> 1 }\n", format "def h = { | a&b -> 1 }")
    // A tag's payload written after a space keeps its brackets: without them the tag would be a part.
    unchanged (lines [ "def i = {"; "  | #some (a & b) -> 1"; "  | _ -> 2"; "}" ])

[<Fact>]
let ``Record patterns by member name keep their ...`` () =
    unchanged (lines [ "def f = {"; "  | (name = \"Alice\", ...) -> 1"; "  | (name = n, age = a) -> 2"; "}" ])
    Assert.Equal("def g = { | (a = 1, ...) -> 1 }\n", format "def g = { | ( a = 1 ,... ) -> 1 }")

[<Fact>]
let ``if, if-is and multi-branch if are printed as written`` () =
    unchanged "def a = { x -> if x > 0 then 1 else 0 }\n"
    unchanged "def b = { x -> if x is #some(y) then y else 0 }\n"
    unchanged (lines [ "def c = { x ->"; "  if x > 0 then"; "    println(\"positive\")"; "}" ])
    unchanged (lines [ "def d = { x ->"; "  if"; "    | x < 0 -> \"negative\""; "    | x is 0 -> \"zero\""; "    | else -> \"positive\""; "}" ])

[<Fact>]
let ``Or-patterns, rec and destructuring defs are printed as written`` () =
    unchanged (lines [ "def f = {"; "  | 0 | 1 -> \"small\""; "  | _ -> \"big\""; "}" ])
    unchanged (lines [ "def g = {"; "  rec loop = { n -> loop(n) }"; "  loop(1)"; "}" ])
    unchanged "def (a, (b, _)) = pair\n"
    unchanged "def a, (b, _) = pair\n"
    unchanged "def low, high = sort(3, -1)\n"
    unchanged (lines [ "def f = { pair ->"; "  def a, b = pair"; "  a + b"; "}" ])
    unchanged "def $Money(pence) = m\n"
    unchanged "def [first, ...rest] = xs\n"
    unchanged (lines [ "def f = { m ->"; "  def $Money(pence) = m"; "  pence"; "}" ])
