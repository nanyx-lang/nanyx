module TranspilerWasmTests.Features.Loops.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - for loops over ranges and lists`` () =
    // Regression: `for 1..3 | i -> println("{i}")` was a parse error (#37).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def shout: [Console] list(string) -> () = { names ->\n" +
        "  for names | name -> println(name ++ \"!\")\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  for 1..3 | i -> println(\"{i}\")\n" +
        "  for [\"a\", \"b\"] | item -> println(item)\n" +
        "  for 1..5 | 5 -> println(\"Go!\")\n" +
        "           | _ -> println(\"Ready...\")\n" +
        "  for 1..2 -> println(\"tick\")\n" +
        "  def n = 2\n" +
        "  for 1..n | i ->\n" +
        "    for 1..n | j -> println(\"{i},{j}\")\n" +
        "  shout([\"hey\"])\n" +
        "}\n"
    runSource source |> assertRan "1\n2\n3\na\nb\nReady...\nReady...\nReady...\nReady...\nGo!\ntick\ntick\n1,1\n1,2\n2,1\n2,2\nhey!\n"

[<Fact>]
let ``Run - rec local loops`` () =
    // Regression: `rec loop = { ... }` was a parse error (#38).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def countdown: int -> int = { n ->\n" +
        "  rec loop = { i, acc -> if i > 0 then loop(i - 1, acc + i) else acc }\n" +
        "  loop(n, 0)\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{countdown(10)}\")\n" +
        "  rec say = { i ->\n" +
        "    if i > 0 then\n" +
        "      println(\"{i}\")\n" +
        "      say(i - 1)\n" +
        "  }\n" +
        "  say(2)\n" +
        "}\n"
    runSource source |> assertRan "55\n2\n1\n"
