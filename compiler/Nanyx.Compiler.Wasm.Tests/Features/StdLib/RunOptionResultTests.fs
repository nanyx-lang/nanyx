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
let ``Run - Option.toList gives the value of #some as a list, and the empty list otherwise`` () =
    let source =
        header +
        "@entry\n" +
        "def main = {\n" +
        "  showInts(Option.toList(present))\n" +
        "  showInts(absent \\Option.toList)\n" +
        "  def notFound: #some(int) | #notFound = #notFound\n" +
        "  showInts([1, ...(notFound \\Option.toList)])\n" +
        "}\n"
    runSource source |> assertRan "[3]\n[]\n[1]\n"

[<Fact>]
let ``Run - Option.values keeps the values of the #somes in order and drops the other tags`` () =
    let source =
        header +
        "@entry\n" +
        "def main = {\n" +
        "  showInts(Option.values([present, absent, #some(5), absent, #some(7)]))\n" +
        "  showInts([absent, absent] \\Option.values)\n" +
        "  showInts(Option.values([]))\n" +
        "}\n"
    runSource source |> assertRan "[3, 5, 7]\n[]\n[]\n"

[<Fact>]
let ``Run - Option.values of a long list does not overflow the stack`` () =
    let source =
        header +
        "def options: int, list(#some(int) | #none) -> list(#some(int) | #none) = {\n" +
        "  | 0, acc -> acc\n" +
        "  | n, acc -> options(n - 1, [if n % 2 == 0 then #some(n) else #none, ...acc])\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def values = Option.values(options(200000, []))\n" +
        "  dbg(List.length(values))\n" +
        "  dbg(Option.defaultValue(List.head(values), 0))\n" +
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
