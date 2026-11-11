module TranspilerWasmTests.Features.StdLib.RunOptionResult

open Xunit
open TranspilerWasmTestHelpers

// `show...` match on the value: `dbg` of a tag returned from a call prints its raw encoding.
let private header =
    "import nanyx/console\n" +
    "import nanyx/text\n" +
    "\n" +
    "def showInts: [Console] list(int) -> () = { xs ->\n" +
    "  println(\"[\" ++ String.join(xs \\List.map(Int.toString), \", \") ++ \"]\")\n" +
    "}\n" +
    "def showOption: [Console] (#some(int) | #none) -> () = {\n" +
    "  | #some(n) -> println(\"#some({n})\")\n" +
    "  | #none -> println(\"#none\")\n" +
    "}\n" +
    "def showResult: [Console] Result(int, string) -> () = {\n" +
    "  | #some(n) -> println(\"#some({n})\")\n" +
    "  | #error(e) -> println(\"#error({e})\")\n" +
    "}\n" +
    "\n" +
    "def present: #some(int) | #none = #some(3)\n" +
    "def absent: #some(int) | #none = #none\n" +
    "def ok: Result(int, string) = #some(4)\n" +
    "def bad: Result(int, string) = #error(\"bad\")\n" +
    "\n"

[<Fact>]
let ``Run - toListOption gives the value of #some as a list, and the empty list otherwise`` () =
    let source =
        header +
        "@entry\n" +
        "def main = {\n" +
        "  showInts(toListOption(present))\n" +
        "  showInts(absent \\toListOption)\n" +
        "  def notFound: #some(int) | #notFound = #notFound\n" +
        "  showInts([1, ...(notFound \\toListOption)])\n" +
        "}\n"
    runSource source |> assertRan "[3]\n[]\n[1]\n"

[<Fact>]
let ``Run - valuesOption keeps the values of the #somes in order and drops the other tags`` () =
    let source =
        header +
        "@entry\n" +
        "def main = {\n" +
        "  showInts(valuesOption([present, absent, #some(5), absent, #some(7)]))\n" +
        "  showInts([absent, absent] \\valuesOption)\n" +
        "  showInts(valuesOption([]))\n" +
        "}\n"
    runSource source |> assertRan "[3, 5, 7]\n[]\n[]\n"

[<Fact>]
let ``Run - valuesOption of a long list does not overflow the stack`` () =
    let source =
        header +
        "def options: int, list(#some(int) | #none) -> list(#some(int) | #none) = {\n" +
        "  | 0, acc -> acc\n" +
        "  | n, acc -> options(n - 1, [if n % 2 == 0 then #some(n) else #none, ...acc])\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def values = valuesOption(options(200000, []))\n" +
        "  dbg(List.length(values))\n" +
        "  dbg(defaultValueOption(List.head(values), 0))\n" +
        "}\n"
    runSource source |> assertRan "dbg: 100000\ndbg: 2\n"

[<Fact>]
let ``Run - Result.mapOr applies the function to a success and gives the default for an error`` () =
    let source =
        header +
        "@entry\n" +
        "def main = {\n" +
        "  dbg(Result.mapOr(ok, 0, { n -> n * 10 }))\n" +
        "  dbg(bad \\Result.mapOr(0, { n -> n * 10 }))\n" +
        "  println(Result.mapOr(bad, \"none\", Int.toString))\n" +
        "}\n"
    runSource source |> assertRan "dbg: 40\ndbg: 0\nnone\n"

[<Fact>]
let ``Run - Result.toOption gives #some for a success and #none for an error`` () =
    let source =
        header +
        "@entry\n" +
        "def main = {\n" +
        "  showOption(Result.toOption(ok))\n" +
        "  showOption(bad \\Result.toOption)\n" +
        "}\n"
    runSource source |> assertRan "#some(4)\n#none\n"

[<Fact>]
let ``Run - Result.orElse keeps a success and passes an error to the fallback`` () =
    let source =
        header +
        "def retry: string -> Result(int, string) = { e -> #error(\"retried {e}\") }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  showResult(Result.orElse(ok, { e -> #some(0) }))\n" +
        "  showResult(bad \\Result.orElse { e -> #some(String.length(e)) })\n" +
        "  showResult(bad \\Result.orElse(retry))\n" +
        "  showResult(bad \\Result.orElse { e -> #error(\"recovered from {e}\") })\n" +
        "}\n"
    runSource source |> assertRan "#some(4)\n#some(3)\n#error(retried bad)\n#error(recovered from bad)\n"

// #183: two steps that fail with different tags.
let private steps =
    "import nanyx/console\n" +
    "\n" +
    "def parseDigit: string -> #some(int) | #invalidInt = {\n" +
    "  | \"1\" -> #some(1)\n" +
    "  | _ -> #invalidInt\n" +
    "}\n" +
    "def find: int -> #some(string) | #notFound = {\n" +
    "  | 1 -> #some(\"1\")\n" +
    "  | 2 -> #some(\"x\")\n" +
    "  | _ -> #notFound\n" +
    "}\n"

[<Fact>]
let ``Run - bindOption keeps the tags both steps fail with, matched with no catch-all`` () =
    let source =
        steps +
        "def piped: int -> string = { n ->\n" +
        "  match find(n) \\bindOption(parseDigit)\n" +
        "    | #some(digit) -> \"#some({digit})\"\n" +
        "    | #notFound -> \"#notFound\"\n" +
        "    | #invalidInt -> \"#invalidInt\"\n" +
        "}\n" +
        "def called: int -> string = { n ->\n" +
        "  def b = bindOption(find(n), parseDigit)\n" +
        "  match b\n" +
        "    | #some(digit) -> \"#some({digit})\"\n" +
        "    | #notFound -> \"#notFound\"\n" +
        "    | #invalidInt -> \"#invalidInt\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(piped(1))\n" +
        "  println(piped(2))\n" +
        "  println(piped(3))\n" +
        "  println(called(1))\n" +
        "  println(called(2))\n" +
        "  println(called(3))\n" +
        "}\n"
    runSource source |> assertRan "#some(1)\n#invalidInt\n#notFound\n#some(1)\n#invalidInt\n#notFound\n"

[<Fact>]
let ``Run - Result.bind continues a success and stops at the first error`` () =
    let source =
        header +
        "def half: int -> Result(int, string) = { n -> if n % 2 == 0 then #some(n / 2) else #error(\"odd\") }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  showResult(ok \\Result.bind(half))\n" +
        "  showResult(Result.bind(ok, half) \\Result.bind(half))\n" +
        "  showResult(Result.bind(bad, half))\n" +
        "}\n"
    runSource source |> assertRan "#some(2)\n#some(1)\n#error(bad)\n"
