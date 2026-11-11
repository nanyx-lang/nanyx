module TranspilerWasmTests.Features.Except.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - except replaces the tag it names and passes the others through`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def parseNumber = { s -> int.fromString(s) except #invalidInt -> #invalidVersionNumber }\n" +
        "\n" +
        "def show = {\n" +
        "  | #some(n) -> \"number {n}\"\n" +
        "  | #invalidVersionNumber -> \"not a version number\"\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(show(parseNumber(\"42\")))\n" +
        "  println(show(parseNumber(\"x\")))\n" +
        "}\n"
    runSource source |> assertRan "number 42\nnot a version number\n"

[<Fact>]
let ``Run - except with several arms, a nested tag and a binding`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "type Loaded = #some(int) | #error(#notFound | #io(string) | #denied)\n" +
        "\n" +
        "def notFound: Loaded = #error(#notFound)\n" +
        "def failed: Loaded = #error(#io(\"disk\"))\n" +
        "def denied: Loaded = #error(#denied)\n" +
        "def loaded: Loaded = #some(7)\n" +
        "\n" +
        "def recover: Loaded -> #some(int) | #error(#denied) = { result ->\n" +
        "  result except\n" +
        "    | #error(#notFound) -> #some(0)\n" +
        "    | #error(#io(message)) -> #some(message \\string.length)\n" +
        "}\n" +
        "\n" +
        "def show: (#some(int) | #error(#denied)) -> string = {\n" +
        "  | #some(n) -> \"{n}\"\n" +
        "  | #error(#denied) -> \"denied\"\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  for [notFound, failed, denied, loaded] | result -> println(show(recover(result)))\n" +
        "}\n"
    runSource source |> assertRan "0\n4\ndenied\n7\n"

[<Fact>]
let ``Run - except leaves a single case for a destructuring def`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def find: (list(int), int) -> #some(int) | #notFound = { xs, target ->\n" +
        "  match xs\n" +
        "    | [] -> #notFound\n" +
        "    | [x, ...rest] -> if x == target then #some(x) else find(rest, target)\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def #some(found) = find([1, 2, 3], 9) except #notFound -> #some(-1)\n" +
        "  println(\"{found}\")\n" +
        "}\n"
    runSource source |> assertRan "-1\n"
