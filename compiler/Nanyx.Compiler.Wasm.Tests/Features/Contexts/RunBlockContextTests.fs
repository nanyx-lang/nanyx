/// A context a `module name =` block declares, used in the module that imports the block.
module TranspilerWasmTests.Features.Contexts.BlockContexts

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a def that writes a module block's context calls its member`` () =
    let source =
        """module db =
  export context Tx = (commit: () -> ())

import db

def f: [Tx] () -> () = { commit() }
def g: [Console] () -> () = { use Tx(commit = { println("committed") }) in f() }
g()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "committed\n"
