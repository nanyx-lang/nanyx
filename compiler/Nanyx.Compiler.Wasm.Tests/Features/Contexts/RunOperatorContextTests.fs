module TranspilerWasmTests.Features.OperatorContexts.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a def that does not write its type adds whatever type it is called with`` () =
    // `def add = { x, y -> x + y }` was `int, int -> int`. It is `[Add(a)] a, a -> a`, and `lerp` `[Num(a)]`.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type $Money = (pence: int)\n" +
        "def $Money.zero: $Money = $Money(pence = 0)\n" +
        "def $Money.`+`: $Money, $Money -> $Money = { p, q -> $Money(pence = p.pence + q.pence) }\n" +
        "def $Money.`-`: $Money, $Money -> $Money = { p, q -> $Money(pence = p.pence - q.pence) }\n" +
        "def $Money.negate: $Money -> $Money = { p -> $Money(pence = 0 - p.pence) }\n" +
        "\n" +
        "def add = { x, y -> x + y }\n" +
        "def neg = { x -> -x }\n" +
        "def lerp = { a, b, t -> a + (b - a) * t }\n" +
        "def sumOfSquares = { xs -> xs \\list.map { x -> x * x } \\list.sum }\n" +
        "def local = { n ->\n" +
        "  def twice = { v -> v + v }\n" +
        "  twice(n) + 1\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{add(1, 2)} {add(1.5, 2.25)} {add($Money(pence = 1), $Money(pence = 2)).pence}\")\n" +
        "  println(\"{neg(4)} {neg(2.5)} {neg($Money(pence = 7)).pence}\")\n" +
        "  println(\"{lerp(0.0, 10.0, 0.5)} {lerp(0, 10, 2)}\")\n" +
        "  println(\"{sumOfSquares([1, 2, 3])} {sumOfSquares([0.5, 1.5])} {local(5)}\")\n" +
        "}\n"
    runSource source |> assertRan "3 3.75 3\n-4 -2.5 -7\n5.0 20\n14 2.5 11\n"

[<Fact>]
let ``Run - an operator required by its member is passed the instance of the prelude's context`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def add: [`+`: a, a -> a] = { + }\n" +
        "def scale: [`+`: a, a -> a + `*`: a, a -> a] a, a -> a = { x, k -> x * k + x }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{add(1, 2)} {add(1.5, 2.25)} {scale(3, 2)} {scale(1.5, 2.0)}\")\n" +
        "}\n"
    runSource source |> assertRan "3 3.75 9 4.5\n"
