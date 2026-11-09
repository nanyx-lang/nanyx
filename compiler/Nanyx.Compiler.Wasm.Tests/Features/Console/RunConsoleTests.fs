module TranspilerWasmTests.Features.Console.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - script hello world prints the greeting`` () =
    runE2E "twentythree" |> assertRan "Hello, Nanyx!\n"

[<Fact>]
let ``Run - project hello world runs the entry point`` () =
    runE2E "twentyfour" |> assertRan "Hello, World!\n"

[<Fact>]
let ``Run - dbg prints integers, strings and tags in order`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def main = {\n" +
        "  dbg(42)\n" +
        "  dbg(-7)\n" +
        "  dbg(\"text\")\n" +
        "  dbg(#ok)\n" +
        "  dbg(#some(5))\n" +
        "  println(\"done\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "dbg: 42\ndbg: -7\ndbg: text\ndbg: #ok\ndbg: #some(5)\ndone\n"

[<Fact>]
let ``Run - dbg prints a tag-typed value by its tag and payload`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def pick = { n ->\n" +
        "  if n == 0 then #some(\"first\")\n" +
        "  else if n == 1 then #none\n" +
        "  else #err\n" +
        "}\n" +
        "\n" +
        "def main = {\n" +
        "  def a = dbg(pick(0))\n" +
        "  dbg(pick(1))\n" +
        "  dbg(pick(2))\n" +
        "  dbg(dbg(a))\n" +
        "  println(\"done\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source
    |> assertRan "dbg: #some(\"first\")\ndbg: #none\ndbg: #err\ndbg: #some(\"first\")\ndbg: #some(\"first\")\ndone\n"

[<Fact>]
let ``Run - console print and println write to stdout`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "print(\"a\")\n" +
        "print(\"b\")\n" +
        "println(\"\")\n" +
        "println(\"c\")\n"
    runSource source |> assertRan "ab\nc\n"

[<Fact>]
let ``Run - eprint and eprintln write to stderr`` () =
    let source =
        "def warn: [Console] string -> () = { message -> eprintln(\"warning: \" ++ message) }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  print(\"out \")\n" +
        "  eprint(\"err \")\n" +
        "  flush()\n" +
        "  warn(\"careful\")\n" +
        "  println(\"done\")\n" +
        "}\n"
    let outcome = runSource source
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal("out done\n", outcome.StandardOutput)
    Assert.Equal("err warning: careful\n", outcome.StandardError)

[<Fact>]
let ``Run - readLine and readToEnd read stdin`` () =
    let source =
        "@entry\n" +
        "def main = {\n" +
        "  match readLine()\n" +
        "    | #some(line) -> println(\"first [\" ++ line ++ \"]\")\n" +
        "    | #error(_) -> println(\"no first\")\n" +
        "  match readLine()\n" +
        "    | #some(line) -> println(\"second [\" ++ line ++ \"]\")\n" +
        "    | #error(_) -> println(\"no second\")\n" +
        "  match readToEnd()\n" +
        "    | #some(rest) -> println(\"rest [\" ++ rest ++ \"]\")\n" +
        "    | #error(_) -> println(\"failed\")\n" +
        "  match readLine()\n" +
        "    | #some(line) -> println(\"more [\" ++ line ++ \"]\")\n" +
        "    | #error(_) -> println(\"end of input\")\n" +
        "}\n"
    runSourceWithInput "hello\r\nworld\nthe\nrest" source
    |> assertRan "first [hello]\nsecond [world]\nrest [the\nrest]\nend of input\n"
