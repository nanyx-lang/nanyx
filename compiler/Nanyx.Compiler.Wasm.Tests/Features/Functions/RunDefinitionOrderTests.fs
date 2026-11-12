module TranspilerWasmTests.Features.Functions.RunDefinitionOrder

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a pipe on the result of a function defined later in the file`` () =
    let source =
        """import nanyx/console
import nanyx/text

@entry
def main = {
  def y = g(5)
  println("{y \contains("x")} {y \contains("s")}")
  println("{isEven(10)} {isOdd(10)}")
}

def g = { x -> "text {x}" }

def isEven = { n -> if n == 0 then true else isOdd(n - 1) }
def isOdd = { n -> if n == 0 then false else isEven(n - 1) }
"""
    runSource source |> assertRan "true false\ntrue false\n"
