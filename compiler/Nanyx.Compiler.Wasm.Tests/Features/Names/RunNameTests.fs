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

[<Fact>]
let ``Run - a parameter in an imported module keeps its value where it shares a name with a definition there`` () =
    let files =
        [ "limits.nyx",
          "module limits\n\n" +
          "export def low: int, int -> int = { a, b -> if a <= b then a else b }\n\n" +
          "export def atLeast: (int, low: int) -> int = { n, low -> if n < low then low else n }\n"
          "main.nyx",
          "module main\n\nimport limits as limits\n\n@entry\ndef main = {\n  println(\"{limits.atLeast(1, low = 5)} {limits.low(1, 5)}\")\n}\n" ]
    let outcome, _ = nanyxOnPackage files [ "run"; "{package}" ]
    assertRan "5 1\n" outcome
