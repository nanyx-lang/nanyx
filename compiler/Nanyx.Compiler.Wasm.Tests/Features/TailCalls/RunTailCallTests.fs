module TranspilerWasmTests.Features.TailCalls.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

// Tail calls use no stack (docs/src/content/work-in-progress/tail-calls.md). Each of these recurses a million calls deep, far past
// what the stack holds when every call keeps a frame.

[<Fact>]
let ``Run - a function calling itself in tail position recurses a million deep`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def countdown: (int, int) -> int = { n, acc ->\n" +
        "  if n == 0 then acc else countdown(n - 1, acc + 1)\n" +
        "}\n" +
        "\n" +
        "def steps: (int, int) -> int = { n, acc ->\n" +
        "  match n\n" +
        "  | 0 -> acc\n" +
        "  | _ -> n - 1 \\steps(acc + 1)\n" +
        "}\n" +
        "\n" +
        "println(\"{countdown(1000000, 0)}\")\n" +
        "println(\"{steps(1000000, 0)}\")\n"
    runSource source |> assertRan "1000000\n1000000\n"

[<Fact>]
let ``Run - a local rec function calling itself in tail position recurses a million deep`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def count: int -> int = { n ->\n" +
        "  rec loop = { i, acc ->\n" +
        "    if i == 0 then acc else loop(i - 1, acc + 1)\n" +
        "  }\n" +
        "  loop(n, 0)\n" +
        "}\n" +
        "\n" +
        "println(\"{count(1000000)}\")\n"
    runSource source |> assertRan "1000000\n"

[<Fact>]
let ``Run - functions calling each other in tail position recurse a million deep`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def isEven: int -> bool = { n -> if n == 0 then true else isOdd(n - 1) }\n" +
        "def isOdd: int -> bool = { n -> if n == 0 then false else isEven(n - 1) }\n" +
        "\n" +
        "println(\"{isEven(1000000)}\")\n"
    runSource source |> assertRan "true\n"

[<Fact>]
let ``Run - a tail call through a closure uses no stack`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def callBack: (int, (int -> int)) -> int = { n, f ->\n" +
        "  match n\n" +
        "  | 0 -> 0\n" +
        "  | _ -> f(n - 1)\n" +
        "}\n" +
        "\n" +
        "def again: int -> int = { n -> callBack(n, again) }\n" +
        "\n" +
        "println(\"{again(1000000)}\")\n"
    runSource source |> assertRan "0\n"

[<Fact>]
let ``Run - a tail call that passes contexts on uses no stack`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Counter = (\n" +
        "  next: int -> int\n" +
        ")\n" +
        "\n" +
        "def counted: [Counter] (int, int) -> int = { n, acc ->\n" +
        "  if n == 0 then acc else counted(next(n), acc + 1)\n" +
        "}\n" +
        "\n" +
        "def main = {\n" +
        "  use Counter(next = { n -> n - 1 })\n" +
        "  println(\"{counted(1000000, 0)}\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "1000000\n"

[<Fact>]
let ``Run - the body of use ... in is in tail position`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Counter = (next: int -> int)\n" +
        "\n" +
        "def loop: (int, int) -> int = { n, acc ->\n" +
        "  if n == 0 then acc else use Counter(next = { m -> m - 1 }) in loop(next(n), acc + 1)\n" +
        "}\n" +
        "\n" +
        "println(\"{loop(1000000, 0)}\")\n"
    runSource source |> assertRan "1000000\n"
