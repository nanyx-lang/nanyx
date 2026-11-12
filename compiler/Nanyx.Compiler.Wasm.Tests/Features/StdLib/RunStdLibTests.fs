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
        "println(string.uppercase(\"hello, nanyx!\"))\n" +
        "\"shout\" \\uppercase \\println\n"
    runSource source |> assertRan "HELLO, NANYX!\nSHOUT\n"

[<Fact>]
let ``Run - text length, lowercase, trim, repeat and substring`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "dbg(string.length(\"\"))\n" +
        "dbg(string.length(\"hello\"))\n" +
        "println(string.lowercase(\"Hello, NANYX!\"))\n" +
        "println(\"[\" ++ string.trim(\"  padded \") ++ \"]\")\n" +
        "println(\"[\" ++ string.trim(\"   \") ++ \"]\")\n" +
        "println(string.repeat(\"ab\", 3))\n" +
        "println(\"[\" ++ string.repeat(\"ab\", 0) ++ \"]\")\n" +
        "println(string.substring(\"nanyx\", start = 1, length = 3))\n" +
        "println(string.substring(\"nanyx\", start = 3, length = 10))\n"
    runSource source |> assertRan "dbg: 0\ndbg: 5\nhello, nanyx!\n[padded]\n[]\nababab\n[]\nany\nyx\n"

[<Fact>]
let ``Run - text startsWith, endsWith and contains`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: [Console] bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "show(string.startsWith(\"nanyx\", \"nan\"))\n" +
        "show(string.startsWith(\"nanyx\", \"nyx\"))\n" +
        "show(string.startsWith(\"nan\", \"nanyx\"))\n" +
        "show(string.endsWith(\"nanyx\", \"nyx\"))\n" +
        "show(string.endsWith(\"nanyx\", \"nan\"))\n" +
        "show(string.contains(\"hello world\", \"o w\"))\n" +
        "show(string.contains(\"hello world\", \"xyz\"))\n" +
        "show(string.contains(\"hello\", \"\"))\n"
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
        "  show(string.indexOf(\"hello\", \"ll\"))\n" +
        "  show(string.indexOf(\"abcabc\", \"c\"))\n" +
        "  show(string.indexOf(\"hello\", \"xyz\"))\n" +
        "  show(string.indexOf(\"lo\", \"hello\"))\n" +
        "  show(string.indexOf(\"hello\", \"\"))\n" +
        "  show(string.lastIndexOf(\"abcabc\", \"bc\"))\n" +
        "  show(string.lastIndexOf(\"abc\", \"\"))\n" +
        "  show(string.lastIndexOf(\"ab\", \"abc\"))\n" +
        "  dbg(string.count(\"banana\", \"an\"))\n" +
        "  dbg(string.count(\"aaaa\", \"aa\"))\n" +
        "  dbg(string.count(\"abc\", \"\"))\n" +
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
        "println(string.replace(\"hello world\", pattern = \"o\", replacement = \"0\"))\n" +
        "println(string.replace(\"a-b-c\", pattern = \"-\", replacement = \" - \"))\n" +
        "println(string.replace(\"abc\", pattern = \"\", replacement = \"x\"))\n" +
        "println(string.removePrefix(\"nanyx\", \"nan\"))\n" +
        "println(string.removePrefix(\"nanyx\", \"nyx\"))\n" +
        "println(string.removeSuffix(\"nanyx\", \"nyx\"))\n" +
        "println(string.padStart(\"7\", width = 3, fill = \"0\"))\n" +
        "println(string.padEnd(\"ab\", width = 5, fill = \"xy\"))\n" +
        "println(string.padStart(\"long\", width = 2, fill = \"0\"))\n"
    runSource source |> assertRan "hell0 w0rld\na - b - c\nabc\nyx\nnanyx\nna\n007\nabxyx\nlong\n"

[<Fact>]
let ``Run - text trimStart, trimEnd, isEmpty and isBlank`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: [Console] bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "println(\"[\" ++ string.trimStart(\" \t x \") ++ \"]\")\n" +
        "println(\"[\" ++ string.trimEnd(\" x \r\n\") ++ \"]\")\n" +
        "show(string.isEmpty(\"\"))\n" +
        "show(string.isEmpty(\" \"))\n" +
        "show(string.isBlank(\" \t\n\"))\n" +
        "show(string.isBlank(\" x \"))\n"
    runSource source |> assertRan "[x ]\n[ x]\nyes\nno\nyes\nno\n"

[<Fact>]
let ``Run - text lines and words`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(string.join(string.lines(\"a\nb\r\nc\n\"), \"|\"))\n" +
        "println(string.join(string.lines(\"a\n\nb\"), \"|\"))\n" +
        "dbg(string.length(string.concat(string.lines(\"\"))))\n" +
        "println(string.join(string.words(\"  one two\tthree\n\"), \"|\"))\n" +
        "dbg(string.length(string.concat(string.words(\"   \"))))\n"
    runSource source |> assertRan "a|b|c\na||b\ndbg: 0\none|two|three\ndbg: 0\n"

[<Fact>]
let ``Run - text chars takes a string apart by code point`` () =
    let source = """import nanyx/console
import nanyx/text

println(string.join("abc" \chars, "|"))
println(string.join(string.chars("héllo"), "|"))
println("{list.length(string.chars("héllo"))} {string.length("héllo")}")
println("{list.length(string.chars(""))}")
println(string.join(string.chars("a€b😀c"), "|"))
println("{string.concat(string.chars("a€b😀c")) == "a€b😀c"}")
"""
    runSource source |> assertRan "a|b|c\nh|é|l|l|o\n5 6\n0\na|€|b|😀|c\ntrue\n"

[<Fact>]
let ``Run - text reverse turns a string around by code point`` () =
    let source = """import nanyx/console
import nanyx/text

println(string.reverse("abc"))
println("héllo" \reverse)
println(string.reverse("a€b😀c"))
println(string.reverse("é€😀"))
println("[" ++ string.reverse("") ++ "]")
println(string.reverse("x"))
"""
    runSource source |> assertRan "cba\nolléh\nc😀b€a\n😀€é\n[]\nx\n"

[<Fact>]
let ``Run - text split, join and concat`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def parts = string.split(\"a,b,,c\", on = \",\")\n" +
        "println(string.join(parts, \" | \"))\n" +
        "println(string.join(string.split(\"no separators\", on = \"-\"), \"+\"))\n" +
        "println(string.join(string.split(\"a--b\", on = \"--\"), \"/\"))\n" +
        "println(\"[\" ++ string.join([], \", \") ++ \"]\")\n" +
        "println(string.concat([\"Hello\", \", \", \"World\"]))\n" +
        "println(string.join(string.split(\"x\", on = \"\"), \"?\"))\n"
    runSource source |> assertRan "a | b |  | c\nno separators\na/b\n[]\nHello, World\nx\n"

[<Fact>]
let ``Run - text split, substring, padStart, padEnd and replace take their named arguments when piped`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(string.join(\"a,b\" \\split(on = \",\"), \"|\"))\n" +
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
let ``Run - a string's length on a lambda parameter typed by the call`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def ap: string, (string -> int) -> int = { x, f -> f(x) }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def n = ap(\"bbb\", { s -> s.length })\n" +
        "  println(\"{n}\")\n" +
        "  def lengths = list.map([\"bbb\", \"a\"], { s -> s.length })\n" +
        "  println(\"{lengths}\")\n" +
        "  println(string.join(list.sortBy([\"bbb\", \"a\", \"cc\"], { .length }), \",\"))\n" +
        "}\n"
    runSource source |> assertRan "3\n[3, 1]\na,cc,bbb\n"

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
        "show(int.fromString(\"42\"))\n" +
        "show(int.fromString(\"+5\"))\n" +
        "show(int.fromString(\"007\"))\n" +
        "show(int.fromString(\"0\"))\n" +
        "show(int.fromString(\"-17\"))\n" +
        "show(int.fromString(\"70000\"))\n" +
        "show(int.fromString(\"\"))\n" +
        "show(int.fromString(\"-\"))\n" +
        "show(int.fromString(\"4x2\"))\n" +
        "show(int.fromString(\" 1\"))\n" +
        "show(\"12\" \\ int.fromString)\n"
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
        "show(bool.fromString(\"true\"))\n" +
        "show(bool.fromString(\"false\"))\n" +
        "show(bool.fromString(\"True\"))\n" +
        "show(bool.fromString(\"\"))\n" +
        "show(bool.fromString(\" true\"))\n" +
        "show(bool.fromString(\"1\"))\n" +
        "show(\"false\" \\ bool.fromString)\n"
    runSource source |> assertRan "#some(true)\n#some(false)\n#invalidBool\n#invalidBool\n#invalidBool\n#invalidBool\n#some(false)\n"

[<Fact>]
let ``Run - Float.fromString reads decimals and rejects anything else`` () =
    let inputs =
        [ "1.5"; "-0.25"; "42"; "+3.0"; ".5"; "5."; "007.50"; "1.5e3"; "2E-3"; "1.0e-7"; "1.0e21"; "-0.0"
          "1e400"; "1e-400"; "NaN"; "Infinity"; "-Infinity"
          ""; "-"; "abc"; "1.2.3"; "."; "e5"; "1e"; "1e+"; " 1.5"; "1.5 "; "1,5"; "--1"; "1x" ]
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: [Console] (#some(float) | #invalidFloat) -> () = {\n" +
        "  | #some(x) -> println(\"#some({x})\")\n" +
        "  | #invalidFloat -> println(\"#invalidFloat\")\n" +
        "}\n" +
        "\n" +
        (inputs |> List.map (fun input -> $"show(float.fromString(\"{input}\"))\n") |> String.concat "") +
        "show(\"0.5\" \\ float.fromString)\n"
    let expected =
        [ "#some(1.5)"; "#some(-0.25)"; "#some(42.0)"; "#some(3.0)"; "#some(0.5)"; "#some(5.0)"; "#some(7.5)"
          "#some(1500.0)"; "#some(0.002)"; "#some(1.0e-7)"; "#some(1.0e21)"; "#some(-0.0)"
          "#some(Infinity)"; "#some(0.0)"; "#some(NaN)"; "#some(Infinity)"; "#some(-Infinity)" ]
        @ List.replicate 13 "#invalidFloat"
        @ [ "#some(0.5)" ]
    runSource source |> assertRan (expected |> List.map (fun line -> line + "\n") |> String.concat "")

[<Fact>]
let ``Run - Float.fromString reads back the floats Float.toString writes`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def check: [Console] float -> () = { x ->\n" +
        "  match float.fromString(float.toString(x))\n" +
        "    | #some(y) -> println(if y == x then \"{x} reads back\" else \"{x} reads back as {y}\")\n" +
        "    | #invalidFloat -> println(\"{x} does not read back\")\n" +
        "}\n" +
        "\n" +
        "check(0.1 + 0.2)\n" +
        "check(1.0 / 3.0)\n" +
        "check(2.0 / 3.0)\n" +
        "check(-123.456)\n" +
        "check(6.02214076e23)\n" +
        "check(1.0e-7)\n" +
        "check(1.0e21)\n" +
        "check(5.0)\n"
    runSource source
    |> assertRan (
        "0.30000000000000004 reads back\n0.3333333333333333 reads back\n0.6666666666666666 reads back\n" +
        "-123.456 reads back\n6.02214076e23 reads back\n1.0e-7 reads back\n1.0e21 reads back\n5.0 reads back\n")

/// `readAll`, generic in the prelude's `FromString`, and a function of each result type that sums or counts what was read.
let private readAllSource =
    "import nanyx/console\n" +
    "\n" +
    "def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e) = { xs -> xs \\list.map(fromString) }\n" +
    "\n" +
    "def total: list(#some(int) | #invalidInt) -> int = { results ->\n" +
    "  results \\valuesOption \\list.fold(0, { sum, n -> sum + n })\n" +
    "}\n" +
    "\n" +
    "def countTrue: list(#some(bool) | #invalidBool) -> int = { results ->\n" +
    "  results \\valuesOption \\list.filter({ b -> b }) \\list.length\n" +
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
        "def described = readInts([\"4\", \"four\"]) \\list.map({\n" +
        "  | #some(n) -> \"#some({n})\"\n" +
        "  | #invalidInt -> \"#invalidInt\"\n" +
        "})\n" +
        "println(list.fold(described, \"\", { acc, text -> acc ++ text ++ \" \" }))\n"
    runSource source |> assertRan "31\n2\n7 -1 0\n#some(4) #invalidInt \n"

[<Fact>]
let ``Run - a function generic in FromString reads a type of your own with its attached fromString`` () =
    let source =
        readAllSource +
        "type Point = (x: int, y: int)\n" +
        "\n" +
        "def Point.fromString: string -> #some(Point) | #invalidPoint = { s ->\n" +
        "  match int.fromString(s)\n" +
        "    | #some(n) -> #some(Point(x = n, y = n * 2))\n" +
        "    | #invalidInt -> #invalidPoint\n" +
        "}\n" +
        "\n" +
        "def sumY: list(#some(Point) | #invalidPoint) -> int = { results ->\n" +
        "  results \\valuesOption \\list.fold(0, { sum, p -> sum + p.y })\n" +
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
        "def x: float = int.toFloat(n) * 1.5\n" +
        "println(\"{x}\")\n" +
        "println(\"{int.toFloat(-7)}\")\n" +
        "println(\"{int.toFloat(0)}\")\n" +
        "println(\"{(7 \\toFloat) / (2 \\toFloat)}\")\n"
    runSource source |> assertRan "4.5\n-7.0\n0.0\n3.5\n"

[<Fact>]
let ``Run - toString of an int, a float, a bool and a string, called and piped`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "println(int.toString(42))\n" +
        "println(int.toString(-17))\n" +
        "println(float.toString(1.5))\n" +
        "println(bool.toString(true))\n" +
        "println(bool.toString(false))\n" +
        "println(string.toString(\"Ada\"))\n" +
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
        "println(string.join([1, 2, 3] \\list.map(int.toString), \", \"))\n" +
        "println(string.join([0.5, 2.0] \\list.map(float.toString), \", \"))\n" +
        "println(string.join([true, false] \\list.map(bool.toString), \", \"))\n" +
        "println(string.join([\"a\", \"b\"] \\list.map(string.toString), \", \"))\n" +
        "println(string.join([4, 5] \\map(int.toString), \"+\"))\n"
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
let ``Run - nanyx/math abs takes an int or a float`` () =
    // #610: `abs` is `[Num(a) & Ord(a)] a -> a`; it took an `int` only.
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "println(\"{math.abs(-2.5)} {math.abs(2.5)} {math.abs(-42)} {math.abs(42)} {math.abs(0)} {math.abs(-0.0)}\")\n"
    runSource source |> assertRan "2.5 2.5 42 42 0 0.0\n"

[<Fact>]
let ``Run - nanyx/math min and max take any type with an order`` () =
    // #610: `min` and `max` are `[Ord(a)] a, a -> a`.
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "println(\"{math.max(1.5, 2.0)} {math.max(3, 7)} {math.max(\"pear\", \"apple\")}\")\n" +
        "println(\"{math.min(1.5, 2.0)} {math.min(3, 7)} {math.min(\"pear\", \"apple\")}\")\n" +
        "println(\"{math.min(-1.5, -2.0)} {math.max(7, 7)}\")\n"
    runSource source |> assertRan "2.0 7 pear\n1.5 3 apple\n-2.0 7\n"

[<Fact>]
let ``Run - nanyx/math clamp takes an int or a float`` () =
    // #610: `clamp` is `[Ord(a)] (a, min: a, max: a) -> a`.
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "println(\"{math.clamp(120, min = 0, max = 100)} {math.clamp(-5, min = 0, max = 100)} {math.clamp(5, min = 0, max = 100)}\")\n" +
        "println(\"{math.clamp(1.5, min = 0.0, max = 1.0)} {math.clamp(-0.5, min = 0.0, max = 1.0)} {math.clamp(0.25, min = 0.0, max = 1.0)}\")\n" +
        "println(\"{2.5 \\math.clamp(min = 0.0, max = 1.0)} {\"m\" \\math.clamp(min = \"a\", max = \"f\")}\")\n"
    runSource source |> assertRan "100 0 5\n1.0 0.0 0.25\n1.0 f\n"

[<Fact>]
let ``Run - nanyx/math abs, min and max are called from generic functions`` () =
    // #610: a function that requires `Num` and `Ord` passes them on, and `x * 2` runs at `int` and at `float`.
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "def double: [Num(a)] a -> a = { x -> x * 2 }\n" +
        "def distance: [Num(a) & Ord(a)] a, a -> a = { p, q -> math.abs(p - q) }\n" +
        "def widest: [Ord(a)] a, a, a -> a = { p, q, r -> math.max(p, math.max(q, r)) }\n" +
        "println(\"{double(21)} {double(1.5)}\")\n" +
        "println(\"{distance(3, 10)} {distance(1.5, 0.25)}\")\n" +
        "println(\"{widest(3, 9, 4)} {widest(0.5, 0.25, 0.75)} {widest(\"b\", \"c\", \"a\")}\")\n"
    runSource source |> assertRan "42 3.0\n7 1.25\n9 0.75 c\n"

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
    runSource source |> assertRan "9.0 1.4142135623730951\n3 -4 4\n4 -3 4\n4 3 -3 2 0\n"

[<Fact>]
let ``Run - nanyx/math truncate drops the fraction towards zero`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "println(\"{math.truncate(3.9)} {math.truncate(-3.9)} {math.truncate(4.0)} {math.truncate(-0.5)}\")\n" +
        "println(\"{math.truncate(int.toFloat(10) / 4.0)}\")\n"
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
