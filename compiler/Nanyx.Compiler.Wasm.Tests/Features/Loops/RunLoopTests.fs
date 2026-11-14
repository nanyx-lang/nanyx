module TranspilerWasmTests.Features.Loops.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - for loops over ranges and lists`` () =
    // Regression: `for 1..3 | i -> println("{i}")` was a parse error (#37).
    let source =
        normalized """import nanyx/console

def shout: [Console] list(string) -> () = { names ->
  for names | name -> println(name ++ "!")
}
@entry
def main = {
  for 1..3 | i -> println("{i}")
  for ["a", "b"] | item -> println(item)
  for 1..5 | 5 -> println("Go!")
           | _ -> println("Ready...")
  for 1..2 -> println("tick")
  def n = 2
  for 1..n | i ->
    for 1..n | j -> println("{i},{j}")
  shout(["hey"])
}
"""
    runSource source |> assertRan "1\n2\n3\na\nb\nReady...\nReady...\nReady...\nReady...\nGo!\ntick\ntick\n1,1\n1,2\n2,1\n2,2\nhey!\n"

[<Fact>]
let ``Run - for loops over several iterables walk them in step`` () =
    // The program from the loops page: a list zipped with a range that has no end counts its elements.
    let source =
        normalized """import nanyx/console

def things = [
  "Alex"
  "Sam"
  "Jo"
]

@entry
def main = {
  for things | thing ->
    println(thing)
  for things, 0.. | thing, i ->
    println("{i}: {thing}")
  for things, 1..2 | "Sam", i -> println("Sam is {i}")
                   | thing, _ -> println("not Sam: {thing}")
  for 10..12, [[1], [2, 3], [4, 5, 6], [7]], 100.. | a, xs, c -> println("{a} {list.length(xs)} {c}")
  for things, things -> println("tick")
  for things, 0.. | thing, i ->
    for [true, false], 0.. | flag, j -> println("{i}.{j} {thing} {flag}")
}
"""
    let expected =
        normalized """Alex
Sam
Jo
0: Alex
1: Sam
2: Jo
not Sam: Alex
Sam is 2
10 1 100
11 2 101
12 3 102
tick
tick
tick
0.0 Alex true
0.1 Alex false
1.0 Sam true
1.1 Sam false
2.0 Jo true
2.1 Jo false
"""
    runSource source |> assertRan expected

[<Fact>]
let ``Run - rec local loops`` () =
    // Regression: `rec loop = { ... }` was a parse error (#38).
    let source =
        normalized """import nanyx/console

def countdown: int -> int = { n ->
  rec loop = { i, acc -> if i > 0 then loop(i - 1, acc + i) else acc }
  loop(n, 0)
}
@entry
def main = {
  println("{countdown(10)}")
  rec say = { i ->
    if i > 0 then
      println("{i}")
      say(i - 1)
  }
  say(2)
}
"""
    runSource source |> assertRan "55\n2\n1\n"

[<Fact>]
let ``Run - while loops run while their condition holds`` () =
    // The program from the loops page (#957), and loops whose condition and body read a list held in a cell: each time
    // round they borrow it, so the memory checks find nothing left over.
    let source = """
import nanyx/console

def countdown = {
  memory {
    mut remaining = 3

    while remaining > 0 ->
      println("{remaining}...")
      remaining := remaining - 1
  }
}

def pairs = { n ->
  memory {
    mut i = 0
    while i < n ->
      mut j = 0
      while j < i ->
        println("{i},{j}")
        j := j + 1
      i := i + 1
  }
}

def drain = {
  memory {
    mut items = ["a", "b", "c"]
    while list.length(items) > 0 ->
      match items
        | [first, ...rest] ->
          println(first)
          items := rest
        | [] -> ()
    while false -> println("never")
  }
}

@entry
def main = {
  countdown()
  pairs(3)
  drain()
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3...\n2...\n1...\n1,0\n2,0\n2,1\na\nb\nc\n"

[<Fact>]
let ``Run - loops, floats, pipes and guards through the core IR`` () =
    let source =
        normalized """import nanyx/console

def area: float -> float = { r -> r * r * 2.0 }

def size = { n ->
  match n
    | > 100 -> "big"
    | < 0 -> "negative"
    | _ -> "small"
}

def double: int -> int = { n -> n * 2 }

def main = {
  for [1, 60, -3] | n -> println(size(n \double))
  for 1..2 | i -> println("{i}: {area(1.5)}")
  if area(1.0) > 1.5 then println("yes") else println("no")
}

main()
"""
    runSource source |> assertRan "small\nbig\nnegative\n1: 4.5\n2: 4.5\nyes\n"
