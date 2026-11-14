module TranspilerWasmTests.Features.Matching.RunAndPatterns

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - the documented guard with an and-pattern`` () =
    // The pattern-matching page's "Guards" example (#427).
    let source =
        normalized """import nanyx/console

def describeNumber: int -> string = { n ->
  match n
    | { < 0 } -> "negative"
    | 0 -> "zero"
    | n & { > 100 } -> "{n}, which is large!"
    | _ -> "positive"
}
@entry
def main = {
  println(describeNumber(-3))
  println(describeNumber(0))
  println(describeNumber(500))
  println(describeNumber(7))
}
"""
    runSource source |> assertRan "negative\nzero\n500, which is large!\npositive\n"

[<Fact>]
let ``Run - the documented and-patterns, the name after the guard`` () =
    // The pattern-matching page's "And patterns" example (#427).
    let source =
        normalized """import nanyx/console

def show: int -> string = { value ->
  match value
    | { < 0 } & n -> "Negative: {n}"
    | { > 100 } & n -> "Large: {n}"
    | n -> "Something else: {n}"
}
@entry
def main = {
  println(show(-5))
  println(show(200))
  println(show(42))
}
"""
    runSource source |> assertRan "Negative: -5\nLarge: 200\nSomething else: 42\n"

[<Fact>]
let ``Run - and-patterns nested in a tag and a tuple`` () =
    // Inside another pattern, every part matches the same part of the value, and binds what it binds.
    let source =
        normalized """import nanyx/console

def size: #some(int) | #none -> string = { value ->
  match value
    | #some(n & { > 10 }) -> "big {n}"
    | #some(n) -> "small {n}"
    | #none -> "nothing"
}
def pair: (int, int) -> string = { p ->
  match p
    | (a, b) & (0, _) & whole -> "zero first, then {b}, {a} and {whole.1}"
    | (a & 1, b) -> "one first, then {b} ({a})"
    | _ -> "other"
}
@entry
def main = {
  println(size(#some(20)))
  println(size(#some(3)))
  println(size(#none))
  println(pair((0, 9)))
  println(pair((1, 8)))
  println(pair((2, 7)))
}
"""
    runSource source
    |> assertRan "big 20\nsmall 3\nnothing\nzero first, then 9, 0 and 9\none first, then 8 (1)\nother\n"

[<Fact>]
let ``Run - an and-pattern tests its parts left to right`` () =
    // `#some(_) & { ... }`: the predicate runs only once the tag matches, so it can take the payload apart.
    let source =
        normalized """import nanyx/console

def payload: #some(int) | #none -> int = { value ->
  match value
    | #some(n) -> n
    | #none -> 0
}
def check: #some(int) | #none -> string = { value ->
  match value
    | #some(_) & { v -> payload(v) > 5 } -> "more than five"
    | _ -> "not"
}
@entry
def main = {
  println(check(#some(6)))
  println(check(#some(1)))
  println(check(#none))
}
"""
    runSource source |> assertRan "more than five\nnot\nnot\n"
