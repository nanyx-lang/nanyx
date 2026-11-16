module TranspilerWasmTests.Features.Memory.MRefs

open Xunit
open TranspilerWasmTestHelpers

// `MRef(a)` in `nanyx/mref`: the box a captured cell is, as a value. The tests run with memory checks on, so a value an
// `MRef` does not give up, when it is changed or when the `MRef` is dropped, is reported.

[<Fact>]
let ``Run - an MRef is read, set and updated, and every holder sees the change`` () =
    let source = """
import nanyx/mref

def bump = { r -> MRef.update(r, { n -> n + 1 }) }

@entry
def main = {
  def total = memory {
    def r = MRef.new(1)
    def holder = (ref = r, name = "x")
    MRef.set(r, 5)
    bump(holder.ref)
    def refs = [r, r]
    for refs | each -> bump(each)
    MRef.get(r)
  }
  println(total)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "8\n"

[<Fact>]
let ``Run - an MRef of heap values gives up the one it held when it is set, and its value when it is dropped`` () =
    let source = """
import nanyx/mref

@entry
def main = {
  def result = memory {
    def names = MRef.new(["a", "b"])
    MRef.set(names, ["c"])
    MRef.update(names, { ns -> list.map(ns, { n -> "{n}!" }) })
    def kept = MRef.get(names)
    MRef.set(names, [])
    def nested = MRef.new(MRef.new("inner"))
    MRef.set(MRef.get(nested), "changed")
    (kept, MRef.get(MRef.get(nested)))
  }
  println(result)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "([c!], changed)\n"

[<Fact>]
let ``Run - an MRef made in a function, captured by a lambda and returned, is shared`` () =
    let source = """
import nanyx/mref

def incremented: [Memory] MRef(int) -> int = { count ->
  MRef.update(count, { n -> n + 1 })
  MRef.get(count)
}

def counter: [Memory] () -> ([Memory] () -> int) = {
  def count = MRef.new(0)
  { () -> incremented(count) }
}

@entry
def main = {
  def last = memory {
    def next = counter()
    next()
    next()
    next()
  }
  println(last)
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3\n"
