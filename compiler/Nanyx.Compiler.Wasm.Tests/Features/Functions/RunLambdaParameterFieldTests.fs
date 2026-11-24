module TranspilerWasmTests.Features.Functions.RunLambdaParameterFields

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - each lambda of a list of named-field functions takes the fields by name`` () =
    let source =
        """def applyAll: [c] list(([c] (x: int, y: int) -> int)) -> list(int) = { fs ->
  list.map(fs, { f -> f(x = 5, y = 2) })
}

def results = { _ -> applyAll([{ x, y -> x + y }, { y, x -> x - y }]) }

println(results())
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "[7, 3]\n"
