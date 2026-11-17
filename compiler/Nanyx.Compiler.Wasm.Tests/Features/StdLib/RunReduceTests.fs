module TranspilerWasmTests.Features.StdLib.RunReduce

open Xunit
open TranspilerWasmTestHelpers

// `reduce` folds a collection's elements from the element type's `Default`, so an empty one gives the default (#1792).

/// For each collection, `main`'s lines: a sum, an empty one's default, and strings joined.
let private reduced (imports: string) (lines: string) =
    $"""{imports}
@entry
def main = {{
{lines}
}}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Run - list, set, map and seq reduce from the default`` () =
    let source =
        reduced
            ""
            """  println([1, 2, 3, 4] \list.reduce { + })
  def none: list(int) = []
  println(none \list.reduce { + })
  println(["a", "b"] \list.reduce { ++ })
  println(set.fromList([1, 2, 3]) \set.reduce { + })
  println(set.fromList(["a"]) \set.reduce { ++ })
  def noKeys: map(string, int) = []
  println(["a" => 1, "b" => 2] \map.reduce { + })
  println(noKeys \map.reduce { + })
  println(["x" => "a", "y" => "b"] \map.reduce { ++ })
  println((1..4) \seq.reduce { + })
  println((1..0) \seq.reduce { + })
  println(list.toSeq(["a", "b"]) \seq.reduce { ++ })"""
    runSource source |> assertRan "10\n0\nab\n6\na\n3\n0\nab\n10\n0\nab\n"

[<Fact>]
let ``Run - array, slice, stack and queue reduce from the default`` () =
    let source =
        reduced
            "import nanyx/array\nimport nanyx/slice\nimport nanyx/stack\nimport nanyx/queue\n"
            """  def xs = array.fromList([1, 2, 3])
  println(xs \array.reduce { + })
  println(array.fromList(["a", "b"]) \array.reduce { ++ })
  println(array.slice(xs, 1, 3) \Slice.reduce { + })
  println(array.slice(xs, 1, 1) \Slice.reduce { + })
  println(Stack.fromList([1, 2, 3]) \Stack.reduce { + })
  println(Stack.fromList(["a", "b"]) \Stack.reduce { ++ })
  def nothing: Stack(int) = Stack.empty
  println(nothing \Stack.reduce { + })
  println(Queue.fromList([1, 2, 3]) \Queue.reduce { + })
  println(Queue.fromList(["a", "b"]) \Queue.reduce { ++ })
  def empty: Queue(string) = Queue.empty
  println("[{empty \Queue.reduce { ++ }}]")"""
    runSource source |> assertRan "6\nab\n5\n0\n6\nab\n0\n6\nab\n[]\n"

[<Fact>]
let ``Run - MList, MArray and MSlice reduce from the default`` () =
    let source =
        reduced
            "import nanyx/mlist\nimport nanyx/marray\nimport nanyx/mslice\n"
            """  memory {
    def items = MList.fromList([1, 2, 3])
    println(items \MList.reduce { + })
    def none: MList(int) = MList.empty()
    println(none \MList.reduce { + })
    def xs = MArray.fromList(["a", "b", "c"])
    println(xs \MArray.reduce { ++ })
    println(MArray.fromList([4, 5]) \MArray.reduce { + })
    println(MArray.slice(xs, 1, 3) \MSlice.reduce { ++ })
    println("[{MArray.slice(xs, 0, 0) \MSlice.reduce { ++ }}]")
  }"""
    runSource source |> assertRan "6\n0\nabc\n9\nbc\n[]\n"
