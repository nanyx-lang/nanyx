module TranspilerWasmTests.Features.ControlFlow.BoolMatch

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - matching a function value's bool result against true and false picks the right arm`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def same: int, int -> bool = { a, b -> a == b }\n" +
        "\n" +
        "def f: int, int, (int, int -> bool) -> bool = { x, y, cmp ->\n" +
        "  match cmp(x, y)\n" +
        "    | true -> true\n" +
        "    | false -> false\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{f(1, 1, same)}\")\n" +
        "  println(\"{f(1, 2, same)}\")\n" +
        "}\n"
    runSource source |> assertRan "true\nfalse\n"
