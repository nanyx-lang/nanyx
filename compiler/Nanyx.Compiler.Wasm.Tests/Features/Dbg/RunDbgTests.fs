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
