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

[<Fact>]
let ``Run - a def that calls itself at an ever larger type is reported`` () =
    let source =
        """def nest: a, int -> int = { x, n -> if n == 0 then 0 else nest([x], n - 1) }

println(nest(1, 3))
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Contains("NYX0301", outcome.StandardError)
