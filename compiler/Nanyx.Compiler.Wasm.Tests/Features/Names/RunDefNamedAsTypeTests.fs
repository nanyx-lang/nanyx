module TranspilerWasmTests.Features.Names.RunDefNamedAsType

open Xunit
open TranspilerWasmTestHelpers

// #1841: a program's own top-level def named as a built-in type, `map`, leaves the type's attached definitions alone,
// in the program and in the library code it inlines; a local value of that name still reads its own field.

/// What running `source` printed, with calls inlined or not.
let private run (inlineCalls: bool) (source: string) = runSourceWithOptions true inlineCalls "" (source.ReplaceLineEndings "\n")

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``Run - a def named map, list or string leaves the type's attached definitions alone`` (inlineCalls: bool) =
    run inlineCalls """def map: int -> int = { x -> x }
def list: int -> int = { x -> x + 1 }
def string: int -> int = { x -> x + 2 }

def empty: map(string, int) = Map.empty
def none: () -> map(string, int) = { map.empty }

println(list.groupBy([1, 2, 3], { x -> x % 2 }))
println(map(4) + list(4) + string(4))
println((empty \map.length) + map.length(none()))
println(list.length([1, 2]) + string.length("abc"))
"""
    |> assertRan "[1 => [1, 3], 0 => [2]]\n15\n0\n5\n"

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``Run - a local value named as a built-in type is the value`` (inlineCalls: bool) =
    run inlineCalls """def read = { list -> list.length }

println(read((length = 3)))
"""
    |> assertRan "3\n"
