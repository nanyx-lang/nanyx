module TranspilerWasmTests.Features.StdLib.Ranges

open Xunit
open TranspilerWasmTestHelpers

// Ranges of any type with an `Enumerable` and an `Ord` (#1700): `a..b` is `seq.range(a, b)`, and a range of `int`s
// still counts.

[<Fact>]
let ``Run - a range of ints counts, as a value and in a for loop`` () =
    let source = """
def sumTo: int -> int = { n -> (1..n) \seq.fold(0, { total, x -> total + x }) }

for 1..3 | i -> println(i)
for ["a", "b"], 1.. | letter, n -> println("{n}. {letter}")
println(sumTo(10))
println((0..) \seq.take(3) \seq.toList)
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "1\n2\n3\n1. a\n2. b\n55\n[0, 1, 2]\n"

[<Fact>]
let ``Run - an empty range has no elements`` () =
    let source = """
for 3..1 | i -> println(i)
println((5..1) \seq.toList)
println(list.length((1..0) \seq.toList))
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "[]\n0\n"

[<Fact>]
let ``Run - a range of a type with an attached succ and compare`` () =
    let source = """
type Point = protected (x: int, y: int)

def Point.succ: Point -> Point = { p -> Point(x = p.x + 1, y = p.y + 1) }

def Point.compare: Point, Point -> #less | #equal | #greater = { p1, p2 ->
  int.compare(p1.x + p1.y, p2.x + p2.y)
}

def points = Point(x = 1, y = 1)..Point(x = 3, y = 3)

for points | p -> println("{p.x}, {p.y}")
println(points \seq.map { p -> p.x } \seq.toList)
for Point(x = 0, y = 0).., [10, 20] | p, n -> println("{n}: {p.x}")
println(list.length((Point(x = 2, y = 2)..Point(x = 1, y = 1)) \seq.toList))
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "1, 1\n2, 2\n3, 3\n[1, 2, 3]\n10: 0\n20: 1\n0\n"

[<Fact>]
let ``Run - a function that requires Enumerable and Ord makes a range of its type variable`` () =
    let source = """
def between: [Enumerable(a), Ord(a)] a, a -> list(a) = { first, last -> (first..last) \seq.toList }

type Day = protected (n: int)

def Day.succ: Day -> Day = { d -> Day(n = d.n + 1) }

def Day.compare: Day, Day -> #less | #equal | #greater = { a, b -> int.compare(a.n, b.n) }

println(between(3, 5))
println(between(Day(n = 1), Day(n = 3)) \list.map { d -> d.n })
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "[3, 4, 5]\n[1, 2, 3]\n"

[<Fact>]
let ``A range of a type with no succ is missing its Enumerable`` () =
    let source = """
type Name = protected (s: string)

def Name.compare: Name, Name -> #less | #equal | #greater = { a, b -> string.compare(a.s, b.s) }

def names = Name(s = "a")..Name(s = "b")
"""
    (checkScript (source.ReplaceLineEndings "\n")).StandardOutput
    |> assertDiagnostic "NYX0205" "Missing context 'Enumerable'" "main.nyx:6:13" "'Sequence.range' requires the context 'Enumerable(Name)'"

[<Fact>]
let ``A range of ints counts without passing an Enumerable`` () =
    let source = """
def sumTo: int -> int = { n -> (1..n) \seq.fold(0, { total, x -> total + x }) }

def count: int -> () = { n ->
  for 1..n | i -> println(i)
}

count(3)
println(sumTo(3))
println((0..) \seq.take(2) \seq.toList)
"""
    let wat = transpileWatNotInlined (source.ReplaceLineEndings "\n")
    Assert.Equal(0, countOccurrences "call $Seq_range" wat)
    Assert.Equal(0, countOccurrences "call $Seq_from" wat)
    Assert.Equal(0, countOccurrences "succ" wat)
    // The `for` loop counts: only `sumTo`'s range is made.
    Assert.Equal(1, countOccurrences "call $__intRange" (functionText "sumTo" wat))
    Assert.Equal(0, countOccurrences "call $__intRange" (functionText "count" wat))
