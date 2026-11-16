/// A `for` loop over a `seq(a)` value pulls it one element at a time, the body running between pulls (#1698, see
/// docs/src/content/work-in-progress/lazy-sequences.md).
module TranspilerWasmTests.Features.Loops.RunSeqLoop

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a for loop over a seq value runs the body for each element`` () =
    let source =
        normalized """def evens = (0..10) \seq.filter { x -> x % 2 == 0 }

for evens | x -> println(x)
"""
    runSource source |> assertRan "0\n2\n4\n6\n8\n10\n"

[<Fact>]
let ``Run - a for loop over a seq that prints pulls each element just before the body runs`` () =
    let source =
        normalized """def each: [Console] ([Console] seq(int)) -> () = { s ->
  for s | x -> println("got {x}")
}

@entry
def main: [Console] () -> () = {
  def loud = (1..3) \seq.map { x ->
    println("pull {x}")
    x * 10
  }
  each(loud)
  for loud | x -> println("again {x}")
}
"""
    runSource source |> assertRan "pull 1\ngot 10\npull 2\ngot 20\npull 3\ngot 30\npull 1\nagain 10\npull 2\nagain 20\npull 3\nagain 30\n"

[<Fact>]
let ``Run - a for loop walks a list and a seq in step, ending with the shorter`` () =
    let source =
        normalized """def doubled = (1..) \seq.map { x -> x * 2 }

for ["a", "b", "c"], doubled | x, y -> println("{x}{y}")
for (1..2) \seq.map { x -> "{x}" }, ["p", "q", "r"], 10.. | x, y, z -> println("{x}{y}{z}")
"""
    runSource source |> assertRan "a2\nb4\nc6\n1p10\n2q11\n"
