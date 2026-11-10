module TranspilerWasmTests.Features.Strings.RunInterpolation

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a hole of a built-in type writes it as it always has`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def n = -42\n" +
        "  println(\"{n} {0} {1.5} {true} {false} {\"text\"} {#red} {n > 0}\")\n" +
        "  println(\"{Int.toString(7)} {Bool.toString(true)} {Float.toString(2.5)}\")\n" +
        "}\n"
    runSource source |> assertRan "-42 0 1.5 true false text #red false\n7 true 2.5\n"

[<Fact>]
let ``Run - a hole writes a value through its type's attached toString`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.toString = { p -> \"({p.x}, {p.y})\" }\n" +
        "\n" +
        "type $Money = private int\n" +
        "def $Money.toString: $Money -> string = { m ->\n" +
        "  def $Money(pence) = m\n" +
        "  \"£{pence / 100}.{pence % 100}\"\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def p = Point(x = 1, y = 2)\n" +
        "  println(\"p is {p}, costing {$Money(250)}\")\n" +
        "}\n"
    runSource source |> assertRan "p is (1, 2), costing £2.50\n"

[<Fact>]
let ``Run - a use changes how a hole writes its type`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"before {1}\")\n" +
        "  use ToString(int) = ( toString = { n -> \"<{Int.toString(n)}>\" } )\n" +
        "  println(\"after {1}\")\n" +
        "}\n"
    runSource source |> assertRan "before 1\nafter <1>\n"

[<Fact>]
let ``Run - a generic function that interpolates its parameter writes each type it is called with`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.toString = { p -> \"({p.x}, {p.y})\" }\n" +
        "\n" +
        "def angled = { value -> \"<{value}>\" }\n" +
        "def labelled: [ToString(a)] (string, a) -> string = { name, value -> \"{name}: {value}\" }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(angled(1) ++ angled(\"s\") ++ angled(Point(x = 3, y = 4)))\n" +
        "  println(labelled(\"at\", Point(x = 5, y = 6)))\n" +
        "  println([1, 2] \\List.map({ n -> \"#{n}\" }) \\String.join(\" \"))\n" +
        "}\n"
    runSource source |> assertRan "<1><s><(3, 4)>\nat: (5, 6)\n#1 #2\n"

[<Fact>]
let ``Run - a user's own ToString context and the prelude's agree`` () =
    // The program declares a `ToString` of its own, as code written before the prelude had one does.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context ToString(a) = (toString: a -> string)\n" +
        "type Point = (x: int, y: int)\n" +
        "def Point.toString = { p -> \"({p.x}, {p.y})\" }\n" +
        "\n" +
        "def describe: [ToString(a)] = { v -> toString(v) }\n" +
        "def twice = { v -> describe(v) ++ describe(v) }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(twice(Point(x = 1, y = 2)))\n" +
        "  println(\"{Point(x = 3, y = 4)}\")\n" +
        "}\n"
    runSource source |> assertRan "(1, 2)(1, 2)\n(3, 4)\n"
