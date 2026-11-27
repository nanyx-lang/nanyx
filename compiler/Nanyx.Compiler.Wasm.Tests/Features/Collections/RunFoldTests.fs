/// The relatives of each collection's `fold` (#2944): `foldBack`, from the last element to the first, on `list`, `array`
/// and `Slice`; and `foldWhile`, whose step goes on with `#some(acc)` and stops with anything else, on `list`, `array`, `Slice`, `seq`, `map` and `set`.
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
    else if total + x > 6 then #full(total) else #some(total + x)
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
    |> assertRan "#full(6)\n#full(6)\n#full(6)\n#some(3)\n#some(3)\n#some(1)\n"

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
    |> assertRan "#full(6)\n#some(3)\n"

[<Fact>]
let ``Run - foldWhile gives the step's own stopping tags as its result`` () =
    runSource
        """
def sum = { xs: list(int) ->
  match list.foldWhile(xs, 0, { total, x -> if x < 0 then #negative(x) else if x > 10 then #tooBig(x) else #some(total + x) })
    | #some(total) -> "sum {total}"
    | #negative(x) -> "negative {x}"
    | #tooBig(x) -> "too big {x}"
}

println(sum([1, 2, 3]))
println(sum([1, -2, 30]))
println(sum([1, 20, -3]))
"""
    |> assertRan "sum 6\nnegative -2\ntoo big 20\n"

[<Fact>]
let ``Run - foldWhile ends on an endless sequence`` () =
    runSource
        """
println((1..) \seq.foldWhile(0, { total, n -> if total + n > 1000 then #full(total) else #some(total + n) }))
"""
    |> assertRan "#full(990)\n"

[<Fact>]
let ``Run - foldWhile on a map and a set stops at the first stop and calls the step no more`` () =
    runSource
        """
def m = ["a" => 1, "b" => 2, "c" => 3]
def s = set[1, 2, 3, 4, 5]

-- Stops at the second entry, whichever it is, so the third would crash the step.
def entries = map.foldWhile(m, 0, { seen, _, _ ->
  if seen == 2 then crash("the step was called after the stop") else if seen == 1 then #enough(seen + 1) else #some(seen + 1)
})
def elements = set.foldWhile(s, 0, { seen, _ ->
  if seen == 3 then crash("the step was called after the stop") else if seen == 2 then #enough(seen + 1) else #some(seen + 1)
})
def keysAndValues = map.foldWhile(m, 0, { total, key, value -> #some(total + value + string.length(key)) })

println("{entries} {elements} {keysAndValues}")
println(set.foldWhile(s, 0, { total, x -> #some(total + x) }))
"""
    |> assertRan "#enough(2) #enough(3) #some(9)\n#some(15)\n"
