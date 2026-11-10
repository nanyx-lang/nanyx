module TranspilerWasmTests.Features.Matching.RunNestedTagPatterns

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - arms that name one tag with different tags in its payload`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type ParseError = #helpRequested | #missingArgument(string)\n" +
        "\n" +
        "def describe: #some(int) | #error(ParseError) -> string = { parsed ->\n" +
        "  match parsed\n" +
        "    | #some(n) -> \"ok {n}\"\n" +
        "    | #error(#helpRequested) -> \"help\"\n" +
        "    | #error(#missingArgument(name)) -> \"missing {name}\"\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(describe(#some(3)))\n" +
        "  println(describe(#error(#helpRequested)))\n" +
        "  println(describe(#error(#missingArgument(\"--input\"))))\n" +
        "}\n"
    runSource source |> assertRan "ok 3\nhelp\nmissing --input\n"
