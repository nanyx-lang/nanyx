module TranspilerWasmTests.Features.Dbg.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - dbg as a pipe prints each kind of value`` () =
    let source =
        "\"hi\" \\dbg\n" +
        "5 \\dbg\n" +
        "#ok \\dbg\n" +
        "#some(3) \\dbg\n"
    runSource source |> assertRan "dbg: hi\ndbg: 5\ndbg: #ok\ndbg: #some(3)\n"

[<Fact>]
let ``Run - dbg piped from a call in a for loop`` () =
    let source =
        "def show = { i -> \"{i}\" }\n" +
        "\n" +
        "for 1..3 | i ->\n" +
        "    show(i) \\dbg\n"
    runSource source |> assertRan "dbg: 1\ndbg: 2\ndbg: 3\n"

[<Fact>]
let ``Run - dbg prints a bool as true or false`` () =
    let source =
        "def flag = true\n" +
        "dbg(flag)\n" +
        "dbg(false)\n"
    runSource source |> assertRan "dbg: true\ndbg: false\n"

[<Fact>]
let ``Run - dbg prints the bool a comparison gives`` () =
    let source =
        "type Point = (x: int, y: int)\n" +
        "def a = Point(x = 0, y = 0)\n" +
        "def b = Point(x = 0, y = 0)\n" +
        "dbg(a == b)\n" +
        "dbg(1 == 2)\n" +
        "dbg(1 < 2)\n" +
        "dbg(2 < 1)\n"
    runSource source |> assertRan "dbg: true\ndbg: false\ndbg: true\ndbg: false\n"

[<Fact>]
let ``Run - dbg prints a bool in a tag's payload as true or false`` () =
    let source =
        "def flag = 1 < 2\n" +
        "dbg(#some(true))\n" +
        "dbg(#some(flag))\n" +
        "def wrapped = if flag then #yes(false) else #no\n" +
        "dbg(wrapped)\n"
    runSource source |> assertRan "dbg: #some(true)\ndbg: #some(true)\ndbg: #yes(false)\n"

[<Fact>]
let ``Run - dbg of a float or a tag in a program with no other strings`` () =
    runSource "dbg(1.5)\n" |> assertRan "dbg: 1.5\n"
    runSource "def flag = 1 < 2\ndef t = if flag then #yes else #no\ndbg(t)\n" |> assertRan "dbg: #yes\n"

[<Fact>]
let ``Run - dbg prints a string in a tag's payload as the string`` () =
    runSource "dbg(#some(\"hi\"))\n" |> assertRan "dbg: #some(\"hi\")\n"
