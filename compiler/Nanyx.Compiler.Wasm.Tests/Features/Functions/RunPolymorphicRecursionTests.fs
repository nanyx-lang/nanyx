/// A def that writes its type calls itself at other types, each call running the specialisation at its own.
module TranspilerWasmTests.Features.Functions.RunPolymorphicRecursion

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a def that writes its type calls itself at other types`` () =
    let source =
        """def describe: a, int -> string = { x, n ->
  if n == 0 then "{n}" else describe("s", n - 1) ++ describe(true, n - 1)
}

println(describe(1.5, 2))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "0000\n"

/// Past the cap on its specialisations, `nest` is called through one with its type argument's values boxed, which
/// calls itself at a list of boxes, so the specialisations end (see `RunSpecialisationCap`).
[<Fact>]
let ``Run - a def that calls itself at an ever larger type runs`` () =
    let source =
        """def nest: a, int -> int = { x, n -> if n == 0 then n else nest([x], n - 1) + 1 }

println(nest(1, 100))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "100\n"
