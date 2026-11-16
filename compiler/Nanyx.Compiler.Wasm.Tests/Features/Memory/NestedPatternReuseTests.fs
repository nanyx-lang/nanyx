module TranspilerWasmTests.Features.Memory.NestedPatternReuse

open Xunit
open TranspilerWasmTestHelpers

// A `match` arm whose pattern takes apart an object inside the one it matches, as `[b, a, ...rest]` takes apart the
// second cell, releases each object and reuses its block as the hand-written nested `match` does, and an arm that
// takes apart several scrutinees reuses each one's block (see `CoreIrReuse`). The tests count the blocks the allocator
// hands out (#1547).

let private counting =
    normalized """def countUp: int, int, list(int) -> list(int) = { i, n, acc ->
  if i > n then acc else countUp(i + 1, n, [i, ...acc])
}
def sum: list(int) -> int = {
  | [] -> 0
  | [x, ...rest] -> x + sum(rest)
}
def pairs: list(int), list(int) -> list(int) = {
  | [b, a, ...rest], acc -> pairs(rest, [a + b, ...acc])
  | _, acc -> acc
}
def built: int -> int = { n -> sum(countUp(1, n, [])) }
"""

[<Fact>]
let ``Nested reuse - an arm that takes apart two cells of a list held once allocates nothing`` () =
    use program = instantiateWasm (counting + "def run: int -> int = { n -> sum(pairs(countUp(1, n, []), [])) }\n") []
    let buildAllocations, _ = allocationsOf program "built" 100
    let pairAllocations, total = allocationsOf program "run" 100
    Assert.Equal(100, buildAllocations)
    Assert.Equal(buildAllocations, pairAllocations)
    Assert.Equal(5050, total)

[<Fact>]
let ``Nested reuse - an arm that takes apart a tag inside a list cell allocates nothing`` () =
    let source =
        normalized """def somes: int, list(#some(int) | #none) -> list(#some(int) | #none) = { n, acc ->
  if n == 0 then acc else somes(n - 1, [#some(n), ...acc])
}
def bump: list(#some(int) | #none), list(#some(int) | #none) -> list(#some(int) | #none) = {
  | [#some(n), ...rest], acc -> bump(rest, [#some(n + 1), ...acc])
  | _, acc -> acc
}
def total: list(#some(int) | #none) -> int = {
  | [#some(n), ...rest] -> n + total(rest)
  | _ -> 0
}
def built: int -> int = { n -> total(somes(n, [])) }
def run: int -> int = { n -> total(bump(somes(n, []), [])) }
"""
    use program = instantiateWasm source []
    let buildAllocations, _ = allocationsOf program "built" 100
    let bumpAllocations, result = allocationsOf program "run" 100
    Assert.Equal(200, buildAllocations)
    Assert.Equal(buildAllocations, bumpAllocations)
    Assert.Equal(5050 + 100, result)

[<Fact>]
let ``Nested reuse - an arm reuses the cell of a scrutinee after one of another size`` () =
    let source =
        counting
        + normalized """def names: int, list(string) -> list(string) = { n, acc ->
  if n == 0 then acc else names(n - 1, ["x", ...acc])
}
def zipBump: list(string), list(int), list(int) -> list(int) = {
  | [_, ...names], [a, ...rest], acc -> zipBump(names, rest, [a + 1, ...acc])
  | _, _, acc -> acc
}
def both: int -> int = { n -> list.length(names(n, [])) + sum(countUp(1, n, [])) }
def run: int -> int = { n -> sum(zipBump(names(n, []), countUp(1, n, []), [])) }
"""
    use program = instantiateWasm source []
    let buildAllocations, _ = allocationsOf program "both" 100
    let zipAllocations, result = allocationsOf program "run" 100
    Assert.Equal(200, buildAllocations)
    Assert.Equal(buildAllocations, zipAllocations)
    Assert.Equal(5050 + 100, result)

[<Fact>]
let ``Nested reuse - taking apart a list still used afterwards leaves it as it was`` () =
    let source =
        counting
        + normalized """
@entry
def main = {
  def xs = countUp(1, 10, [])
  def ys = pairs(xs, [])
  println(sum(xs))
  println(sum(ys))
  println(list.length(xs))
}
"""
    let outcome = runSource source
    Assert.Equal("", outcome.StandardError)
    Assert.Equal("55\n55\n10\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Fact>]
let ``Nested reuse - patterns of a fixed length, with literals, tags and records inside, free what they take apart`` () =
    let source =
        normalized """def describe: list(int) -> string = {
  | [a, b] -> "two: {a + b}"
  | [0, ...rest] -> "zero then {list.length(rest)}"
  | [a, b, c, ...rest] -> "three: {a + b + c} and {list.length(rest)}"
  | _ -> "other"
}

def firstNames: list((name: string, age: int)) -> list(string) = {
  | [(name = n, age = 1), ...rest] -> ["baby {n}", ...firstNames(rest)]
  | [(name = n, ...), ...rest] -> [n, ...firstNames(rest)]
  | [] -> []
}

def nested: list(#some(#some(string) | #none) | #none) -> list(string) = {
  | [#some(#some(s)), ...rest] -> [s, ...nested(rest)]
  | [_, ...rest] -> nested(rest)
  | [] -> []
}

@entry
def main = {
  println(describe([1, 2]))
  println(describe([0, 5, 6]))
  println(describe([1, 2, 3, 4, 5]))
  println(describe([7]))
  println(firstNames([(name = "Ada", age = 36), (name = "Bo", age = 1)]))
  def shared = [#some(#some("x")), #none, #some(#none), #some(#some("y"))]
  println(nested(shared))
  println(nested(shared))
}
"""
    let outcome = runSource source
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(
        """two: 3
zero then 2
three: 6 and 2
other
[Ada, baby Bo]
[x, y]
[x, y]
""",
        outcome.StandardOutput.ReplaceLineEndings "\n")
