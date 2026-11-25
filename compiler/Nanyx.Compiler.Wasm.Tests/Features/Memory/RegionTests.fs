module TranspilerWasmTests.Features.Memory.Regions

open Xunit
open TranspilerWasmTestHelpers

// A `memory` block frees its mutable objects when it ends, so a cycle through an `MRef`, a captured cell or an `MArray`
// is freed (#2232). Each block has
// a region, and a value stored into an outer block's object, or given back by a block, moves onto the outer block's
// region first (#2376), so an inner block frees what it made that nothing outside it holds. The tests run with memory
// checks on, so an object the regions don't free fails them, and so does one freed while something can still read it.

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
let ``Run - a cycle through a captured cell is freed when the block ends`` () =
    let source = """
def apply: [c] ([c] int -> int), int -> int = { g, x -> g(x) }

def n = memory {
  mut countdown: [Memory] int -> int = { x -> x }
  countdown := { x -> if x > 0 then apply(countdown, x - 1) else 10 }
  apply(countdown, 3)
}
println(n)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "10\n"

[<Fact>]
let ``Run - a cycle through an MArray is freed when the block ends`` () =
    let source = """
import nanyx/marray

type Node = [Memory] protected (name: string, children: MArray(#some(Node) | #none))

def n = memory {
  def children: MArray(#some(Node) | #none) = MArray.init(2, { _ -> #none })
  def a = Node(name = "abc", children = children)
  MArray.set(children, 0, #some(a))
  MArray.length(children)
}
println(n)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "2\n"

[<Fact>]
let ``Run - an MArray made in an inner block under the outer block's Memory lives until the outer block ends`` () =
    let source = """
import (
  nanyx/marray
  nanyx/mref
)

def v = memory { context m ->
  def r: MRef(#some(MArray(MRef(int))) | #none) = MRef.new(#none)
  memory {
    use m
    MRef.set(r, #some(MArray.init(3, { i -> MRef.new(i * 2) })))
  }
  match MRef.get(r)
    | #some(xs) ->
      match MArray.at(xs, 2)
        | #some(inner) -> MRef.get(inner)
        | #notFound -> 0
    | #none -> 0
}
println(v)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "4\n"

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
let ``Run - a cycle made in an inner block is freed`` () =
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

// The a -> b -> c case: `a`'s block calls `b`, which has none, and `b` calls `c`, which has a block of its own that makes
// a cycle. `c`'s block frees its cycle when it ends, while `a`'s runs on, so the most memory in use at once doesn't
// grow with how many times `c` runs.
let private nestedCycles =
    normalized """import nanyx/mref

type Node = [Memory] protected (name: string, next: MRef(#some(Node) | #none))

def c: int -> int = { i ->
  memory {
    def empty: MRef(#some(Node) | #none) = MRef.new(#none)
    MRef.set(empty, #some(Node(name = "node", next = empty)))
    i
  }
}

def b: int -> int = { n -> if n == 0 then 0 else c(n) + b(n - 1) }

def a: int -> int = { n ->
  memory {
    def total = MRef.new(0)
    MRef.set(total, b(n))
    MRef.get(total)
  }
}
"""

[<Fact>]
let ``A block called from inside another frees its cycle when it ends`` () =
    let peakAfter (n: int) =
        use program = instantiateWasm nestedCycles []
        let liveBefore = program.LiveBlocks
        Assert.Equal(n * (n + 1) / 2, program.Call("a", n))
        Assert.Equal(liveBefore, program.LiveBlocks)
        program.PeakLiveBytes
    Assert.Equal(peakAfter 2, peakAfter 200)

/// A script whose statement is a `memory` block that stores into its local `count` times, then prints it.
let private storing (count: int) =
    String.concat "\n" [ "memory {"; "  mut x = 0"; yield! List.replicate count "  x := x + 1"; "  println(x)"; "}"; "" ]

[<Fact>]
let ``Run - a script's memory block with hundreds of stores runs`` () =
    // At 250 stores this once failed in the backend with a temporary used out of order (#2409).
    for count in [ 250; 2000 ] do
        runSource (storing count) |> assertRan $"{count}\n"

/// Blocks whose cells are all locals and that call nothing that can make a box, as a loop is written.
let private localCells =
    normalized """def digits: int -> int = { n ->
  memory {
    mut rest = n
    mut count = 0

    while rest > 0 ->
      rest := rest // 10
      count := count + 1

    count
  }
}

def sumRange: int -> int = { n ->
  memory {
    mut total = 0
    mut i = 1

    while i <= n ->
      total := total + i
      i := i + 1

    total
  }
}
"""

// A block that nothing can put an object on has no region, so it allocates nothing when it runs, and a module of such
// blocks has no region helpers (#2418). Each one allocated and freed a region at every run.
[<Fact>]
let ``A block whose cells are all locals allocates nothing when it runs`` () =
    use program = instantiateWasm localCells []
    let before = program.Allocations
    for n in 1..100 do
        Assert.Equal(string n |> String.length, program.Call("digits", n))
        Assert.Equal(n * (n + 1) / 2, program.Call("sumRange", n))
    Assert.Equal(before, program.Allocations)

[<Fact>]
let ``A program whose blocks hold only local cells has no regions`` () =
    let wat = transpileWat (localCells + "\nprintln(digits(2024) + sumRange(10))\n")
    Assert.DoesNotContain("__nanyx_region_enter", wat)
    Assert.DoesNotContain("__nanyx_region_exit", wat)
