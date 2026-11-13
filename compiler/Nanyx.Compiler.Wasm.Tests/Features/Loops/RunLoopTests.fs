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
let ``Run - for loops over several iterables walk them in step`` () =
    // The program from the loops page: a list zipped with a range that has no end counts its elements.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def things = [\n" +
        "  \"Alex\"\n" +
        "  \"Sam\"\n" +
        "  \"Jo\"\n" +
        "]\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  for things | thing ->\n" +
        "    println(thing)\n" +
        "  for things, 0.. | thing, i ->\n" +
        "    println(\"{i}: {thing}\")\n" +
        "  for things, 1..2 | \"Sam\", i -> println(\"Sam is {i}\")\n" +
        "                   | thing, _ -> println(\"not Sam: {thing}\")\n" +
        "  for 10..12, [[1], [2, 3], [4, 5, 6], [7]], 100.. | a, xs, c -> println(\"{a} {list.length(xs)} {c}\")\n" +
        "  for things, things -> println(\"tick\")\n" +
        "  for things, 0.. | thing, i ->\n" +
        "    for [true, false], 0.. | flag, j -> println(\"{i}.{j} {thing} {flag}\")\n" +
        "}\n"
    let expected =
        "Alex\nSam\nJo\n0: Alex\n1: Sam\n2: Jo\nnot Sam: Alex\nSam is 2\n10 1 100\n11 2 101\n12 3 102\n" +
        "tick\ntick\ntick\n" +
        "0.0 Alex true\n0.1 Alex false\n1.0 Sam true\n1.1 Sam false\n2.0 Jo true\n2.1 Jo false\n"
    runSource source |> assertRan expected

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

[<Fact>]
let ``Run - while loops run while their condition holds`` () =
    // The program from the loops page (#957), and loops whose condition and body read a list held in a cell: each time
    // round they borrow it, so the memory checks find nothing left over.
    let source = """
import nanyx/console

def countdown = {
  memory {
    mut remaining = 3

    while remaining > 0 ->
      println("{remaining}...")
      remaining := remaining - 1
  }
}

def pairs = { n ->
  memory {
    mut i = 0
    while i < n ->
      mut j = 0
      while j < i ->
        println("{i},{j}")
        j := j + 1
      i := i + 1
  }
}

def drain = {
  memory {
    mut items = ["a", "b", "c"]
    while list.length(items) > 0 ->
      match items
        | [first, ...rest] ->
          println(first)
          items := rest
        | [] -> ()
    while false -> println("never")
  }
}

@entry
def main = {
  countdown()
  pairs(3)
  drain()
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3...\n2...\n1...\n1,0\n2,0\n2,1\na\nb\nc\n"

[<Fact>]
let ``Run - loops, floats, pipes and guards through the core IR`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def area: float -> float = { r -> r * r * 2.0 }\n" +
        "\n" +
        "def size = { n ->\n" +
        "  match n\n" +
        "    | > 100 -> \"big\"\n" +
        "    | < 0 -> \"negative\"\n" +
        "    | _ -> \"small\"\n" +
        "}\n" +
        "\n" +
        "def double: int -> int = { n -> n * 2 }\n" +
        "\n" +
        "def main = {\n" +
        "  for [1, 60, -3] | n -> println(size(n \\double))\n" +
        "  for 1..2 | i -> println(\"{i}: {area(1.5)}\")\n" +
        "  if area(1.0) > 1.5 then println(\"yes\") else println(\"no\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "small\nbig\nnegative\n1: 4.5\n2: 4.5\nyes\n"
