module TranspilerWasmTests.Features.StdLib.RunSequences

open Xunit
open TranspilerWasmTestHelpers

// The prelude's lazy sequence, `seq(a)`, and ranges as values of it (#192). Each program runs with its memory checked.

[<Fact>]
let ``Run - a range is mapped and made a list`` () =
    runSource """
println((1..10) \seq.map { * 2 } \seq.toList)
"""
    |> assertRan "[2, 4, 6, 8, 10, 12, 14, 16, 18, 20]\n"

[<Fact>]
let ``Run - a range without an end is cut short by take`` () =
    runSource """
def naturals = 0..

println(naturals \seq.filter { x -> x % 3 == 0 } \seq.take(4) \seq.toList)
println(naturals \seq.skip(5) \seq.takeWhile { x -> x < 9 } \seq.toList)
"""
    |> assertRan "[0, 3, 6, 9]\n[5, 6, 7, 8]\n"

[<Fact>]
let ``Run - a sequence is pulled only as far as it is needed, when it is`` () =
    runSource """
def loud = (1..5) \seq.map { x ->
  println("pulling {x}")
  x * 10
}

println("made")
println(loud \seq.take(2) \seq.toList)
"""
    |> assertRan "made\npulling 1\npulling 2\n[10, 20]\n"

[<Fact>]
let ``Run - a sequence is unfolded, folded and made from a list`` () =
    runSource """
println(seq.unfold(1, { n -> if n > 50 then #done else #some(n, n * 3) }) \seq.toList)
println((1..4) \seq.fold(0) { + })
println([5, 6, 7] \list.toSeq \seq.map { x -> x + 1 } \seq.toList)
println(seq.empty() \seq.toList \list.length)
"""
    |> assertRan "[1, 3, 9, 27]\n10\n[6, 7, 8]\n0\n"

[<Fact>]
let ``Run - a sequence is pulled by matching on what next gives`` () =
    runSource """
def total: seq(int) -> int = { s ->
  match s.next()
    | #next(x, rest) -> x + total(rest)
    | #done -> 0
}

println(total(1..100))
"""
    |> assertRan "5050\n"

[<Fact>]
let ``Run - a for loop over a range still counts`` () =
    runSource """
for 1..3 | i -> println(i)
for ["a", "b"], 10.. | s, i -> println("{s}{i}")
"""
    |> assertRan "1\n2\n3\na10\nb11\n"

[<Fact>]
let ``Run - a sequence made in a pure function runs its function's context when it is pulled`` () =
    // #1738
    runSource """
def made: () -> [Console] seq(int) = {
  (1..3) \seq.map { x ->
    println("pull {x}")
    x * 10
  }
}

def first: [Console] () -> () = {
  def s = made()
  println("made")
  match s.next()
    | #next(x, _) -> println("got {x}")
    | #done -> ()
}

first()
"""
    |> assertRan "made\npull 1\ngot 10\n"

[<Fact>]
let ``Run - a function that requires a context is passed on to concatMap through a signature's context variable`` () =
    // #1908
    runSource """
def flat: [c] list(a), ([c] a -> [c] seq(b)) -> list(b) = { xs, f ->
  xs \list.toSeq \seq.concatMap(f) \seq.toList
}

println(flat([1, 2], { x ->
  println("at {x}")
  list.toSeq([x])
}))

def pulled: () -> [Console] seq(int) = {
  (1..2) \seq.map { x ->
    println("pull {x}")
    x
  }
}

println(seq.cons(0, { pulled() }) \seq.append(list.toSeq([3])) \seq.toList)
"""
    |> assertRan "at 1\nat 2\n[1, 2]\npull 1\npull 2\n[0, 1, 2, 3]\n"
