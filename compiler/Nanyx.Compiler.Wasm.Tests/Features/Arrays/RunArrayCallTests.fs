/// Calling an array, `xs(i)`, is `array.at(xs, i)` (#1516).
module TranspilerWasmTests.Features.Arrays.Calls

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - calling an array gives the element at an index in range`` () =
    let source =
        """
import nanyx/array

type Shelf = (books: array(string))

def xs = array.fromList([10, 20, 30])
def shelf = Shelf(books = array.fromList(["a", "b"]))

println(xs(0))
println(xs(2))
println(shelf.books(1))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(10)\n#some(30)\n#some(b)\n"

[<Fact>]
let ``Run - calling an array past its end or before its start is none, never a trap`` () =
    let source =
        """
import nanyx/array

def xs = array.fromList([10, 20, 30])

println(xs(3))
println(xs(-1))
println(xs(5) ?? 0)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#none\n#none\n0\n"

[<Fact>]
let ``Run - an array is callable in a module that doesn't import nanyx/array`` () =
    let source =
        """
module reader =
  export def second: array(int) -> #some(int) | #none = { ys -> ys(1) }

import reader
import nanyx/array

println(second(array.fromList([4, 5])))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#some(5)\n"
