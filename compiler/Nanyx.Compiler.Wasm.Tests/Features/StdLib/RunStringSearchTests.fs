module TranspilerWasmTests.Features.StdLib.RunStringSearch

open Xunit
open TranspilerWasmTestHelpers

// Searching a string compares its bytes in place: it makes no string for each position it tries. The search itself is
// a runtime helper, `__nanyx_string_index_between` (#2313).

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

[<Fact>]
let ``Run - searching finds a needle at either end, of several bytes, longer than the string, or empty`` () =
    let source =
        normalized """import nanyx/text

@entry
def main = {
  def s = "aé€😀aé"
  println("{string.indexOf(s, "a")} {string.lastIndexOf(s, "a")} {string.indexOf(s, "😀")} {string.indexOf(s, "aé")}")
  println("{string.indexOf(s, "é€😀aé")} {string.indexOf(s, "é€😀aéx")} {string.indexOf("", "a")} {string.indexOf("", "")}")
  println("{string.indexOf(s, "")} {string.count(s, "aé")} {string.count("aaaa", "aa")} {string.contains(s, "€😀")}")
  println("{string.indexOf("aab", "ab")} {string.indexOf("ababc", "abc")} {string.split("x😀y😀", on = "😀")}")
}
"""
    runSource source
    |> assertRan (
        "#some(0) #some(10) #some(6) #some(0)\n#some(1) #notFound #notFound #some(0)\n"
        + "#some(0) 2 2 true\n#some(1) #some(2) [x, y, ]\n"
    )
