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

[<Fact>]
let ``Run - a block's definition is named by its short name, or an import's alias, in a file that declares a module`` () =
    let source =
        """module main

module coins =
  export def penny = 1

import coins as c

@entry
def main: [Console] () -> () = {
  println(coins.penny + c.penny + main.coins.penny)
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "3\n"
