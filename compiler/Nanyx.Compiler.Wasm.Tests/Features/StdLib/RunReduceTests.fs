module TranspilerWasmTests.Features.StdLib.RunReduce

open Xunit
open TranspilerWasmTestHelpers

// `reduce` combines a collection's elements starting from the first, so an empty one gives `#none` (#2080).

/// For each collection, `main`'s lines: a product, an empty one's `#none`, and strings joined.
let private reduced (imports: string) (lines: string) =
    $"""{imports}
@entry
def main = {{
{lines}
}}
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Run - list reduce starts from the first element, and an empty list gives none`` () =
    let source =
        reduced
            ""
            """  println([1, 2, 3, 4, 5] \list.reduce { * })
  def none: list(int) = []
  println(none \list.reduce { * })
  println([7] \list.reduce { * })
  println([10, 3, 2] \list.reduce { - })"""
    runSource source |> assertRan "#some(120)\n#none\n#some(7)\n#some(5)\n"

[<Fact>]
let ``Run - list reduce works on a type with no Default`` () =
    let source =
        reduced
            "type Point = (x: int, y: int)\n"
            """  def points = [Point(x = 1, y = 2), Point(x = 3, y = 4)]
  match points \list.reduce { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }
    | #some(total) -> println("{total.x}, {total.y}")
    | #none -> println("none")"""
    runSource source |> assertRan "4, 6\n"

[<Fact>]
let ``Run - set, map and seq reduce from the first element`` () =
    let source =
        reduced
            ""
            """  println(set.fromList([1, 2, 3, 4, 5]) \set.reduce { * })
  println(set.fromList(["a", "b"]) \set.reduce { ++ })
  def noElements: set(int) = set.fromList([])
  println(noElements \set.reduce { * })
  def noKeys: map(string, int) = []
  println(["a" => 4, "b" => 5] \map.reduce { * })
  println(noKeys \map.reduce { * })
  println(["x" => "a", "y" => "b"] \map.reduce { ++ })
  println((1..5) \seq.reduce { * })
  println((1..0) \seq.reduce { * })
  println(list.toSeq(["a", "b"]) \seq.reduce { ++ })"""
    runSource source
    |> assertRan "#some(120)\n#some(ba)\n#none\n#some(20)\n#none\n#some(ab)\n#some(120)\n#none\n#some(ab)\n"

[<Fact>]
let ``Run - array, slice, stack and queue reduce from the first element`` () =
    let source =
        reduced
            "import nanyx/array\nimport nanyx/slice\nimport nanyx/stack\nimport nanyx/queue\n"
            """  def xs = array.fromList([1, 2, 3, 4, 5])
  println(xs \array.reduce { * })
  def noElements: array(int) = array.fromList([])
  println(noElements \array.reduce { * })
  println(array.fromList(["a", "b"]) \array.reduce { ++ })
  println(array.slice(xs, 1, 3) \Slice.reduce { * })
  println(array.slice(xs, 1, 1) \Slice.reduce { * })
  println(Stack.fromList([1, 2, 3, 4, 5]) \Stack.reduce { * })
  def nothing: Stack(int) = Stack.empty
  println(nothing \Stack.reduce { * })
  println(Queue.fromList(["a", "b"]) \Queue.reduce { ++ })
  def empty: Queue(string) = Queue.empty
  println(empty \Queue.reduce { ++ })"""
    runSource source
    |> assertRan "#some(120)\n#none\n#some(ab)\n#some(6)\n#none\n#some(120)\n#none\n#some(ab)\n#none\n"

[<Fact>]
let ``Run - MList, MArray and MSlice reduce from the first element`` () =
    let source =
        reduced
            "import nanyx/mlist\nimport nanyx/marray\nimport nanyx/mslice\n"
            """  memory {
    def items = MList.fromList([1, 2, 3, 4, 5])
    println(items \MList.reduce { * })
    def none: MList(int) = MList.empty()
    println(none \MList.reduce { * })
    def xs = MArray.fromList(["a", "b", "c"])
    println(xs \MArray.reduce { ++ })
    println(MArray.fromList([4, 5]) \MArray.reduce { * })
    println(MArray.slice(xs, 1, 3) \MSlice.reduce { ++ })
    println(MArray.slice(xs, 0, 0) \MSlice.reduce { ++ })
  }"""
    runSource source |> assertRan "#some(120)\n#none\n#some(abc)\n#some(20)\n#some(bc)\n#none\n"

[<Fact>]
let ``Run - list product multiplies the elements, and an empty list's product is 1`` () =
    let source =
        reduced
            ""
            """  println([1, 2, 3, 4] \list.product)
  def none: list(int) = []
  println(none \list.product)
  println([1.5, 2.0] \list.product)"""
    runSource source |> assertRan "24\n1\n3.0\n"
