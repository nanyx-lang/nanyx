/// Slices, views that share storage instead of copying it: `Slice(a)` over an `Array`, `MSlice(a)` over an `MArray`,
/// and `StringSlice` over a string (#1545). Each run checks memory, so a slice that freed its array too early, or kept
/// it past its last use, fails.
module TranspilerWasmTests.Features.Arrays.Slices

open Xunit
open TranspilerWasmTestHelpers

let private run (source: string) = runSource (source.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - a slice of an array, and a slice of that, view its elements`` () =
    run
        """
import nanyx/slice

def xs = Array[1, 2, 3, 4, 5]
def middle = xs \slice(1, 4)
def inner = Slice.slice(middle, 1, 3)

println(Slice.length(middle))
println("{Slice.toArray(middle)}")
println("{Slice.toArray(inner)}")
println(Slice.fold(middle, 0, { total, x -> total + x }))
println("{Slice.map(inner, { x -> x * 10 })}")
"""
    |> assertRan "3\n[2, 3, 4]\n[3, 4]\n9\n[30, 40]\n"

[<Fact>]
let ``Run - a slice's bounds clamp, and reading past its end is none`` () =
    run
        """
import nanyx/slice

def xs = Array[1, 2, 3]

println(Slice.at(xs \slice(1, 3), 0))
println(Slice.at(xs \slice(1, 3), 2))
println(Slice.at(xs \slice(1, 3), -1))
println("{Slice.toArray(xs \slice(-5, 100))}")
println(Slice.isEmpty(xs \slice(2, 1)))
println(Slice.length(Slice.slice(xs \slice(1, 3), 1, 10)))
"""
    |> assertRan "#some(2)\n#none\n#none\n[1, 2, 3]\ntrue\n1\n"

[<Fact>]
let ``Run - a slice keeps the array it views alive as long as it is used`` () =
    run
        """
import nanyx/slice

def tail: int -> Slice(int) = { n -> Array[n, n + 1, n + 2, n + 3] \slice(2, 4) }

def kept = tail(10)

println("{Slice.toArray(kept)}")
println(Slice.at(kept, 1))
"""
    |> assertRan "[12, 13]\n#some(13)\n"

[<Fact>]
let ``Run - a mutable slice is sorted, set, reversed and filled in place, in the array it views`` () =
    run
        """
import nanyx/marray
import nanyx/mslice

def result = memory {
  def ys = MArray.fromArray(Array[5, 3, 9, 1, 7])
  def front = ys \slice(0, 3)
  MSlice.sort(front)
  def sorted = MArray.toArray(ys)
  ignore(MSlice.set(front, 0, 100))
  MSlice.reverse(MSlice.slice(ys \slice(0, 5), 3, 5))
  def reversed = MArray.toArray(ys)
  MSlice.fill(ys \slice(1, 3), 0)
  (sorted, reversed, MArray.toArray(ys), MSlice.set(front, 3, 1), MSlice.at(front, 1))
}

println("{result}")
"""
    |> assertRan "([3, 5, 9, 1, 7], [100, 5, 9, 7, 1], [100, 0, 0, 7, 1], #outOfRange, #some(0))\n"

[<Fact>]
let ``Run - a mutable slice's toArray is a copy that later changes leave alone`` () =
    run
        """
import nanyx/marray
import nanyx/mslice

def copied = memory {
  def ys = MArray.fromArray(Array[1, 2, 3])
  def view = ys \slice(0, 2)
  def copy = MSlice.toArray(view)
  ignore(MArray.set(ys, 0, 99))
  (copy, MSlice.toArray(view))
}

println("{copied}")
"""
    |> assertRan "([1, 2], [99, 2])\n"

[<Fact>]
let ``Run - a string slice views part of a string without copying it`` () =
    run
        """
import nanyx/text

def line = "let x = 42;"
def rest = line \slice(8, 100)

println(StringSlice.toString(line \slice(4, 5)))
println(StringSlice.toString(rest))
println(StringSlice.length(rest))
println(StringSlice.byteAt(rest, 0))
println(StringSlice.byteAt(rest, 3))
println(StringSlice.startsWith(rest, "42"))
println(StringSlice.toString(StringSlice.slice(rest, 0, 2)))
"""
    |> assertRan "x\n42;\n3\n#some(52)\n#none\ntrue\n42\n"
