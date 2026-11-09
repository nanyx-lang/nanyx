module TranspilerWasmTests.Features.Matching.RunAndPatterns

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - the documented guard with an and-pattern`` () =
    // The pattern-matching page's "Guards" example (#427).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describeNumber: int -> string = { n ->\n" +
        "  match n\n" +
        "    | { < 0 } -> \"negative\"\n" +
        "    | 0 -> \"zero\"\n" +
        "    | n & { > 100 } -> \"{n}, which is large!\"\n" +
        "    | _ -> \"positive\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(describeNumber(-3))\n" +
        "  println(describeNumber(0))\n" +
        "  println(describeNumber(500))\n" +
        "  println(describeNumber(7))\n" +
        "}\n"
    runSource source |> assertRan "negative\nzero\n500, which is large!\npositive\n"

[<Fact>]
let ``Run - the documented and-patterns, the name after the guard`` () =
    // The pattern-matching page's "And patterns" example (#427).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: int -> string = { value ->\n" +
        "  match value\n" +
        "    | { < 0 } & n -> \"Negative: {n}\"\n" +
        "    | { > 100 } & n -> \"Large: {n}\"\n" +
        "    | n -> \"Something else: {n}\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(show(-5))\n" +
        "  println(show(200))\n" +
        "  println(show(42))\n" +
        "}\n"
    runSource source |> assertRan "Negative: -5\nLarge: 200\nSomething else: 42\n"

[<Fact>]
let ``Run - and-patterns nested in a tag and a tuple`` () =
    // Inside another pattern, every part matches the same part of the value, and binds what it binds.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def size: #some(int) | #none -> string = { value ->\n" +
        "  match value\n" +
        "    | #some(n & { > 10 }) -> \"big {n}\"\n" +
        "    | #some(n) -> \"small {n}\"\n" +
        "    | #none -> \"nothing\"\n" +
        "}\n" +
        "def pair: (int, int) -> string = { p ->\n" +
        "  match p\n" +
        "    | (a, b) & (0, _) & whole -> \"zero first, then {b}, {a} and {whole.1}\"\n" +
        "    | (a & 1, b) -> \"one first, then {b} ({a})\"\n" +
        "    | _ -> \"other\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(size(#some(20)))\n" +
        "  println(size(#some(3)))\n" +
        "  println(size(#none))\n" +
        "  println(pair((0, 9)))\n" +
        "  println(pair((1, 8)))\n" +
        "  println(pair((2, 7)))\n" +
        "}\n"
    runSource source
    |> assertRan "big 20\nsmall 3\nnothing\nzero first, then 9, 0 and 9\none first, then 8 (1)\nother\n"

[<Fact>]
let ``Run - an and-pattern tests its parts left to right`` () =
    // `#some(_) & { ... }`: the predicate runs only once the tag matches, so it can take the payload apart.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def payload: #some(int) | #none -> int = { value ->\n" +
        "  match value\n" +
        "    | #some(n) -> n\n" +
        "    | #none -> 0\n" +
        "}\n" +
        "def check: #some(int) | #none -> string = { value ->\n" +
        "  match value\n" +
        "    | #some(_) & { v -> payload(v) > 5 } -> \"more than five\"\n" +
        "    | _ -> \"not\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(check(#some(6)))\n" +
        "  println(check(#some(1)))\n" +
        "  println(check(#none))\n" +
        "}\n"
    runSource source |> assertRan "more than five\nnot\nnot\n"
