/// A float is an `f64` and an `F32` an `f32`, held as an int is, not boxes on the heap (#1876, see "Value
/// representation" in docs/src/content/compiler-internals/memory-management.md).
module TranspilerWasmTests.Features.Floats.FloatValues

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Float arithmetic in a loop allocates nothing`` () =
    let source =
        normalized """def wrap: int -> #some(int) | #none = { n -> #some(n) }

def spin: int, float, float -> float = { n, x, v ->
  if n == 0 then x else spin(n - 1, x + v * 0.001, v - x * 0.001)
}

def run: int -> int = { n -> float.toInt(spin(n, 1.0, 0.0) * 1000.0, #round) }
"""
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 1000
    // `wrap` allocates, so that the module has an allocator to count with.
    Assert.Equal(541, result)
    Assert.Equal(0, allocations)

[<Fact>]
let ``Floats in records, tags, lists and closures keep their values`` () =
    let source =
        """def point = (x = 1.5, y = -2.25)

def scaled: (x: float, y: float), float -> (x: float, y: float) = { p, k -> (x = p.x * k, y = p.y * k) }

def half: float -> #some(float) | #none = { x -> if x > 0.0 then #some(x / 2.0) else #none }

def sum: list(float) -> float = { xs -> xs \list.fold(0.0, { acc, x -> acc + x }) }

def by = 3.0

def times = { x: float -> x * by }

println("{scaled(point, 2.0).x} {scaled(point, 2.0).y}")
match half(5.0)
  | #some(h) -> println("{h}")
  | #none -> println("none")
println("{sum([0.5, 1.25, 2.0] \list.map(times))}")
println("{[1.5, 2.5] == [1.5, 2.5]}")
"""
    runSource source |> assertRan "3.0 -4.5\n2.5\n11.25\ntrue\n"

[<Fact>]
let ``F32 arithmetic in a loop allocates nothing`` () =
    let source =
        normalized """def wrap: int -> #some(int) | #none = { n -> #some(n) }

def spin: int, F32, F32 -> F32 = { n, x, v ->
  if n == 0 then x else spin(n - 1, x + v * 0.001, v - x * 0.001)
}

def run: int -> int = { n -> float.toInt(F32.toFloat(spin(n, 1.0, 0.0) * 1000.0), #round) }
"""
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 1000
    // `wrap` allocates, so that the module has an allocator to count with.
    Assert.Equal(541, result)
    Assert.Equal(0, allocations)
