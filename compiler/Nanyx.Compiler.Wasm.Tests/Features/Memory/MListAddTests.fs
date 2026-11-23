module TranspilerWasmTests.Features.Memory.MListAdd

open Xunit
open TranspilerWasmTestHelpers

// `MList.add` writes the list's first cell, last cell and count into references of their own, so the only block it
// allocates is the new cell (#2403). The allocation tests count the blocks the allocator hands out, with memory checks
// on.

let private program =
    normalized """import nanyx/mlist

def built: int -> int = { n ->
  memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    MList.length(items)
  }
}

def handed: int -> int = { n ->
  def xs = memory {
    def items = MList.empty()
    for 1..n | i -> items += i
    items \toList
  }
  list.length(xs)
}
"""

[<Fact>]
let ``MList - add allocates only the new cell`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let hundred, count = allocationsOf program "built" 100
    let twoHundred, _ = allocationsOf program "built" 200
    Assert.Equal(100, count)
    Assert.Equal(100, twoHundred - hundred)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``MList - a list handed over keeps one block per element`` () =
    use program = instantiateWasm program []
    let liveBefore = program.LiveBlocks
    let hundred, count = allocationsOf program "handed" 100
    let twoHundred, _ = allocationsOf program "handed" 200
    Assert.Equal(100, count)
    Assert.Equal(100, twoHundred - hundred)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Run - a reference an inner block adds to an outer block's MList lives until the outer block ends`` () =
    let source = """
import nanyx/mlist
import nanyx/mref

def v = memory { context m ->
  def items: MList(MRef(int)) = MList.empty()
  items += MRef.new(1)
  memory {
    use m
    items += MRef.new(2)
    items += MRef.new(3)
  }
  MList.toList(items) \list.map { r -> MRef.get(r) } \list.sum
}
println(v)
"""
    runSource (normalized source) |> assertRan "6\n"

[<Fact>]
let ``Run - an MList that holds itself through a reference is freed when its block ends`` () =
    let source = """
import nanyx/mlist
import nanyx/mref

type Node = [Memory] protected (name: string, owner: MRef(#some(MList(Node)) | #none))

def n = memory {
  def total = MRef.new(0)
  for [1, 2, 3] | i ->
    memory {
      def items: MList(Node) = MList.empty()
      def owner: MRef(#some(MList(Node)) | #none) = MRef.new(#none)
      items += Node(name = "node {i}", owner = owner)
      MRef.set(owner, #some(items))
    }
    MRef.update(total, { t -> t + i })
  MRef.get(total)
}
println(n)
"""
    runSource (normalized source) |> assertRan "6\n"
