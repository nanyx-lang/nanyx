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
        "println(String.substring(\"nanyx\", start = 1, length = 3))\n" +
        "println(String.substring(\"nanyx\", start = 3, length = 10))\n"
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
        "println(String.replace(\"hello world\", pattern = \"o\", replacement = \"0\"))\n" +
        "println(String.replace(\"a-b-c\", pattern = \"-\", replacement = \" - \"))\n" +
        "println(String.replace(\"abc\", pattern = \"\", replacement = \"x\"))\n" +
        "println(String.removePrefix(\"nanyx\", \"nan\"))\n" +
        "println(String.removePrefix(\"nanyx\", \"nyx\"))\n" +
        "println(String.removeSuffix(\"nanyx\", \"nyx\"))\n" +
        "println(String.padStart(\"7\", width = 3, fill = \"0\"))\n" +
        "println(String.padEnd(\"ab\", width = 5, fill = \"xy\"))\n" +
        "println(String.padStart(\"long\", width = 2, fill = \"0\"))\n"
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
        "def parts = String.split(\"a,b,,c\", on = \",\")\n" +
        "println(String.join(parts, \" | \"))\n" +
        "println(String.join(String.split(\"no separators\", on = \"-\"), \"+\"))\n" +
        "println(String.join(String.split(\"a--b\", on = \"--\"), \"/\"))\n" +
        "println(\"[\" ++ String.join([], \", \") ++ \"]\")\n" +
        "println(String.concat([\"Hello\", \", \", \"World\"]))\n" +
        "println(String.join(String.split(\"x\", on = \"\"), \"?\"))\n"
    runSource source |> assertRan "a | b |  | c\nno separators\na/b\n[]\nHello, World\nx\n"

[<Fact>]
let ``Run - text split, substring, padStart, padEnd and replace take their named arguments when piped`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.join(\"a,b\" \\split(on = \",\"), \"|\"))\n" +
        "println(\"nanyx\" \\substring(start = 1, length = 3))\n" +
        "println(\"7\" \\padStart(width = 3, fill = \"0\"))\n" +
        "println((\"ab\" \\padEnd(width = 5, fill = \"xy\")) ++ \"]\")\n" +
        "println(\"hello world\" \\replace(pattern = \"o\", replacement = \"0\"))\n"
    runSource source |> assertRan "a|b\nany\n007\nabxyx]\nhell0 w0rld\n"

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
let ``Run - Int.fromString reads decimal integers and rejects anything else`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: [Console] (#some(int) | #invalidInt) -> () = {\n" +
        "  | #some(n) -> println(\"#some({n})\")\n" +
        "  | #invalidInt -> println(\"#invalidInt\")\n" +
        "}\n" +
        "\n" +
        "show(Int.fromString(\"42\"))\n" +
        "show(Int.fromString(\"+5\"))\n" +
        "show(Int.fromString(\"007\"))\n" +
        "show(Int.fromString(\"0\"))\n" +
        "show(Int.fromString(\"-17\"))\n" +
        "show(Int.fromString(\"70000\"))\n" +
        "show(Int.fromString(\"\"))\n" +
        "show(Int.fromString(\"-\"))\n" +
        "show(Int.fromString(\"4x2\"))\n" +
        "show(Int.fromString(\" 1\"))\n" +
        "show(\"12\" \\ Int.fromString)\n"
    runSource source |> assertRan "#some(42)\n#some(5)\n#some(7)\n#some(0)\n#some(-17)\n#some(70000)\n#invalidInt\n#invalidInt\n#invalidInt\n#invalidInt\n#some(12)\n"

[<Fact>]
let ``Run - Bool.fromString reads true and false and rejects anything else`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: [Console] (#some(bool) | #invalidBool) -> () = {\n" +
        "  | #some(b) -> println(\"#some({b})\")\n" +
        "  | #invalidBool -> println(\"#invalidBool\")\n" +
        "}\n" +
        "\n" +
        "show(Bool.fromString(\"true\"))\n" +
        "show(Bool.fromString(\"false\"))\n" +
        "show(Bool.fromString(\"True\"))\n" +
        "show(Bool.fromString(\"\"))\n" +
        "show(Bool.fromString(\" true\"))\n" +
        "show(Bool.fromString(\"1\"))\n" +
        "show(\"false\" \\ Bool.fromString)\n"
    runSource source |> assertRan "#some(true)\n#some(false)\n#invalidBool\n#invalidBool\n#invalidBool\n#invalidBool\n#some(false)\n"

/// `readAll`, generic in the prelude's `FromString`, and a function of each result type that sums or counts what was read.
let private readAllSource =
    "import nanyx/console\n" +
    "\n" +
    "def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e) = { xs -> xs \\List.map(fromString) }\n" +
    "\n" +
    "def total: list(#some(int) | #invalidInt) -> int = { results ->\n" +
    "  results \\Option.values \\List.fold(0, { sum, n -> sum + n })\n" +
    "}\n" +
    "\n" +
    "def countTrue: list(#some(bool) | #invalidBool) -> int = { results ->\n" +
    "  results \\Option.values \\List.filter({ b -> b }) \\List.length\n" +
    "}\n" +
    "\n"

[<Fact>]
let ``Run - a function generic in FromString reads ints and bools with the attached fromString`` () =
    let source =
        readAllSource +
        "def sumAll = { texts -> total(readAll(texts)) }\n" +
        "def trueCount = { texts -> countTrue(readAll(texts)) }\n" +
        "def firstOrZero = { texts ->\n" +
        "  match readAll(texts)\n" +
        "    | [#some(n), ..._] -> n\n" +
        "    | [#invalidInt, ..._] -> -1\n" +
        "    | [] -> 0\n" +
        "}\n" +
        "def ofInts: list(#some(int) | r) -> list(#some(int) | r) = { xs -> xs }\n" +
        "def readInts = { texts -> ofInts(readAll(texts)) }\n" +
        "\n" +
        "println(\"{sumAll([\"1\", \"x\", \"30\"])}\")\n" +
        "println(\"{trueCount([\"true\", \"no\", \"true\", \"false\"])}\")\n" +
        "println(\"{firstOrZero([\"7\"])} {firstOrZero([\"seven\"])} {firstOrZero([])}\")\n" +
        "def described = readInts([\"4\", \"four\"]) \\List.map({\n" +
        "  | #some(n) -> \"#some({n})\"\n" +
        "  | #invalidInt -> \"#invalidInt\"\n" +
        "})\n" +
        "println(List.fold(described, \"\", { acc, text -> acc ++ text ++ \" \" }))\n"
    runSource source |> assertRan "31\n2\n7 -1 0\n#some(4) #invalidInt \n"

[<Fact>]
let ``Run - a function generic in FromString reads a type of your own with its attached fromString`` () =
    let source =
        readAllSource +
        "type Point = (x: int, y: int)\n" +
        "\n" +
        "def Point.fromString: string -> #some(Point) | #invalidPoint = { s ->\n" +
        "  match Int.fromString(s)\n" +
        "    | #some(n) -> #some(Point(x = n, y = n * 2))\n" +
        "    | #invalidInt -> #invalidPoint\n" +
        "}\n" +
        "\n" +
        "def sumY: list(#some(Point) | #invalidPoint) -> int = { results ->\n" +
        "  results \\Option.values \\List.fold(0, { sum, p -> sum + p.y })\n" +
        "}\n" +
        "\n" +
        "def sumYs = { texts -> sumY(readAll(texts)) }\n" +
        "\n" +
        "println(\"{sumYs([\"4\", \"nope\", \"5\"])}\")\n"
    runSource source |> assertRan "18\n"

[<Fact>]
let ``Run - Int.toFloat turns an int into a float, called and piped`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def n = 3\n" +
        "def x: float = Int.toFloat(n) * 1.5\n" +
        "println(\"{x}\")\n" +
        "println(\"{Int.toFloat(-7)}\")\n" +
        "println(\"{Int.toFloat(0)}\")\n" +
        "println(\"{(7 \\toFloat) / (2 \\toFloat)}\")\n"
    runSource source |> assertRan "4.5\n-7.0\n0.0\n3.5\n"

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
        "def d = math.clamp(120, min = 0, max = 100)\n" +
        "def e = math.clamp(-5, min = 0, max = 100)\n" +
        "def f = math.pow(2, 8)\n" +
        "def g = math.pow(5, 0)\n" +
        "println(\"{a} {b} {c} {d} {e} {f} {g}\")\n"
    runSource source |> assertRan "42 3 7 100 0 256 1\n"

[<Fact>]
let ``Run - nanyx/math clamp takes its named arguments when piped`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "def x = 50\n" +
        "println(\"{x \\math.clamp(min = 0, max = 10)} {x \\math.clamp(min = 60, max = 70)} {x \\math.clamp(min = 0, max = 99)}\")\n"
    runSource source |> assertRan "10 60 50\n"

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
let ``Run - nanyx/math truncate drops the fraction towards zero`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "println(\"{math.truncate(3.9)} {math.truncate(-3.9)} {math.truncate(4.0)} {math.truncate(-0.5)}\")\n" +
        "println(\"{math.truncate(Int.toFloat(10) / 4.0)}\")\n"
    runSource source |> assertRan "3 -3 4 0\n2\n"

[<Fact>]
let ``Run - nanyx/math truncate of an infinity stops the program`` () =
    let source =
        "import nanyx/math as math\n" +
        "\n" +
        "dbg(math.truncate(1.0 / 0.0))\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("wasm trap: integer overflow", outcome.StandardError)

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
