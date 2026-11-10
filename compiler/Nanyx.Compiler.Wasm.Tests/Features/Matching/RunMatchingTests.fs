module TranspilerWasmTests.Features.Matching.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - matching on string literals`` () =
    // Regression: `| "Sat" -> ...` failed with "Unsupported match pattern" (#32).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def kind: string -> string = { day ->\n" +
        "  match day\n" +
        "    | \"Sat\" -> \"weekend\"\n" +
        "    | \"Sun\" -> \"weekend\"\n" +
        "    | _ -> \"weekday\"\n" +
        "}\n" +
        "def greet: #name(string) | #anon -> string = { who ->\n" +
        "  match who\n" +
        "    | #name(\"Ada\") -> \"hello, Ada!\"\n" +
        "    | #name(_) -> \"hello, someone\"\n" +
        "    | #anon -> \"hello, stranger\"\n" +
        "}\n" +
        "def suffix = \"t\"\n" +
        "println(kind(\"Sat\") ++ \" \" ++ kind(\"Sun\") ++ \" \" ++ kind(\"Mon\"))\n" +
        "println(kind(\"Sa\" ++ suffix))\n" +
        "println(greet(#name(\"Ada\")) ++ \" / \" ++ greet(#name(\"Bob\")) ++ \" / \" ++ greet(#anon))\n"
    runSource source |> assertRan "weekend weekend weekday\nweekend\nhello, Ada! / hello, someone / hello, stranger\n"

[<Fact>]
let ``Run - tuple patterns in match arms`` () =
    // Regression: `| (0, _) -> ...` failed with "Unsupported match pattern" (#33).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: (int, int) -> string = { p ->\n" +
        "  match p\n" +
        "    | (0, _) -> \"x zero\"\n" +
        "    | (_, 0) -> \"y zero\"\n" +
        "    | _ -> \"other\"\n" +
        "}\n" +
        "def swapped: (int, int) -> string = { p ->\n" +
        "  match p\n" +
        "    | (a, b) -> \"{b} {a}\"\n" +
        "}\n" +
        "def firstName: list((string, int)) -> string = { people ->\n" +
        "  match people\n" +
        "    | [(name, _), ..._rest] -> name\n" +
        "    | [] -> \"nobody\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(describe((0, 1)) ++ \" \" ++ describe((1, 0)) ++ \" \" ++ describe((1, 1)))\n" +
        "  println(swapped((1, 2)))\n" +
        "  println(firstName([(\"Ada\", 36), (\"Grace\", 45)]) ++ \" \" ++ firstName([]))\n" +
        "}\n"
    runSource source |> assertRan "x zero y zero other\n2 1\nAda nobody\n"

[<Fact>]
let ``Run - a name alone in a pattern binds the record field of that name`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def a: #some(x: string, y: string) = #some(x = \"left\", y = \"right\")\n" +
        "def g: typeof a -> string = { #some(y, x) -> \"x = {x}, y = {y}\" }\n" +
        "def describe: (name: string, age: int) -> string = { r -> match r\n" +
        "  | (age, name) -> \"{name} is {age}\" }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(g(a))\n" +
        "  println(describe((name = \"Ada\", age = 36)))\n" +
        "}\n"
    runSource source |> assertRan "x = left, y = right\nAda is 36\n"

[<Fact>]
let ``Run - identifier patterns bind the scrutinee`` () =
    // Regression: `| n -> ...` and `| 0, y -> ...` failed with "Unsupported identifier" (#34).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def echo: int -> int = { v ->\n" +
        "  match v\n" +
        "    | 0 -> 100\n" +
        "    | n -> n + 1\n" +
        "}\n" +
        "def pick: (int, int) -> int = { a, b ->\n" +
        "  match a, b\n" +
        "    | 0, y -> y\n" +
        "    | x, _ -> x\n" +
        "}\n" +
        "def label: string -> string = { s ->\n" +
        "  match s\n" +
        "    | text -> \"<\" ++ text ++ \">\"\n" +
        "}\n" +
        "dbg(echo(0))\n" +
        "dbg(echo(41))\n" +
        "dbg(pick(0, 5))\n" +
        "dbg(pick(7, 5))\n" +
        "println(label(\"x\"))\n"
    runSource source |> assertRan "dbg: 100\ndbg: 42\ndbg: 5\ndbg: 7\n<x>\n"

[<Fact>]
let ``Run - if-matching`` () =
    // Regression: `if get(3) is #some(v) then ...` was a parse error (#42).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def get: int -> #some(int) | #none = { n -> #some(n) }\n" +
        "def nothing: int -> #some(int) | #none = { _n -> #none }\n" +
        "def describe: #some(int) | #none -> string = { r ->\n" +
        "  if r is #some(v) then \"got {v}\" else \"nothing\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  if get(3) is #some(v) then println(\"got {v}\")\n" +
        "  if nothing(0) is #some(v) then println(\"unexpected {v}\")\n" +
        "  println(describe(get(5)) ++ \" \" ++ describe(nothing(5)))\n" +
        "}\n"
    runSource source |> assertRan "got 3\ngot 5 nothing\n"

[<Fact>]
let ``Run - multi-branch if`` () =
    // #76: `if | cond -> ... | value is pattern -> ... | else -> ...` was a parse error at `|`.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def lookup: int -> #some(int) | #none = { n -> if n > 100 then #none else #some(n * 2) }\n" +
        "def classify: int -> string = { n ->\n" +
        "  if | n < 0 -> \"negative\"\n" +
        "     | n == 0 ->\n" +
        "       \"zero\"\n" +
        "     | lookup(n) is #some(d) -> \"doubled {d}\"\n" +
        "     | else -> \"too big\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(classify(0 - 4))\n" +
        "  println(classify(0))\n" +
        "  println(classify(21))\n" +
        "  println(classify(500))\n" +
        "  if | classify(1) == \"zero\" -> println(\"unexpected\")\n" +
        "     | classify(0) == \"zero\" -> println(\"no else\")\n" +
        "}\n"
    runSource source |> assertRan "negative\nzero\ndoubled 42\ntoo big\nno else\n"

[<Fact>]
let ``Run - guard patterns in the documented form`` () =
    // Regression: the docs write guards as `| { < 0 } -> ...`; the parser only knew `| < 0 ->` (#46).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: int -> string = { n ->\n" +
        "  match n\n" +
        "    | { < 0 } -> \"negative\"\n" +
        "    | 0 -> \"zero\"\n" +
        "    | _ -> \"positive\"\n" +
        "}\n" +
        "def limit = 10\n" +
        "def size: int -> string = { n ->\n" +
        "  match n\n" +
        "    | { >= limit * 2 } -> \"huge\"\n" +
        "    | { >= limit } -> \"big\"\n" +
        "    | else -> \"small\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(describe(-1) ++ \" \" ++ describe(0) ++ \" \" ++ describe(5))\n" +
        "  println(size(25) ++ \" \" ++ size(10) ++ \" \" ++ size(3))\n" +
        "}\n"
    runSource source |> assertRan "negative zero positive\nhuge big small\n"

[<Fact>]
let ``Run - any predicate lambda as a guard pattern`` () =
    // #428: a guard is any predicate on the value matched, not only one comparison against it.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def parity: int -> string = { n ->\n" +
        "  match n\n" +
        "    | { % 2 == 0 } -> \"even\"\n" +
        "    | _ -> \"odd\"\n" +
        "}\n" +
        "def size: string -> string = { s ->\n" +
        "  match s\n" +
        "    | { .length > 5 } -> \"long\"\n" +
        "    | { x -> x == \"\" } -> \"empty\"\n" +
        "    | _ -> \"short\"\n" +
        "}\n" +
        "def nested: (int, #some(string) | #none) -> string = { p ->\n" +
        "  match p\n" +
        "    | ({ < 0 }, _) -> \"negative\"\n" +
        "    | (n, #some({ .length > 2 })) -> \"long at {n}\"\n" +
        "    | _ -> \"other\"\n" +
        "}\n" +
        "def busy: list(int), int -> string = { xs, threshold ->\n" +
        "  match xs\n" +
        "    | { ys -> (ys \\filter { > threshold }).length > 1 } -> \"busy\"\n" +
        "    | [] -> \"empty\"\n" +
        "    | _ -> \"quiet\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(parity(4) ++ \" \" ++ parity(7) ++ \" \" ++ parity(-2))\n" +
        "  println(size(\"marvellous\") ++ \" \" ++ size(\"\") ++ \" \" ++ size(\"hi\"))\n" +
        "  println(nested((-1, #none)) ++ \" \" ++ nested((2, #some(\"abc\"))) ++ \" \" ++ nested((5, #some(\"ab\"))))\n" +
        "  println(busy([1, 9, 9], 5) ++ \" \" ++ busy([1, 9], 5) ++ \" \" ++ busy([], 5))\n" +
        "}\n"
    runSource source |> assertRan "even odd even\nlong empty short\nnegative long at 2 other\nbusy quiet empty\n"

[<Fact>]
let ``Run - or-patterns in match arms`` () =
    // #43: `| a | b -> ...` is one arm matching either alternative.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Day = #mon | #tue | #wed | #thu | #fri | #sat | #sun\n" +
        "def kind: Day -> string = { day ->\n" +
        "  match day\n" +
        "    | #sat | #sun -> \"weekend\"\n" +
        "    | _ -> \"weekday\"\n" +
        "}\n" +
        "def size: int -> string = { n ->\n" +
        "  match n\n" +
        "    | 0\n" +
        "    | 1\n" +
        "    | 2 -> \"small\"\n" +
        "    | _ -> \"big\"\n" +
        "}\n" +
        "def axis: (int, int) -> string = {\n" +
        "  | 0, _ | _, 0 -> \"axis\"\n" +
        "  | _, _ -> \"plane\"\n" +
        "}\n" +
        "println(kind(#sat) ++ \" \" ++ kind(#sun) ++ \" \" ++ kind(#wed))\n" +
        "println(size(1) ++ \" \" ++ size(2) ++ \" \" ++ size(9))\n" +
        "println(axis(0, 5) ++ \" \" ++ axis(5, 0) ++ \" \" ++ axis(3, 3))\n"
    runSource source |> assertRan "weekend weekend weekday\nsmall small big\naxis axis plane\n"

[<Fact>]
let ``Run - a lone else in a match on several values`` () =
    // A lone `else` used to crash type checking in a match on several values.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: int, int -> string = { x, y ->\n" +
        "  match x, y\n" +
        "    | 0, 0 -> \"origin\"\n" +
        "    | 0, _ -> \"on the y axis\"\n" +
        "    | else -> \"elsewhere\"\n" +
        "}\n" +
        "println(describe(0, 0) ++ \", \" ++ describe(0, 5) ++ \", \" ++ describe(3, 4))\n"
    runSource source |> assertRan "origin, on the y axis, elsewhere\n"

[<Fact>]
let ``Run - several values and a tuple of them are matched alike, brackets or not`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "match (1, 2)\n" +
        "  | (x, y) -> println(\"{x}, {y}\")\n" +
        "match 1, 2\n" +
        "  | x, y -> println(\"{x}, {y}\")\n" +
        "match (1, 2)\n" +
        "  | 0, _ -> println(\"zero\")\n" +
        "  | x, y -> println(\"{x}, {y}\")\n" +
        "match 1, 2\n" +
        "  | (0, _) -> println(\"zero\")\n" +
        "  | (x, y) -> println(\"{x}, {y}\")\n"
    runSource source |> assertRan "1, 2\n1, 2\n1, 2\n1, 2\n"

[<Fact>]
let ``Run - an arm for a tag the value cannot have never matches`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def simple = #nil\n" +
        "\n" +
        "def check = match simple\n" +
        "  | #nil -> \"empty\"\n" +
        "  | #some x -> \"has value\"\n" +
        "  | else -> \"other\"\n" +
        "\n" +
        "println(check)\n"
    let outcome = runSource source
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("empty\n", outcome.StandardOutput)