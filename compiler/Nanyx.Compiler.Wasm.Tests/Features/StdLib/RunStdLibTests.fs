module TranspilerWasmTests.Features.StdLib.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - text upper-cases and prints`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.uppercase(\"hello, nanyx!\"))\n" +
        "\"shout\" \\uppercase \\println\n"
    runSource source |> assertRan "HELLO, NANYX!\nSHOUT\n"

[<Fact>]
let ``Run - text length, lowercase, trim, repeat and substring`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "dbg(String.length(\"\"))\n" +
        "dbg(String.length(\"hello\"))\n" +
        "println(String.lowercase(\"Hello, NANYX!\"))\n" +
        "println(\"[\" ++ String.trim(\"  padded \") ++ \"]\")\n" +
        "println(\"[\" ++ String.trim(\"   \") ++ \"]\")\n" +
        "println(String.repeat(\"ab\", 3))\n" +
        "println(\"[\" ++ String.repeat(\"ab\", 0) ++ \"]\")\n" +
        "println(String.substring(\"nanyx\", 1, 3))\n" +
        "println(String.substring(\"nanyx\", 3, 10))\n"
    runSource source |> assertRan "dbg: 0\ndbg: 5\nhello, nanyx!\n[padded]\n[]\nababab\n[]\nany\nyx\n"

[<Fact>]
let ``Run - text startsWith, endsWith and contains`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: [Console] bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "show(String.startsWith(\"nanyx\", \"nan\"))\n" +
        "show(String.startsWith(\"nanyx\", \"nyx\"))\n" +
        "show(String.startsWith(\"nan\", \"nanyx\"))\n" +
        "show(String.endsWith(\"nanyx\", \"nyx\"))\n" +
        "show(String.endsWith(\"nanyx\", \"nan\"))\n" +
        "show(String.contains(\"hello world\", \"o w\"))\n" +
        "show(String.contains(\"hello world\", \"xyz\"))\n" +
        "show(String.contains(\"hello\", \"\"))\n"
    runSource source |> assertRan "yes\nno\nno\nyes\nno\nyes\nno\nyes\n"

[<Fact>]
let ``Run - text indexOf, lastIndexOf and count`` () =
    // `show` matches on the result: `dbg` of a tag returned from a call prints its raw encoding.
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: [Console] (#some(int) | #notFound) -> () = {\n" +
        "  | #some(i) -> println(\"#some({i})\")\n" +
        "  | #notFound -> println(\"#notFound\")\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  show(String.indexOf(\"hello\", \"ll\"))\n" +
        "  show(String.indexOf(\"abcabc\", \"c\"))\n" +
        "  show(String.indexOf(\"hello\", \"xyz\"))\n" +
        "  show(String.indexOf(\"lo\", \"hello\"))\n" +
        "  show(String.indexOf(\"hello\", \"\"))\n" +
        "  show(String.lastIndexOf(\"abcabc\", \"bc\"))\n" +
        "  show(String.lastIndexOf(\"abc\", \"\"))\n" +
        "  show(String.lastIndexOf(\"ab\", \"abc\"))\n" +
        "  dbg(String.count(\"banana\", \"an\"))\n" +
        "  dbg(String.count(\"aaaa\", \"aa\"))\n" +
        "  dbg(String.count(\"abc\", \"\"))\n" +
        "}\n"
    runSource source
    |> assertRan (
        "#some(2)\n#some(2)\n#notFound\n#notFound\n#some(0)\n" +
        "#some(4)\n#some(3)\n#notFound\n" +
        "dbg: 2\ndbg: 2\ndbg: 0\n")

[<Fact>]
let ``Run - text replace, removePrefix, removeSuffix, padStart and padEnd`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.replace(\"hello world\", \"o\", \"0\"))\n" +
        "println(String.replace(\"a-b-c\", \"-\", \" - \"))\n" +
        "println(String.replace(\"abc\", \"\", \"x\"))\n" +
        "println(String.removePrefix(\"nanyx\", \"nan\"))\n" +
        "println(String.removePrefix(\"nanyx\", \"nyx\"))\n" +
        "println(String.removeSuffix(\"nanyx\", \"nyx\"))\n" +
        "println(String.padStart(\"7\", 3, \"0\"))\n" +
        "println(String.padEnd(\"ab\", 5, \"xy\"))\n" +
        "println(String.padStart(\"long\", 2, \"0\"))\n"
    runSource source |> assertRan "hell0 w0rld\na - b - c\nabc\nyx\nnanyx\nna\n007\nabxyx\nlong\n"

[<Fact>]
let ``Run - text trimStart, trimEnd, isEmpty and isBlank`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: [Console] bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "println(\"[\" ++ String.trimStart(\" \t x \") ++ \"]\")\n" +
        "println(\"[\" ++ String.trimEnd(\" x \r\n\") ++ \"]\")\n" +
        "show(String.isEmpty(\"\"))\n" +
        "show(String.isEmpty(\" \"))\n" +
        "show(String.isBlank(\" \t\n\"))\n" +
        "show(String.isBlank(\" x \"))\n"
    runSource source |> assertRan "[x ]\n[ x]\nyes\nno\nyes\nno\n"

[<Fact>]
let ``Run - text lines and words`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.join(String.lines(\"a\nb\r\nc\n\"), \"|\"))\n" +
        "println(String.join(String.lines(\"a\n\nb\"), \"|\"))\n" +
        "dbg(String.length(String.concat(String.lines(\"\"))))\n" +
        "println(String.join(String.words(\"  one two\tthree\n\"), \"|\"))\n" +
        "dbg(String.length(String.concat(String.words(\"   \"))))\n"
    runSource source |> assertRan "a|b|c\na||b\ndbg: 0\none|two|three\ndbg: 0\n"

[<Fact>]
let ``Run - text split, join and concat`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def parts = String.split(\"a,b,,c\", \",\")\n" +
        "println(String.join(parts, \" | \"))\n" +
        "println(String.join(String.split(\"no separators\", \"-\"), \"+\"))\n" +
        "println(String.join(String.split(\"a--b\", \"--\"), \"/\"))\n" +
        "println(\"[\" ++ String.join([], \", \") ++ \"]\")\n" +
        "println(String.concat([\"Hello\", \", \", \"World\"]))\n" +
        "println(String.join(String.split(\"x\", \"\"), \"?\"))\n"
    runSource source |> assertRan "a | b |  | c\nno separators\na/b\n[]\nHello, World\nx\n"

[<Fact>]
let ``Run - a string's and a list's built-in length`` () =
    // No `import nanyx/text`: a string's length is a property of the string, not an attached definition.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def size: string -> int = { s -> s.length }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def words = [\"nan\", \"yx\", \"lang\"]\n" +
        "  def greeting = \"hello\"\n" +
        "  println(\"{greeting.length} {words.length} {size(\"\")} {[].length}\")\n" +
        "  def record = (length = \"field\")\n" +
        "  println(record.length)\n" +
        "}\n"
    runSource source |> assertRan "5 3 0 0\nfield\n"

[<Fact>]
let ``Run - Int.parse reads decimal integers and rejects anything else`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: [Console] (#some(int) | #invalidInt) -> () = {\n" +
        "  | #some(n) -> println(\"#some({n})\")\n" +
        "  | #invalidInt -> println(\"#invalidInt\")\n" +
        "}\n" +
        "\n" +
        "show(Int.parse(\"42\"))\n" +
        "show(Int.parse(\"+5\"))\n" +
        "show(Int.parse(\"007\"))\n" +
        "show(Int.parse(\"0\"))\n" +
        "show(Int.parse(\"-17\"))\n" +
        "show(Int.parse(\"70000\"))\n" +
        "show(Int.parse(\"\"))\n" +
        "show(Int.parse(\"-\"))\n" +
        "show(Int.parse(\"4x2\"))\n" +
        "show(Int.parse(\" 1\"))\n" +
        "show(\"12\" \\ Int.parse)\n"
    runSource source |> assertRan "#some(42)\n#some(5)\n#some(7)\n#some(0)\n#some(-17)\n#some(70000)\n#invalidInt\n#invalidInt\n#invalidInt\n#invalidInt\n#some(12)\n"

[<Fact>]
let ``Run - toString of an int, a float, a bool and a string, called and piped`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "println(Int.toString(42))\n" +
        "println(Int.toString(-17))\n" +
        "println(Float.toString(1.5))\n" +
        "println(Bool.toString(true))\n" +
        "println(Bool.toString(false))\n" +
        "println(String.toString(\"Ada\"))\n" +
        "println(7 \\toString)\n" +
        "println(2.25 \\toString)\n" +
        "println(false \\toString)\n" +
        "println(\"text\" \\toString)\n"
    runSource source |> assertRan "42\n-17\n1.5\ntrue\nfalse\nAda\n7\n2.25\nfalse\ntext\n"

[<Fact>]
let ``Run - toString of each built-in type passed as a function`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.join([1, 2, 3] \\List.map(Int.toString), \", \"))\n" +
        "println(String.join([0.5, 2.0] \\List.map(Float.toString), \", \"))\n" +
        "println(String.join([true, false] \\List.map(Bool.toString), \", \"))\n" +
        "println(String.join([\"a\", \"b\"] \\List.map(String.toString), \", \"))\n" +
        "println(String.join([4, 5] \\map(Int.toString), \"+\"))\n"
    runSource source |> assertRan "1, 2, 3\n0.5, 2.0\ntrue, false\na, b\n4+5\n"

[<Fact>]
let ``Run - a ToString of each built-in type is synthesized from the prelude's toString`` () =
    // Nothing names `Int.toString` here: the instance the type checker builds from it must still be compiled.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context ToString(a) = (toString: a -> string)\n" +
        "def label: [ToString(a)] a -> string = { x -> \"<\" ++ toString(x) ++ \">\" }\n" +
        "println(label(42))\n" +
        "println(label(true))\n" +
        "println(label(1.5))\n" +
        "println(label(\"s\"))\n"
    runSource source |> assertRan "<42>\n<true>\n<1.5>\n<s>\n"

[<Fact>]
let ``Run - nanyx/math abs, min, max, clamp and pow`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "def a = math.abs(-42)\n" +
        "def b = math.min(3, 7)\n" +
        "def c = math.max(3, 7)\n" +
        "def d = math.clamp(120, 0, 100)\n" +
        "def e = math.clamp(-5, 0, 100)\n" +
        "def f = math.pow(2, 8)\n" +
        "def g = math.pow(5, 0)\n" +
        "println(\"{a} {b} {c} {d} {e} {f} {g}\")\n"
    runSource source |> assertRan "42 3 7 100 0 256 1\n"

[<Fact>]
let ``Run - nanyx/math sqrt, floor, ceil and round`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "def s = math.sqrt(81.0)\n" +
        "def t = math.sqrt(2.0)\n" +
        "println(\"{s} {t}\")\n" +
        "println(\"{math.floor(3.9)} {math.floor(-3.1)} {math.floor(4.0)}\")\n" +
        "println(\"{math.ceil(3.1)} {math.ceil(-3.9)} {math.ceil(4.0)}\")\n" +
        "println(\"{math.round(3.5)} {math.round(2.5)} {math.round(-2.5)} {math.round(2.4999)} {math.round(-0.4)}\")\n"
    runSource source |> assertRan "9.0 1.414214\n3 -4 4\n4 -3 4\n4 3 -3 2 0\n"

[<Fact>]
let ``Run - nanyx/math floor of a float out of the range of int stops the program`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "println(\"before\")\n" +
        "def n = math.floor(1.0e12)\n" +
        "println(\"after {n}\")\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains("wasm trap: integer overflow", outcome.StandardError)

[<Fact>]
let ``Run - nanyx/math round of NaN stops the program`` () =
    let source =
        "import nanyx/math as math\n" +
        "\n" +
        "dbg(math.round(0.0 / 0.0))\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("wasm trap: invalid conversion to integer", outcome.StandardError)
