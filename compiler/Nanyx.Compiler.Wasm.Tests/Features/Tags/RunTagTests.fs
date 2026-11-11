module TranspilerWasmTests.Features.Tags.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an unannotated function whose branches return different tags`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def getMessage = { flag ->\n" +
        "  def message = \"Hello, from Nanyx!\"\n" +
        "  if flag then #some(message)\n" +
        "  else #noMessage\n" +
        "}\n" +
        "\n" +
        "def describe = { flag ->\n" +
        "  match getMessage(flag)\n" +
        "    | #some(_) -> println(\"There was a message\")\n" +
        "    | #noMessage -> println(\"No message available\")\n" +
        "}\n" +
        "\n" +
        "describe(true)\n" +
        "describe(false)\n"
    runSource source |> assertRan "There was a message\nNo message available\n"

[<Fact>]
let ``Run - a piped lambda can match only the tags left of a union's rest`` () =
    // The rest `ω` is `#invalidInt` from the piped value, and the lambda's input is `#invalidInt` from its pattern:
    // one type, a tag alone and a union of one (#457).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def mapError = {\n" +
        "  | #some(x), _ -> #some(x)\n" +
        "  | other, f -> f(other)\n" +
        "}\n" +
        "\n" +
        "def show: #some(int) | #bad(string) -> string = {\n" +
        "  | #some(n) -> \"some {n}\"\n" +
        "  | #bad(s) -> s\n" +
        "}\n" +
        "\n" +
        "println(show(int.fromString(\"12\") \\mapError({ #invalidInt -> #bad(\"bad\") })))\n" +
        "println(show(int.fromString(\"zz\") \\mapError({ #invalidInt -> #bad(\"bad\") })))\n"
    runSource source |> assertRan "some 12\nbad\n"

[<Fact>]
let ``Run - a tag union rest passes the tags a match does not name through`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "\n" +
        "def show: (#some(string) | #notFound | #error) -> string = { | #some(s) -> s\n" +
        "                                                       | #notFound -> \"notFound\"\n" +
        "                                                       | #error -> \"error\" }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def x: (#some(int) | #notFound | #error) = #some(20)\n" +
        "  def y: (#some(int) | #notFound | #error) = #error\n" +
        "  println(show(mapOption(x, { n -> \"{n + 1}\" })))\n" +
        "  println(show(mapOption(y, { n -> \"{n + 1}\" })))\n" +
        "}\n"
    runSource source |> assertRan "21\nerror\n"

[<Fact>]
let ``Run - interpolating tags without a payload`` () =
    let source =
        "def flip = { | #on -> #off | #off -> #on }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def c = if 1 > 2 then #ok else #none\n" +
        "  println(\"{c} {flip(#on)} {#done}\")\n" +
        "}\n"
    runSource source |> assertRan "#none #off #done\n"

[<Fact>]
let ``Run - interpolating a tag whose type names a tag no expression writes`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: (#red | #green) -> string = { c -> \"colour {c}\" }\n" +
        "\n" +
        "println(show(#red))\n"
    runSource source |> assertRan "colour #red\n"

[<Fact>]
let ``Run - a tag keeps a payload that does not fit in 16 bits`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: [Console] (#some(int) | #none) -> () = {\n" +
        "  | #some(70000) -> println(\"seventy thousand\")\n" +
        "  | #some(n) -> println(\"#some({n})\")\n" +
        "  | #none -> println(\"#none\")\n" +
        "}\n" +
        "\n" +
        "show(#some(-17))\n" +
        "show(#some(70000))\n" +
        "show(#some(4464))\n" +
        "show(#none)\n" +
        "println(\"{#some(70000) == #some(70000)} {#some(70000) == #some(4464)} {#some(1) != #some(2)}\")\n" +
        "dbg(#some(-17))\n"
    runSource source |> assertRan "#some(-17)\nseventy thousand\n#some(4464)\n#none\ntrue false true\ndbg: #some(-17)\n"

[<Fact>]
let ``Run - a tag's string payload allocated past 64 KB of heap`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def grow: (string, int) -> string = { s, n -> if n == 0 then s else grow(s ++ \"x\", n - 1) }\n" +
        "\n" +
        "def show: [Console] (#name(string) | #anon) -> () = {\n" +
        "  | #name(s) -> println(\"name {s}\")\n" +
        "  | #anon -> println(\"anon\")\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  -- About 80 KB of strings, so what comes next is allocated above 64 KB.\n" +
        "  def filler = grow(\"\", 400)\n" +
        "  show(#name(\"Ada\" ++ \"!\"))\n" +
        "  println(\"filler is empty: {filler == \"\"}\")\n" +
        "}\n"
    runSource source |> assertRan "name Ada!\nfiller is empty: false\n"
