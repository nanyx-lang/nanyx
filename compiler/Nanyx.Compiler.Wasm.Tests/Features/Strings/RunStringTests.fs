module TranspilerWasmTests.Features.Strings.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an interpolated value of a type only the type checker knows`` () =
    // The backend works a hole's type out from what it sees, and asks the type checker when that is not enough
    // (#193): a generic call's result, a for loop's element, a value bound by a match at the top of a script.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def xs = [1, 2, 3]\n" +
        "def total = List.fold(xs, 0, { acc, x -> acc + x })\n" +
        "println(\"{total} [{List.fold(xs, 1, { acc, x -> acc * x })}]\")\n" +
        "for [\"a\", \"b\"] | s ->\n" +
        "  println(\"[{s}]\")\n" +
        "def found = xs \\find({ x -> x > 1 })\n" +
        "match found\n" +
        "  | #some(n) -> println(\"found {n}\")\n" +
        "  | #notFound -> println(\"none\")\n"
    runSource source |> assertRan "6 [6]\n[a]\n[b]\nfound 2\n"

[<Fact>]
let ``Run - an interpolated string inside a hole leaves the holes around it alone`` () =
    // Each hole is evaluated before the string is written; a string inside a later hole used to overwrite the
    // earlier holes' values (#326), printing them empty or trapping.
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def pad: string, int -> string = { text, width -> String.padStart(text, width, \" \") }\n" +
        "\n" +
        "def money: int -> string = { pence ->\n" +
        "  def pounds = pence / 100\n" +
        "  def rest = pence % 100\n" +
        "  \"£{pounds}.{String.padStart(\"{rest}\", 2, \"0\")}\"\n" +
        "}\n" +
        "\n" +
        "def show: [Console] string, int -> () = { name, qty ->\n" +
        "  println(\"[{pad(name, 6)}][{pad(\"{qty}\", 4)}]\")\n" +
        "  println(\"{pad(\"{name}!\", 7)}|{pad(\"{qty}\", 3)}|{\"<{\"{qty}\"}>\"}\")\n" +
        "  println(\"{money(289)} and {money(1205)}\")\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = { show(\"tea\", 2) }\n"
    runSource source |> assertRan "[   tea][   2]\n   tea!|  2|<2>\n£2.89 and £12.05\n"

[<Fact>]
let ``Run - string concatenation and equality`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def name = \"Nanyx\"\n" +
        "println(\"Hello, \" ++ name ++ \"!\")\n" +
        "println(if name == \"Nanyx\" then \"same\" else \"different\")\n" +
        "println(if name != \"Nanyx\" then \"different\" else \"same\")\n" +
        "println(if \"a\" ++ \"b\" == \"ab\" then \"concat works\" else \"concat broken\")\n"
    runSource source |> assertRan "Hello, Nanyx!\nsame\nsame\nconcat works\n"

[<Fact>]
let ``Run - interpolating variables bound by match patterns`` () =
    // Regression: the backend did not know the type of a pattern-bound name, so `"{n}"` inside a
    // match arm failed with "Unable to determine interpolated value type" (#35).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: #some(int) | #none -> string = { v ->\n" +
        "  match v\n" +
        "    | #some(n) -> \"some {n}\"\n" +
        "    | #none -> \"none\"\n" +
        "}\n" +
        "def greet: #named(string) | #anon -> string = { v ->\n" +
        "  match v\n" +
        "    | #named(name) -> \"hello {name}\"\n" +
        "    | #anon -> \"hello stranger\"\n" +
        "}\n" +
        "def first: list(int) -> string = { xs ->\n" +
        "  match xs\n" +
        "    | [] -> \"empty\"\n" +
        "    | [x, ..._rest] -> \"first {x}\"\n" +
        "}\n" +
        "println(describe(#some(3)) ++ \", \" ++ describe(#none))\n" +
        "println(greet(#named(\"Ada\")) ++ \", \" ++ greet(#anon))\n" +
        "println(first([7, 8]) ++ \", \" ++ first([]))\n"
    runSource source |> assertRan "some 3, none\nhello Ada, hello stranger\nfirst 7, empty\n"

[<Fact>]
let ``Run - interpolation at the top level of a script`` () =
    // Regression: the script start function used to omit the interpolation scratch locals (#48).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def x = 1 + 1\n" +
        "println(\"script {x}\")\n" +
        "println(\"{x * 10} again\")\n"
    runSource source |> assertRan "script 2\n20 again\n"

[<Fact>]
let ``Run - string literals inside interpolation holes`` () =
    // Regression: literals nested in `{...}` were not collected into the data section (#49).
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def main = {\n" +
        "  println(\"len {String.length(\"abc\")} and {String.uppercase(\"x\")}\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "len 3 and X\n"

[<Fact>]
let ``Run - interpolation holes that allocate or have effects run once`` () =
    // Regression: holes used to be evaluated twice, and an allocating hole in the second pass
    // overwrote the output buffer, so `"a {s ++ "!"} c"` printed just `b!`.
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def s = \"b\"\n" +
        "def shout: [Console] string -> string = { v ->\n" +
        "  println(\"shouting \" ++ v)\n" +
        "  String.uppercase(v)\n" +
        "}\n" +
        "println(\"a {s ++ \"!\"} c {String.uppercase(s)} d\")\n" +
        "println(\"[{shout(\"x\")}]\")\n"
    runSource source |> assertRan "a b! c B d\nshouting x\n[X]\n"

[<Fact>]
let ``Run - strings and lists through the core IR`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def greet: string -> string = { name -> \"Hello, \" ++ name }\n" +
        "\n" +
        "def heads: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "    | [] -> 0\n" +
        "    | [a] -> a\n" +
        "    | [a, b, ...rest] -> a + b\n" +
        "}\n" +
        "\n" +
        "def answer = { s ->\n" +
        "  match s\n" +
        "    | \"yes\" -> \"agreed\"\n" +
        "    | _ -> \"declined\"\n" +
        "}\n" +
        "\n" +
        "def show = { n -> \"{n * 1}\" }\n" +
        "\n" +
        "def main = {\n" +
        "  println(greet(\"Ada\"))\n" +
        "  println(show(heads([10, 20] ++ [30])))\n" +
        "  println(show(heads([7])))\n" +
        "  println(answer(\"yes\"))\n" +
        "  println(answer(\"no\"))\n" +
        "  println(show(\"four\".length))\n" +
        "  if \"a\" != \"b\" then println(\"different\") else println(\"same\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "Hello, Ada\n30\n7\nagreed\ndeclined\n4\ndifferent\n"

[<Fact>]
let ``Run - interpolated strings and dbg through the core IR`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def pick = { n -> if n > 0 then #yes else #no }\n" +
        "\n" +
        "def describe: int -> string = { n -> \"n is {n}, positive {n > 0}, pick {pick(n)}\" }\n" +
        "\n" +
        "def nested: string -> string = { s -> \"outer {\"inner {s}\"} end\" }\n" +
        "\n" +
        "def debugs: int -> int = { n ->\n" +
        "  dbg(n)\n" +
        "  dbg(#some(n))\n" +
        "  dbg(pick(n))\n" +
        "  n\n" +
        "}\n" +
        "\n" +
        "def main = {\n" +
        "  println(describe(3))\n" +
        "  println(describe(-1))\n" +
        "  println(nested(\"x\"))\n" +
        "  debugs(2)\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source
    |> assertRan "n is 3, positive true, pick #yes\nn is -1, positive false, pick #no\nouter inner x end\ndbg: 2\ndbg: #some(2)\ndbg: #yes\n"
