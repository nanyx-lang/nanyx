module TranspilerWasmTests.Features.ControlFlow.DoBlocks

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a do block's defs shadow the ones outside it only inside it, and its value is its last statement`` () =
    let source =
        normalized """import nanyx/console

@entry
def main = {
  def x = 1
  do
    def x = 2
    println("inner {x}")
  def y =
    do
      def z = 10
      z + x
  println("outer {x} {y}")
}
"""
    runSource source |> assertRan "inner 2\nouter 1 11\n"
