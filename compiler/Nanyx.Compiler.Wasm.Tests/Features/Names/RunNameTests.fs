module TranspilerWasmTests.Features.Names.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - names in backticks are defined, called and read as any other name`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def `add points`: (int, int) -> int = { a, b -> a + b }\n" +
        "def `is it empty?`: string -> bool = { s -> s == \"\" }\n" +
        "def `match` = 7\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def person = (`first name` = \"Ada\", age = 36)\n" +
        "  println(\"{`add points`(1, 2)} {`is it empty?`(\"\")} {`match`} {person.`first name`}\")\n" +
        "}\n"
    runSource source |> assertRan "3 true 7 Ada\n"

[<Fact>]
let ``Run - names that differ only in characters an identifier cannot hold stay apart`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def `add points`: (int, int) -> int = { a, b -> a + b }\n" +
        "def add_points: (int, int) -> int = { a, b -> a * b }\n" +
        "def `<+>`: (int, int) -> int = { a, b -> a - b }\n" +
        "def `<->`: (int, int) -> int = { a, b -> b - a }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{`add points`(2, 5)} {add_points(2, 5)} {`<+>`(2, 5)} {`<->`(2, 5)}\")\n" +
        "}\n"
    runSource source |> assertRan "7 10 -3 3\n"
