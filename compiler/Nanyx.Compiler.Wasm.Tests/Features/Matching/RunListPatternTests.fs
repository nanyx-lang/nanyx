module TranspilerWasmTests.Features.Matching.RunListPattern

open Xunit
open TranspilerWasmTestHelpers

// A list pattern looks only at the cells it needs (#557): `[]` checks that the list is empty, `[a, b]` that it has two
// cells and ends there, and `[a, b, ...rest]` that it has at least two.

/// `describe` tells apart the empty list, lists of one, two and three, and longer lists.
let private describe =
    "import nanyx/console\n" +
    "\n" +
    "def describe: list(int) -> string = {\n" +
    "  | [] -> \"empty\"\n" +
    "  | [a] -> \"one {a}\"\n" +
    "  | [a, b] -> \"two {a} {b}\"\n" +
    "  | [a, b, c] -> \"three {a} {b} {c}\"\n" +
    "  | [a, b, ...rest] -> \"more {a} {b} {list.length(rest)}\"\n" +
    "}\n"

[<Fact>]
let ``Run - an empty list pattern matches only the empty list`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def isEmpty: list(int) -> bool = {\n" +
        "  | [] -> true\n" +
        "  | _ -> false\n" +
        "}\n" +
        "println(\"{isEmpty([])} {isEmpty([1])} {isEmpty([1, 2])}\")\n"
    runSource source |> assertRan "true false false\n"

[<Fact>]
let ``Run - an exact list pattern matches only lists of its length`` () =
    let source =
        describe +
        "println(describe([]))\n" +
        "println(describe([1]))\n" +
        "println(describe([1, 2]))\n" +
        "println(describe([1, 2, 3]))\n" +
        "println(describe([1, 2, 3, 4]))\n"
    runSource source |> assertRan "empty\none 1\ntwo 1 2\nthree 1 2 3\nmore 1 2 2\n"

[<Fact>]
let ``Run - an exact list pattern tests its elements only on a list of its length`` () =
    // `[1, 2]` must not match `[1, 2, 3]`, nor `[1]` (whose second cell does not exist).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def check: list(int) -> string = {\n" +
        "  | [1, 2] -> \"pair\"\n" +
        "  | [1, ..._] -> \"starts with 1\"\n" +
        "  | _ -> \"other\"\n" +
        "}\n" +
        "println(check([1, 2]))\n" +
        "println(check([1, 2, 3]))\n" +
        "println(check([1]))\n" +
        "println(check([]))\n" +
        "println(check([2, 2]))\n"
    runSource source |> assertRan "pair\nstarts with 1\nstarts with 1\nother\nother\n"

[<Fact>]
let ``Run - a rest pattern matches lists at least as long as its elements`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def check: list(int) -> string = {\n" +
        "  | [...all] -> \"any {list.length(all)}\"\n" +
        "}\n" +
        "def atLeastTwo: list(int) -> string = {\n" +
        "  | [a, b, ...rest] -> \"{a} {b} then {list.length(rest)}\"\n" +
        "  | _ -> \"short\"\n" +
        "}\n" +
        "println(check([]))\n" +
        "println(check([1, 2]))\n" +
        "println(atLeastTwo([]))\n" +
        "println(atLeastTwo([1]))\n" +
        "println(atLeastTwo([1, 2]))\n" +
        "println(atLeastTwo([1, 2, 3, 4]))\n"
    runSource source |> assertRan "any 0\nany 2\nshort\nshort\n1 2 then 0\n1 2 then 2\n"

[<Fact>]
let ``Run - list.fold walks a list of 1,000,000 elements`` () =
    // Each step matches `[]` and `[x, ...rest]`; when a match counted the whole list this took hours.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def upTo: int, list(int) -> list(int) = { n, acc -> if n == 0 then acc else upTo(n - 1, [n, ...acc]) }\n" +
        "def xs = upTo(1000000, [])\n" +
        "println(\"{list.length(xs)} {list.fold(xs, 0, { total, x -> total + x % 10 })}\")\n"
    runSource source |> assertRan "1000000 4500000\n"
