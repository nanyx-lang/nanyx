/// A `for` loop walks anything `Iterable`: a type that attaches `toSeq`, an array, a slice, a stack, a mutable array,
/// and a value of a type a signature requires `Iterable` of (see
/// docs/src/content/deep-dives/lazy-sequences.md).
module TranspilerWasmTests.Features.Loops.RunIterableLoop

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a for loop walks a user's type that attaches toSeq`` () =
    let source =
        normalized """type Bag = (items: list(string))

def Bag.toSeq: Bag -> seq(string) = { b -> list.toSeq(b.items) }

for Bag(items = ["a", "b"]) | x -> println(x)
"""
    runSource source |> assertRan "a\nb\n"

[<Fact>]
let ``Run - a for loop walks an array, a slice, a stack and a mutable array`` () =
    let source =
        normalized """import nanyx/slice
import nanyx/stack
import nanyx/marray

for Array[1, 2] | x -> println("array {x}")
for Array.slice(Array[3, 4, 5], 1, 3) | x -> println("slice {x}")
for Stack[6, 7] | x -> println("stack {x}")
memory {
  def m = MArray[8, 9]
  for m | x -> println("marray {x}")
}
"""
    runSource source |> assertRan "array 1\narray 2\nslice 4\nslice 5\nstack 6\nstack 7\nmarray 8\nmarray 9\n"

[<Fact>]
let ``Run - a for loop walks a value of a type its function requires Iterable of`` () =
    let source =
        normalized """import nanyx/stack

type Bag = (items: list(string))

def Bag.toSeq: Bag -> seq(string) = { b -> list.toSeq(b.items) }

def shout: [Console, Iterable(c, elem = string)] c -> () = { words -> for words | w -> println(w ++ "!") }

def each: [Console, Iterable(c), ToString(elem)] c -> () = { xs -> for xs | x -> println("each {x}") }

shout(Bag(items = ["a", "b"]))
shout(["c"])
each(Stack[1, 2])
each(Array["x"])
"""
    runSource source |> assertRan "a!\nb!\nc!\neach 1\neach 2\neach x\n"

[<Fact>]
let ``Run - a function over Iterable runs at a generic type whose toSeq fixes elem`` () =
    let source =
        normalized """type Bag(a) = protected (items: list(a))

def Bag.toSeq: Bag(a) -> seq(a) = { b -> list.toSeq(b.items) }

def count: [Iterable(c)] c -> int = { xs -> seq.fold(toSeq(xs), 0, { n, _ -> n + 1 }) }

def total: [Iterable(c, elem = int)] c -> int = { xs -> seq.fold(toSeq(xs), 0, { n, x -> n + x }) }

println(count(Bag(items = ["a", "b", "c"])))
println(count([1, 2]))
println(total(Bag(items = [4, 5])))
println(total([6]))
"""
    runSource source |> assertRan "3\n2\n9\n6\n"
