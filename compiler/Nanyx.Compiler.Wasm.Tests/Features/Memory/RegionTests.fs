module TranspilerWasmTests.Features.Memory.Regions

open Xunit
open TranspilerWasmTestHelpers

// A `memory` block frees its mutable objects when it ends, so a cycle through an `MRef` is freed (#2232). The outermost
// block running has the region the objects are on, so an object made in an inner block is freed when the outermost one
// ends. The tests run with memory checks on, so an object the region doesn't free fails them, and so does one it
// frees while something can still read it.

[<Fact>]
let ``Run - a cycle through an MRef is freed when the block ends`` () =
    let source = """
import nanyx/mref

type Node = [Memory] protected (name: string, next: MRef(#some(Node) | #none))

def n = memory {
  def empty: MRef(#some(Node) | #none) = MRef.new(#none)
  def a = Node(name = "abc", next = empty)
  MRef.set(empty, #some(a))
  3
}
println(n)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3\n"

[<Fact>]
let ``Run - two references that hold each other are freed when the block ends`` () =
    let source = """
import nanyx/mref

type Pair = [Memory] protected (label: string, other: MRef(#some(Pair) | #none))

def labels = memory {
  def first: MRef(#some(Pair) | #none) = MRef.new(#none)
  def second: MRef(#some(Pair) | #none) = MRef.new(#none)
  MRef.set(first, #some(Pair(label = "one", other = second)))
  MRef.set(second, #some(Pair(label = "two", other = first)))
  match MRef.get(first)
    | #some(pair) -> pair.label
    | #none -> ""
}
println(labels)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "one\n"

[<Fact>]
let ``Run - an object made in an inner block under the outer block's Memory lives until the outer block ends`` () =
    let source = """
import nanyx/mref

def v = memory { context m ->
  def r: MRef(#some(MRef(int)) | #none) = MRef.new(#none)
  memory {
    use m
    MRef.set(r, #some(MRef.new(7)))
  }
  match MRef.get(r)
    | #some(inner) -> MRef.get(inner)
    | #none -> 0
}
println(v)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "7\n"

[<Fact>]
let ``Run - an object a lambda of the outer block makes in an inner block lives until the outer block ends`` () =
    let source = """
import nanyx/mref

def v = memory {
  def r: MRef(#some(MRef(int)) | #none) = MRef.new(#none)
  def make = { -> MRef.new(5) }
  memory {
    MRef.set(r, #some(make()))
  }
  match MRef.get(r)
    | #some(inner) -> MRef.get(inner)
    | #none -> 0
}
println(v)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "5\n"

[<Fact>]
let ``Run - a cycle made in an inner block is freed when the outermost block ends`` () =
    let source = """
import nanyx/mref

type Node = [Memory] protected (name: string, next: MRef(#some(Node) | #none))

def n = memory {
  def total = MRef.new(0)
  for [1, 2, 3] | i ->
    memory {
      def empty: MRef(#some(Node) | #none) = MRef.new(#none)
      MRef.set(empty, #some(Node(name = "node {i}", next = empty)))
    }
    MRef.update(total, { t -> t + i })
  MRef.get(total)
}
println(n)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "6\n"

[<Fact>]
let ``Run - a block that a workflow's try or return ends frees its objects`` () =
    let source = """
import nanyx/mref

type Node = [Memory] protected (name: string, next: MRef(#some(Node) | #none))

def f: int -> #some(int) | #negative = { n ->
  proc {
    memory {
      def empty: MRef(#some(Node) | #none) = MRef.new(#none)
      MRef.set(empty, #some(Node(name = "x", next = empty)))
      if n < 0 then try #negative
      return n
    }
  }
}
println(f(1))
println(f(-1))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(1)\n#negative\n"

let private repeated =
    normalized """import nanyx/mref

type Node = [Memory] protected (name: string, next: MRef(#some(Node) | #none))

def cycle: int -> int = { n ->
  memory {
    def empty: MRef(#some(Node) | #none) = MRef.new(#none)
    MRef.set(empty, #some(Node(name = "node {n}", next = empty)))
    n
  }
}
"""

[<Fact>]
let ``A block that runs again and again frees its cycle each time it ends`` () =
    use program = instantiateWasm repeated []
    let liveBefore = program.LiveBlocks
    for n in 1..100 do
        Assert.Equal(n, program.Call("cycle", n))
    Assert.Equal(liveBefore, program.LiveBlocks)
