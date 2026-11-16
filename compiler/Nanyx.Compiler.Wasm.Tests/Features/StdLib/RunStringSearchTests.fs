module TranspilerWasmTests.Features.StdLib.RunStringSearch

open Xunit
open TranspilerWasmTestHelpers

// Searching a string compares its bytes in place: it makes no string for each position it tries.

[<Fact>]
let ``Run - contains, count, startsWith and endsWith allocate nothing`` () =
    let source = """
def search: int -> int = { n ->
  def s = "  item 1999 costs 5997 pence, item 2999 costs 8997 pence  "
  def found = if string.contains(s, "pence,") then 1 else 0
  def missing = if string.contains(s, "pounds") then 2 else 0
  def starts = if string.startsWith(s, "  item") then 10 else 0
  def ends = if string.endsWith(s, "pence  ") then 100 else 0
  found + missing + starts + ends + 1000 * string.count(s, "99") + n
}
"""
    use program = instantiateWasm (source.ReplaceLineEndings "\n") []
    let allocations, result = allocationsOf program "search" 0
    Assert.Equal(4111, result)
    Assert.Equal(0, allocations)

[<Fact>]
let ``Run - split allocates only its pieces and their cells`` () =
    let source = """
def pieces: int -> int = { n -> list.length(string.split("a,bb,ccc,dddd", on = ",")) + n }
"""
    use program = instantiateWasm (source.ReplaceLineEndings "\n") []
    let allocations, result = allocationsOf program "pieces" 0
    Assert.Equal(4, result)
    Assert.Equal(8, allocations)
