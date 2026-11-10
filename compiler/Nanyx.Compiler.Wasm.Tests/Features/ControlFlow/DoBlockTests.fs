module TranspilerWasmTests.Features.ControlFlow.DoBlocks

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a do block's defs shadow the ones outside it only inside it, and its value is its last statement`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def x = 1\n" +
        "  do\n" +
        "    def x = 2\n" +
        "    println(\"inner {x}\")\n" +
        "  def y =\n" +
        "    do\n" +
        "      def z = 10\n" +
        "      z + x\n" +
        "  println(\"outer {x} {y}\")\n" +
        "}\n"
    runSource source |> assertRan "inner 2\nouter 1 11\n"
