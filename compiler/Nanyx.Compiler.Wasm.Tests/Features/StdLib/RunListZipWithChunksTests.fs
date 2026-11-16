module TranspilerWasmTests.Features.StdLib.RunListZipWithChunks

open TranspilerWasmTestHelpers
open Xunit

let private run (body: string) =
    runSource ($"@entry\ndef main = {{\n{body}}}\n".ReplaceLineEndings "\n")

/// The start of a program whose `xs` is the list 1 to 100,000, to show a function is stack-safe.
let private withLongList (body: string) =
    """def upTo: int, list(int) -> list(int) = { n, acc -> if n == 0 then acc else upTo(n - 1, [n, ...acc]) }
def xs = upTo(100000, [])

"""
    + body

[<Fact>]
let ``Run - list.zipWith applies f in pairs and stops at the end of the shorter list`` () =
    run
        """  println([1, 2, 3] \list.zipWith([10, 20], { x, y -> x + y }))
  println(list.zipWith(["a", "b"], [1, 2, 3], { s, n -> "{s}{n}" }))
  def none: list(int) = []
  println(list.zipWith(none, [1, 2], { x, y -> x * y }))
"""
    |> assertRan "[11, 22]\n[a1, b2]\n[]\n"

[<Fact>]
let ``Run - list.intersperse puts the separator between neighbouring elements`` () =
    run
        """  println([1, 2, 3] \list.intersperse(0))
  println([1] \list.intersperse(0))
  def none: list(int) = []
  println(none \list.intersperse(0))
"""
    |> assertRan "[1, 0, 2, 0, 3]\n[1]\n[]\n"

[<Fact>]
let ``Run - list.chunks cuts a list into lists of size elements, the last shorter`` () =
    run
        """  println([1, 2, 3, 4, 5] \list.chunks(2))
  println([1, 2, 3, 4] \list.chunks(2))
  println([1, 2] \list.chunks(5))
  def none: list(int) = []
  println(none \list.chunks(3))
"""
    |> assertRan "[[1, 2], [3, 4], [5]]\n[[1, 2], [3, 4]]\n[[1, 2]]\n[]\n"

[<Fact>]
let ``Run - list.windows gives every run of size neighbouring elements, and none for a shorter list`` () =
    run
        """  println([1, 2, 3, 4] \list.windows(2))
  println([1, 2, 3] \list.windows(3))
  println([1, 2] \list.windows(3))
  def none: list(int) = []
  println(none \list.windows(1))
"""
    |> assertRan "[[1, 2], [2, 3], [3, 4]]\n[[1, 2, 3]]\n[]\n[]\n"

[<Fact>]
let ``Run - list.chunks and list.windows crash on a size below 1`` () =
    let chunks = run "  println([1, 2] \\list.chunks(0))\n"
    Assert.Equal(1, chunks.ExitCode)
    Assert.Equal("crash: list.chunks: the size must be 1 or more, but is 0\n", chunks.StandardError)
    let windows = run "  println([1, 2] \\list.windows(-1))\n"
    Assert.Equal(1, windows.ExitCode)
    Assert.Equal("crash: list.windows: the size must be 1 or more, but is -1\n", windows.StandardError)

[<Fact>]
let ``Run - list.scan gives the initial value, then each running total`` () =
    run
        """  println([1, 2, 3] \list.scan(0, { acc, x -> acc + x }))
  println(["a", "b"] \list.scan("", { acc, s -> "{acc}{s}" }))
  def none: list(int) = []
  println(none \list.scan(7, { acc, x -> acc + x }))
"""
    |> assertRan "[0, 1, 3, 6]\n[, a, ab]\n[7]\n"

[<Fact>]
let ``Run - the five functions work on 100,000 elements`` () =
    let source =
        withLongList
            """@entry
def main = {
  println(list.length(list.zipWith(xs, xs, { x, y -> x + y })))
  println(list.length(list.intersperse(xs, 0)))
  println(list.length(list.chunks(xs, 3)))
  println(list.length(list.windows(xs, 2)))
  println(list.fold(list.scan(xs, 0, { acc, x -> acc + x }), 0, { _, x -> x }))
}
"""

    runSource source |> assertRan "100000\n199999\n33334\n99999\n5000050000\n"
