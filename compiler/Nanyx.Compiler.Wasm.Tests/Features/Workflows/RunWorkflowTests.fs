module TranspilerWasmTests.Features.Workflows.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a builder block that uses no workflow keyword runs as its block`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def total = proc {\n" +
        "  def a = 20\n" +
        "  a + 22\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  proc {\n" +
        "    def names = [\"Ada\", \"Grace\"]\n" +
        "    for names | name -> println(\"Hello, \" ++ name)\n" +
        "  }\n" +
        "  def three = proc { 1 + 2 }\n" +
        "  println(\"{total} {three}\")\n" +
        "}\n"
    runSource source |> assertRan "Hello, Ada\nHello, Grace\n42 3\n"

[<Fact>]
let ``Run - workflow keywords in the branches of an if or match end or continue the whole block`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
        "\n" +
        "def check: int -> #some(int) | #negative | #odd = { n -> proc {\n" +
        "  if n < 0 then try #negative\n" +
        "  if n == 0 then return 0\n" +
        "  def half = try halve(n)\n" +
        "  return half\n" +
        "} }\n" +
        "\n" +
        "def pick: [Console] string, int -> #some(string) | #odd | #unknown = { which, n -> proc {\n" +
        "  match which\n" +
        "    | \"half\" ->\n" +
        "      def h = try halve(n)\n" +
        "      if h > 2 then println(\"big half {h}\") else try halve(h)\n" +
        "    | \"same\" -> println(\"kept {n}\")\n" +
        "    | _ -> try #unknown\n" +
        "  return \"done with {which}\"\n" +
        "} }\n" +
        "\n" +
        "def showCheck: #some(int) | #negative | #odd -> string = {\n" +
        "  | #some(v) -> \"some({v})\"\n" +
        "  | #negative -> \"negative\"\n" +
        "  | #odd -> \"odd\"\n" +
        "}\n" +
        "\n" +
        "def showPick: #some(string) | #odd | #unknown -> string = {\n" +
        "  | #some(v) -> \"some({v})\"\n" +
        "  | #odd -> \"odd\"\n" +
        "  | #unknown -> \"unknown\"\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(showCheck(check(10)))\n" +
        "  println(showCheck(check(-4)))\n" +
        "  println(showCheck(check(0)))\n" +
        "  println(showCheck(check(7)))\n" +
        "  println(showPick(pick(\"half\", 12)))\n" +
        "  println(showPick(pick(\"half\", 4)))\n" +
        "  println(showPick(pick(\"half\", 2)))\n" +
        "  println(showPick(pick(\"same\", 9)))\n" +
        "  println(showPick(pick(\"other\", 9)))\n" +
        "}\n"
    runSource source
    |> assertRan (
        "some(5)\nnegative\nsome(0)\nodd\n" +
        "big half 6\nsome(done with half)\nsome(done with half)\nodd\nkept 9\nsome(done with same)\nunknown\n")

[<Fact>]
let ``Run - a proc block continues past each #some and stops at the first #error`` () =
    // `run(7)` fails at its first `try`, so nothing after it runs; `run(3)` fails at its second. `sum` binds two
    // `try`s nested in its `return`.
    let source =
        "import nanyx/console\n" +
        "import nanyx/result\n" +
        "\n" +
        "def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }\n" +
        "def g: int -> #some(string) | #error(#tooBig) = { n -> if n == 3 then #error(#tooBig) else #some(\"n = {n}\") }\n" +
        "\n" +
        "def run: int -> #some(string) | #error(#tooBig) = { x -> proc {\n" +
        "  def a = try f(x)\n" +
        "  dbg(a)\n" +
        "  def s = try g(a)\n" +
        "  return s\n" +
        "} }\n" +
        "\n" +
        "def add: (int, int) -> int = { a, b -> a + b }\n" +
        "def sum: int -> #some(int) | #error(#tooBig) = { x -> proc {\n" +
        "  return add(try f(x), try f(x + 1))\n" +
        "} }\n" +
        "\n" +
        "def show: [Console] (#some(string) | #error(#tooBig)) -> () = {\n" +
        "  | #some(s) -> println(s)\n" +
        "  | #error(_) -> println(\"error\")\n" +
        "}\n" +
        "\n" +
        "def showInt: [Console] (#some(int) | #error(#tooBig)) -> () = {\n" +
        "  | #some(n) -> println(\"sum = {n}\")\n" +
        "  | #error(_) -> println(\"error\")\n" +
        "}\n" +
        "\n" +
        "show(run(1))\n" +
        "show(run(3))\n" +
        "show(run(7))\n" +
        "showInt(sum(1))\n" +
        "showInt(sum(5))\n"
    runSource source |> assertRan "dbg: 1\nn = 1\ndbg: 3\nerror\nerror\nsum = 3\nerror\n"

[<Fact>]
let ``Run - a proc block gives back whichever failure stopped it`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/result\n" +
        "\n" +
        "def name: int -> #some(string) | #nil = { n -> if n > 5 then #nil else #some(\"n{n}\") }\n" +
        "def find: string -> #some(int) | #notFound = { s -> if s == \"n3\" then #notFound else #some(7) }\n" +
        "\n" +
        "def run = { x -> proc {\n" +
        "  def s = try name(x)\n" +
        "  def i = try find(s)\n" +
        "  return i\n" +
        "} }\n" +
        "\n" +
        "def show: [Console] (#some(int) | #nil | #notFound) -> () = {\n" +
        "  | #some(_) -> println(\"found\")\n" +
        "  | #nil -> println(\"nil\")\n" +
        "  | #notFound -> println(\"not found\")\n" +
        "}\n" +
        "\n" +
        "show(run(1))\n" +
        "show(run(3))\n" +
        "show(run(7))\n"
    runSource source |> assertRan "found\nnot found\nnil\n"

[<Fact>]
let ``Run - the prelude's proc, Result and Option helpers need no import`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def half: int -> Result(int, string) = { n -> if n % 2 == 0 then #some(n / 2) else #error(\"odd\") }\n" +
        "def quarter = { n -> proc {\n" +
        "  def h = try half(n)\n" +
        "  return try half(h)\n" +
        "} }\n" +
        "\n" +
        "def show: [Console] Result(int, string) -> () = {\n" +
        "  | #some(n) -> println(\"some {n}\")\n" +
        "  | #error(e) -> println(\"error {e}\")\n" +
        "}\n" +
        "\n" +
        "show(quarter(8))\n" +
        "show(quarter(6) \\ Result.mapError({ e -> \"ERR: \" ++ e }))\n" +
        "show(half(4) \\ map({ n -> n * 10 }))\n" +
        "def fallback = Result.defaultValue(half(3), 0)\n" +
        "println(\"{fallback}\")\n" +
        "def name: #some(string) | #empty = #some(\"abc\")\n" +
        "def shouted = name \\ Option.map({ s -> s ++ \"!\" })\n" +
        "match shouted\n" +
        "  | #some(s) -> println(s)\n" +
        "  | #empty -> println(\"empty\")\n" +
        "def seven = Option.defaultValue(#none, 7)\n" +
        "def three = Option.mapOr(name, 0, { _s -> 3 })\n" +
        "println(\"{seven} {three}\")\n"
    runSource source |> assertRan "some 2\nerror ERR: odd\nsome 20\n0\nabc!\n7 3\n"

[<Fact>]
let ``Run - a proc block that ends without return runs for what it does and is #some()`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }\n" +
        "\n" +
        "def show: [Console] int -> #some() | #odd = { n -> proc {\n" +
        "  def h = try halve(n)\n" +
        "  println(\"half of {n} is {h}\")\n" +
        "} }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match show(10)\n" +
        "    | #some() -> println(\"shown\")\n" +
        "    | #odd -> println(\"odd\")\n" +
        "  match show(3)\n" +
        "    | #some() -> println(\"shown\")\n" +
        "    | #odd -> println(\"odd\")\n" +
        "}\n"
    runSource source |> assertRan "half of 10 is 5\nshown\nodd\n"