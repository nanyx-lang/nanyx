/// A `memory` block in a module compiled without debug probes, as `nanyx run`, `nanyx wasm` and the playground compile
/// one (#2906).
/// Without probes, which read a block's result where it ends, the result is a temporary the block's end reads once:
/// `region_exit` has to compute it there whatever it holds, and a block whose region is left out
/// (`CoreIrUnusedRegions`) still has to use it.
module TranspilerWasmTests.Features.Memory.UnprobedMemoryBlock

open Xunit
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// What `source` prints, compiled with the default options, which have no probes.
let private printed (source: string) =
    match runHost [ "test" ] (transpileWatWith WasmOptions.Default (normalized source)) with
    | Ok output -> output.ReplaceLineEndings "\n"
    | Error error -> failwith $"It didn't run: %A{error}"

[<Fact>]
let ``A block that needs no region, at the top level or in a function, runs`` () =
    Assert.Equal(
        "a\nb\n",
        printed
            """def f: [Console] () -> () = { _ ->
  memory {
    println("b")
  }
}

memory {
  println("a")
}

f()
"""
    )

[<Fact>]
let ``A block with a region whose result is unit runs, as random.seeded's does`` () =
    Assert.Equal(
        "x\n1\n",
        printed
            """import nanyx/random as random

random.seeded(7, { println("x") })

def counted = memory {
  mut n = 0
  n := n + 1
  n
}

println(counted)
"""
    )

[<Fact>]
let ``A block whose result is promoted out of it runs`` () =
    Assert.Equal(
        "ab\n",
        printed
            """def joined = memory {
  mut parts = ["a"]
  parts := [...parts, "b"]
  parts \string.join("")
}

println(joined)
"""
    )
