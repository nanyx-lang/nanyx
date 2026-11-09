module TranspilerWasmTests.Features.Lists.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - list literals and patterns`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: list(int) -> string = { xs ->\n" +
        "  match xs\n" +
        "  | [] -> \"empty\"\n" +
        "  | [_x] -> \"one\"\n" +
        "  | [1, _y] -> \"one then another\"\n" +
        "  | [_x, ..._rest] -> \"many\"\n" +
        "}\n" +
        "def sum: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "  | [] -> 0\n" +
        "  | [x, ...rest] -> x + sum(rest)\n" +
        "}\n" +
        "println(describe([]))\n" +
        "println(describe([7]))\n" +
        "println(describe([1, 2]))\n" +
        "println(describe([2, 2, 2]))\n" +
        "dbg(sum([1, 2, 3, 4]))\n"
    runSource source |> assertRan "empty\none\none then another\nmany\ndbg: 10\n"

[<Fact>]
let ``Run - an unnamed rest matches a longer list`` () =
    // `[x, ...]` binds the head and ignores the rest, whatever its length.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def first: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "  | [x, ...] -> x\n" +
        "  | [] -> 0\n" +
        "}\n" +
        "dbg(first([5, 6, 7]))\n" +
        "dbg(first([8]))\n" +
        "dbg(first([]))\n"
    runSource source |> assertRan "dbg: 5\ndbg: 8\ndbg: 0\n"

[<Fact>]
let ``Run - list spread in expressions`` () =
    // Regression: `[1, ...rest]` worked as a pattern but not as an expression (#44).
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def sum: list(int) -> int = {\n" +
        "  | [] -> 0\n" +
        "  | [head, ...tail] -> head + sum(tail)\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def xs = [1, ...[2, 3]]\n" +
        "  println(\"{sum(xs)}\")\n" +
        "  def names = [\"b\"]\n" +
        "  println(String.join([\"a\", ...names, \"c\", ...[], ...[\"d\"]], \"-\"))\n" +
        "}\n"
    runSource source |> assertRan "6\na-b-c-d\n"

[<Fact>]
let ``Run - ++ joins lists`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def front = [\"a\", \"b\"]\n" +
        "println(String.join(front ++ [\"c\"] ++ [], \"-\"))\n" +
        "println(String.join([] ++ front, \"-\"))\n" +
        "dbg(String.length(\"ab\" ++ \"cd\" ++ \"e\"))\n"
    runSource source |> assertRan "a-b-c\na-b\ndbg: 5\n"

[<Fact>]
let ``Run - the prelude's List functions`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: list(int) -> string = {\n" +
        "  | [] -> \"[]\"\n" +
        "  | [y, ...rest] -> \"{y} \" ++ show(rest)\n" +
        "}\n" +
        "def xs = [3, 1, 2]\n" +
        "println(show(xs \\ map({ x -> x * 2 })))\n" +
        "println(show(xs \\ filter({ x -> x > 1 })))\n" +
        "println(show(List.reverse(xs)))\n" +
        "println(show(List.flatten([[1, 2], [3], []])))\n" +
        "println(show(List.take(xs, 2)) ++ \" | \" ++ show(List.skip(xs, 2)) ++ \" | \" ++ show(List.append(xs, 9)))\n" +
        "def total: int = List.fold(xs, 0, { acc, x -> acc + x })\n" +
        "def back: int = List.foldBack([1, 2, 3], 0, { acc, x -> acc - x })\n" +
        "def count: int = List.length(xs)\n" +
        "def pairs: int = List.length(List.zip(xs, [\"a\", \"b\"]))\n" +
        "println(\"{total} {back} {count} {pairs}\")\n" +
        "println(if List.all(xs, { x -> x > 0 }) then \"all\" else \"not all\")\n" +
        "println(if List.any(xs, { x -> x > 5 }) then \"any\" else \"none\")\n" +
        "def found = xs \\ find({ x -> x < 3 })\n" +
        "def empty: list(int) = []\n" +
        "def first = List.head(empty)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match found\n" +
        "    | #some(n) -> println(\"found {n}\")\n" +
        "    | #notFound -> println(\"not found\")\n" +
        "  match first\n" +
        "    | #some(n) -> println(\"head {n}\")\n" +
        "    | #emptyList -> println(\"empty\")\n" +
        "}\n"
    runSource source
    |> assertRan (
        "6 2 4 []\n3 2 []\n2 1 3 []\n1 2 3 []\n3 1 [] | 2 [] | 3 1 2 9 []\n" +
        "6 -6 3 2\nall\nnone\nfound 1\nempty\n")
