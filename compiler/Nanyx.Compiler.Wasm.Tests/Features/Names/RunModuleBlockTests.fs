module TranspilerWasmTests.Features.Names.RunModuleBlock

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a module block exports the names a destructuring def in it binds`` () =
    let source =
        """module m =
  def pair = (1, 2)
  export def (a, b) = pair

import m

println(a)
println(b)
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "1\n2\n"
