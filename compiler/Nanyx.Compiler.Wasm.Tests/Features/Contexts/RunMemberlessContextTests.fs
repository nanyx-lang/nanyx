/// A context with no members, a capability such as `context Tx = protected ()`, carries nothing at run time: a `use` of
/// its instance only makes it available, so it compiles to nothing (#3132, #1479).
module TranspilerWasmTests.Features.Contexts.MemberlessContexts

open Xunit
open TranspilerWasmTestHelpers

let private tx = """context Tx = protected ()

def needsTx: [Tx, Console] () -> () = { println("in tx") }
"""

[<Fact>]
let ``Run - a use of a memberless context's instance makes it available`` () =
    let source =
        tx
        + """
def main = {
  use Tx(())
  needsTx()
}

main()
"""
    runSource source |> assertRan "in tx\n"

[<Fact>]
let ``Run - a handler supplies a memberless context to the lambda it runs`` () =
    let source =
        tx
        + """
def withTx: [c] ([Tx, c] () -> a) -> a = { f -> use Tx(()) in f() }

def main = {
  withTx { needsTx() }
  println("done")
}

main()
"""
    runSource source |> assertRan "in tx\ndone\n"
