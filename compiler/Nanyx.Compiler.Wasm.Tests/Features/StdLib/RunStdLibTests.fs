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
