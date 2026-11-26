/// The relatives of each collection's `fold` (#2944): `foldBack`, from the last element to the first, on `list`, `array`
/// and `Slice`; and `foldWhile`, whose step says whether to go on, on `list`, `array`, `Slice`, `seq`, `map` and `set`.
module TranspilerWasmTests.Features.Collections.Folds

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - foldBack folds a list, an array and a slice from the last element to the first`` () =
    runSource
        """
import nanyx/array
import nanyx/slice

def join = { acc: string, x: string -> acc ++ x }
def xs = Array["a", "b", "c", "d"]
def none: Array(string) = Array[]

println(list.foldBack(["a", "b", "c"], "", join))
println(array.foldBack(xs, "", join))
println(Slice.foldBack(xs \slice(1, 3), "", join))
println(array.foldBack(none, "empty", join))
"""
    |> assertRan "cba\ndcba\ncb\nempty\n"

// Each step crashes when it is given the element after the one it stopped at, so a fold that called it again would
// crash rather than print.
let private stepUntilPast6 =
    """
def step = { total: int, x: int ->
  if x == 99
    then crash("the step was called after the stop")
    else if total + x > 6 then #stop(total) else #continue(total + x)
}
"""

[<Fact>]
let ``Run - foldWhile on a list, an array and a slice stops at the first stop and calls the step no more`` () =
    runSource (
        stepUntilPast6
        + """
import nanyx/array
import nanyx/slice

def xs = Array[0, 1, 2, 3, 4, 99]

println(list.foldWhile([1, 2, 3, 4, 99], 0, step))
println(array.foldWhile(xs, 0, step))
println(Slice.foldWhile(xs \slice(1, 6), 0, step))
println(list.foldWhile([1, 2], 0, step))
println(array.foldWhile(Array[1, 2], 0, step))
println(Slice.foldWhile(xs \slice(0, 2), 0, step))
"""
    )
    |> assertRan "6\n6\n6\n3\n3\n1\n"

[<Fact>]
let ``Run - foldWhile on a sequence pulls no element after the stop`` () =
    runSource (
        stepUntilPast6
        + """
def pulled = seq.map([1, 2, 3, 4, 99] \list.toSeq, { x -> if x == 99 then crash("pulled after the stop") else x })

println(seq.foldWhile(pulled, 0, step))
println(seq.foldWhile([1, 2] \list.toSeq, 0, step))
"""
    )
    |> assertRan "6\n3\n"

[<Fact>]
let ``Run - foldWhile ends on an endless sequence`` () =
    runSource
        """
println((1..) \seq.foldWhile(0, { total, n -> if total + n > 1000 then #stop(total) else #continue(total + n) }))
"""
    |> assertRan "990\n"

[<Fact>]
let ``Run - foldWhile on a map and a set stops at the first stop and calls the step no more`` () =
    runSource
        """
def m = ["a" => 1, "b" => 2, "c" => 3]
def s = set[1, 2, 3, 4, 5]

-- Stops at the second entry, whichever it is, so the third would crash the step.
def entries = map.foldWhile(m, 0, { seen, _, _ ->
  if seen == 2 then crash("the step was called after the stop") else if seen == 1 then #stop(seen + 1) else #continue(seen + 1)
})
def elements = set.foldWhile(s, 0, { seen, _ ->
  if seen == 3 then crash("the step was called after the stop") else if seen == 2 then #stop(seen + 1) else #continue(seen + 1)
})
def keysAndValues = map.foldWhile(m, 0, { total, key, value -> #continue(total + value + string.length(key)) })

println("{entries} {elements} {keysAndValues}")
println(set.foldWhile(s, 0, { total, x -> #continue(total + x) }))
"""
    |> assertRan "2 3 9\n15\n"
