module TranspilerWasmTests.Features.StdLib.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - text upper-cases and prints`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

println(string.uppercase("hello, nanyx!"))
"shout" \uppercase \println
"""
    runSource source |> assertRan "HELLO, NANYX!\nSHOUT\n"

[<Fact>]
let ``Run - text length, lowercase, trim, repeat and substring`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

dbg(string.length(""))
dbg(string.length("hello"))
println(string.lowercase("Hello, NANYX!"))
println("[" ++ string.trim("  padded ") ++ "]")
println("[" ++ string.trim("   ") ++ "]")
println(string.repeat("ab", 3))
println("[" ++ string.repeat("ab", 0) ++ "]")
println(string.substring("nanyx", start = 1, length = 3))
println(string.substring("nanyx", start = 3, length = 10))
"""
    runSource source |> assertRan "dbg: 0\ndbg: 5\nhello, nanyx!\n[padded]\n[]\nababab\n[]\nany\nyx\n"

[<Fact>]
let ``Run - text startsWith, endsWith and contains`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

def show: [Console] bool -> () = { b -> println(if b then "yes" else "no") }
show(string.startsWith("nanyx", "nan"))
show(string.startsWith("nanyx", "nyx"))
show(string.startsWith("nan", "nanyx"))
show(string.endsWith("nanyx", "nyx"))
show(string.endsWith("nanyx", "nan"))
show(string.contains("hello world", "o w"))
show(string.contains("hello world", "xyz"))
show(string.contains("hello", ""))
"""
    runSource source |> assertRan "yes\nno\nno\nyes\nno\nyes\nno\nyes\n"

[<Fact>]
let ``Run - text indexOf, lastIndexOf and count`` () =
    // `show` matches on the result: `dbg` of a tag returned from a call prints its raw encoding.
    let source =
        normalized """import nanyx/console
import nanyx/text

def show: [Console] (#some(int) | #notFound) -> () = {
  | #some(i) -> println("#some({i})")
  | #notFound -> println("#notFound")
}

@entry
def main = {
  show(string.indexOf("hello", "ll"))
  show(string.indexOf("abcabc", "c"))
  show(string.indexOf("hello", "xyz"))
  show(string.indexOf("lo", "hello"))
  show(string.indexOf("hello", ""))
  show(string.lastIndexOf("abcabc", "bc"))
  show(string.lastIndexOf("abc", ""))
  show(string.lastIndexOf("ab", "abc"))
  dbg(string.count("banana", "an"))
  dbg(string.count("aaaa", "aa"))
  dbg(string.count("abc", ""))
}
"""
    runSource source
    |> assertRan (
        withFinalNewline """#some(2)
#some(2)
#notFound
#notFound
#some(0)
#some(4)
#some(3)
#notFound
dbg: 2
dbg: 2
dbg: 0""")

[<Fact>]
let ``Run - text replace, removePrefix, removeSuffix, padStart and padEnd`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

println(string.replace("hello world", pattern = "o", replacement = "0"))
println(string.replace("a-b-c", pattern = "-", replacement = " - "))
println(string.replace("abc", pattern = "", replacement = "x"))
println(string.removePrefix("nanyx", "nan"))
println(string.removePrefix("nanyx", "nyx"))
println(string.removeSuffix("nanyx", "nyx"))
println(string.padStart("7", width = 3, fill = "0"))
println(string.padEnd("ab", width = 5, fill = "xy"))
println(string.padStart("long", width = 2, fill = "0"))
"""
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
        (withFinalNewline """show(string.isBlank(" x "))""")
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
        (withFinalNewline """dbg(string.length(string.concat(string.words("   "))))""")
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
        normalized """import nanyx/console
import nanyx/text

def parts = string.split("a,b,,c", on = ",")
println(string.join(parts, " | "))
println(string.join(string.split("no separators", on = "-"), "+"))
println(string.join(string.split("a--b", on = "--"), "/"))
println("[" ++ string.join([], ", ") ++ "]")
println(string.concat(["Hello", ", ", "World"]))
println(string.join(string.split("x", on = ""), "?"))
"""
    runSource source |> assertRan "a | b |  | c\nno separators\na/b\n[]\nHello, World\nx\n"

[<Fact>]
let ``Run - text splitOnce cuts a string at the first separator only`` () =
    let source =
        """
import nanyx/console
import nanyx/text

def show: string, string -> string = { s, on ->
  match s \splitOnce(on = on)
    | #some((before, after)) -> "[{before}|{after}]"
    | #notFound -> "not found"
}

println(show("a=b=c", "="))
println(show("=ab", "="))
println(show("ab=", "="))
println(show("1.0.0+build.5", "+build."))
println(show("abc", "="))
println(show("", "="))
println(show("abc", ""))
"""
    runSource source |> assertRan "[a|b=c]\n[|ab]\n[ab|]\n[1.0.0|5]\nnot found\nnot found\n[|abc]\n"

[<Fact>]
let ``Run - text split, substring, padStart, padEnd and replace take their named arguments when piped`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

println(string.join("a,b" \split(on = ","), "|"))
println("nanyx" \substring(start = 1, length = 3))
println("7" \padStart(width = 3, fill = "0"))
println(("ab" \padEnd(width = 5, fill = "xy")) ++ "]")
println("hello world" \replace(pattern = "o", replacement = "0"))
"""
    runSource source |> assertRan "a|b\nany\n007\nabxyx]\nhell0 w0rld\n"

[<Fact>]
let ``Run - a string's and a list's built-in length`` () =
    // No `import nanyx/text`: a string's length is a property of the string, not an attached definition.
    let source =
        normalized """import nanyx/console

def size: string -> int = { s -> s.length }
@entry
def main = {
  def words = ["nan", "yx", "lang"]
  def greeting = "hello"
  println("{greeting.length} {words.length} {size("")} {[].length}")
  def record = (length = "field")
  println(record.length)
}
"""
    runSource source |> assertRan "5 3 0 0\nfield\n"

[<Fact>]
let ``Run - a string's length on a lambda parameter typed by the call`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

def ap: string, (string -> int) -> int = { x, f -> f(x) }
@entry
def main = {
  def n = ap("bbb", { s -> s.length })
  println("{n}")
  def lengths = list.map(["bbb", "a"], { s -> s.length })
  println("{lengths}")
  println(string.join(list.sortBy(["bbb", "a", "cc"], { .length }), ","))
}
"""
    runSource source |> assertRan "3\n[3, 1]\na,cc,bbb\n"

[<Fact>]
let ``Run - Int.fromString reads decimal integers and rejects anything else`` () =
    let source =
        normalized """import nanyx/console

def show: [Console] (#some(int) | #invalidInt) -> () = {
  | #some(n) -> println("#some({n})")
  | #invalidInt -> println("#invalidInt")
}

show(int.fromString("42"))
show(int.fromString("+5"))
show(int.fromString("007"))
show(int.fromString("0"))
show(int.fromString("-17"))
show(int.fromString("70000"))
show(int.fromString(""))
show(int.fromString("-"))
show(int.fromString("4x2"))
show(int.fromString(" 1"))
show("12" \ int.fromString)
"""
    runSource source |> assertRan "#some(42)\n#some(5)\n#some(7)\n#some(0)\n#some(-17)\n#some(70000)\n#invalidInt\n#invalidInt\n#invalidInt\n#invalidInt\n#some(12)\n"

[<Fact>]
let ``Run - Int.fromString reads the whole range of int and rejects a number past it`` () =
    // A number past the range stopped the program with an overflow, as did the smallest int (#1015).
    let source = """
import nanyx/console

def show: [Console] string -> () = { s ->
  match int.fromString(s)
    | #some(n) -> println("#some({n})")
    | #invalidInt -> println("#invalidInt")
}

show("9223372036854775807")
show("-9223372036854775808")
show("9223372036854775808")
show("-9223372036854775809")
show("99999999999999999999")
show("-0")
println("still running")
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "#some(9223372036854775807)\n#some(-9223372036854775808)\n#invalidInt\n#invalidInt\n#invalidInt\n#some(0)\nstill running\n"

[<Fact>]
let ``Run - Bool.fromString reads true and false and rejects anything else`` () =
    let source =
        normalized """import nanyx/console

def show: [Console] (#some(bool) | #invalidBool) -> () = {
  | #some(b) -> println("#some({b})")
  | #invalidBool -> println("#invalidBool")
}

show(bool.fromString("true"))
show(bool.fromString("false"))
show(bool.fromString("True"))
show(bool.fromString(""))
show(bool.fromString(" true"))
show(bool.fromString("1"))
show("false" \ bool.fromString)
"""
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
        (withFinalNewline """show("0.5" \ float.fromString)""")
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
        normalized """import nanyx/console

def check: [Console] float -> () = { x ->
  match float.fromString(float.toString(x))
    | #some(y) -> println(if y == x then "{x} reads back" else "{x} reads back as {y}")
    | #invalidFloat -> println("{x} does not read back")
}

check(0.1 + 0.2)
check(1.0 / 3.0)
check(2.0 / 3.0)
check(-123.456)
check(6.02214076e23)
check(1.0e-7)
check(1.0e21)
check(5.0)
"""
    runSource source
    |> assertRan (
        withFinalNewline """0.30000000000000004 reads back
0.3333333333333333 reads back
0.6666666666666666 reads back
-123.456 reads back
6.02214076e23 reads back
1.0e-7 reads back
1.0e21 reads back
5.0 reads back""")

/// `readAll`, generic in the prelude's `FromString`, and a function of each result type that sums or counts what was read.
let private readAllSource =
    normalized """import nanyx/console

def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e) = { xs -> xs \list.map(fromString) }

def total: list(#some(int) | #invalidInt) -> int = { results ->
  results \somes \list.fold(0, { sum, n -> sum + n })
}

def countTrue: list(#some(bool) | #invalidBool) -> int = { results ->
  results \somes \list.filter({ b -> b }) \list.length
}

"""

[<Fact>]
let ``Run - a function generic in FromString reads ints and bools with the attached fromString`` () =
    let source =
        readAllSource +
        normalized """def sumAll = { texts -> total(readAll(texts)) }
def trueCount = { texts -> countTrue(readAll(texts)) }
def firstOrZero = { texts ->
  match readAll(texts)
    | [#some(n), ..._] -> n
    | [#invalidInt, ..._] -> -1
    | [] -> 0
}
def ofInts: list(#some(int) | r) -> list(#some(int) | r) = { xs -> xs }
def readInts = { texts -> ofInts(readAll(texts)) }

println("{sumAll(["1", "x", "30"])}")
println("{trueCount(["true", "no", "true", "false"])}")
println("{firstOrZero(["7"])} {firstOrZero(["seven"])} {firstOrZero([])}")
def described = readInts(["4", "four"]) \list.map({
  | #some(n) -> "#some({n})"
  | #invalidInt -> "#invalidInt"
})
println(list.fold(described, "", { acc, text -> acc ++ text ++ " " }))
"""
    runSource source |> assertRan "31\n2\n7 -1 0\n#some(4) #invalidInt \n"

[<Fact>]
let ``Run - a function generic in FromString reads a type of your own with its attached fromString`` () =
    let source =
        readAllSource +
        normalized """type Point = (x: int, y: int)

def Point.fromString: string -> #some(Point) | #invalidPoint = { s ->
  match int.fromString(s)
    | #some(n) -> #some(Point(x = n, y = n * 2))
    | #invalidInt -> #invalidPoint
}

def sumY: list(#some(Point) | #invalidPoint) -> int = { results ->
  results \somes \list.fold(0, { sum, p -> sum + p.y })
}

def sumYs = { texts -> sumY(readAll(texts)) }

println("{sumYs(["4", "nope", "5"])}")
"""
    runSource source |> assertRan "18\n"

[<Fact>]
let ``Run - Int.toFloat turns an int into a float, called and piped`` () =
    let source =
        normalized """import nanyx/console

def n = 3
def x: float = int.toFloat(n) * 1.5
println("{x}")
println("{int.toFloat(-7)}")
println("{int.toFloat(0)}")
println("{(7 \toFloat) / (2 \toFloat)}")
"""
    runSource source |> assertRan "4.5\n-7.0\n0.0\n3.5\n"

[<Fact>]
let ``Run - toString of an int, a float, a bool and a string, called and piped`` () =
    let source =
        normalized """import nanyx/console

println(int.toString(42))
println(int.toString(-17))
println(float.toString(1.5))
println(bool.toString(true))
println(bool.toString(false))
println(string.toString("Ada"))
println(7 \toString)
println(2.25 \toString)
println(false \toString)
println("text" \toString)
"""
    runSource source |> assertRan "42\n-17\n1.5\ntrue\nfalse\nAda\n7\n2.25\nfalse\ntext\n"

[<Fact>]
let ``Run - toString of each built-in type passed as a function`` () =
    let source =
        normalized """import nanyx/console
import nanyx/text

println(string.join([1, 2, 3] \list.map(int.toString), ", "))
println(string.join([0.5, 2.0] \list.map(float.toString), ", "))
println(string.join([true, false] \list.map(bool.toString), ", "))
println(string.join(["a", "b"] \list.map(string.toString), ", "))
println(string.join([4, 5] \map(int.toString), "+"))
"""
    runSource source |> assertRan "1, 2, 3\n0.5, 2.0\ntrue, false\na, b\n4+5\n"

[<Fact>]
let ``Run - a ToString of each built-in type is synthesized from the prelude's toString`` () =
    // Nothing names `Int.toString` here: the instance the type checker builds from it must still be compiled.
    let source =
        normalized """import nanyx/console

context ToString(a) = (toString: a -> string)
def label: [ToString(a)] a -> string = { x -> "<" ++ toString(x) ++ ">" }
println(label(42))
println(label(true))
println(label(1.5))
println(label("s"))
"""
    runSource source |> assertRan "<42>\n<true>\n<1.5>\n<s>\n"

[<Fact>]
let ``Run - nanyx/math abs, min, max, clamp and pow`` () =
    let source =
        normalized """import nanyx/console
import nanyx/math as math

def a = math.abs(-42)
def b = math.min(3, 7)
def c = math.max(3, 7)
def d = math.clamp(120, min = 0, max = 100)
def e = math.clamp(-5, min = 0, max = 100)
def f = math.pow(2, 8)
def g = math.pow(5, 0)
println("{a} {b} {c} {d} {e} {f} {g}")
"""
    runSource source |> assertRan "42 3 7 100 0 256 1\n"

[<Fact>]
let ``Run - nanyx/math abs takes an int or a float`` () =
    // #610: `abs` is `[Num(a) & Ord(a)] a -> a`; it took an `int` only.
    let source =
        normalized """import nanyx/console
import nanyx/math as math

println("{math.abs(-2.5)} {math.abs(2.5)} {math.abs(-42)} {math.abs(42)} {math.abs(0)} {math.abs(-0.0)}")
"""
    runSource source |> assertRan "2.5 2.5 42 42 0 0.0\n"

[<Fact>]
let ``Run - nanyx/math min and max take any type with an order`` () =
    // #610: `min` and `max` are `[Ord(a)] a, a -> a`.
    let source =
        normalized """import nanyx/console
import nanyx/math as math

println("{math.max(1.5, 2.0)} {math.max(3, 7)} {math.max("pear", "apple")}")
println("{math.min(1.5, 2.0)} {math.min(3, 7)} {math.min("pear", "apple")}")
println("{math.min(-1.5, -2.0)} {math.max(7, 7)}")
"""
    runSource source |> assertRan "2.0 7 pear\n1.5 3 apple\n-2.0 7\n"

[<Fact>]
let ``Run - nanyx/math minMax puts two values in ascending order`` () =
    // #489
    let source =
        """
import nanyx/console
import nanyx/math as math

def a, b = math.minMax(7, 3)
def c, d = math.minMax(3, 7)
def e, f = math.minMax(2.5, -1.5)
def g, h = math.minMax("pear", "apple")
def spread: [Num(a) & Ord(a)] a, a -> a = { p, q ->
  def low, high = math.minMax(p, q)
  high - low
}
println("{a} {b} {c} {d} {e} {f} {g} {h}")
println("{spread(9, 4)} {spread(0.25, 0.5)}")
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "3 7 3 7 -1.5 2.5 apple pear\n5 0.25\n"

[<Fact>]
let ``Run - nanyx/math clamp takes an int or a float`` () =
    // #610: `clamp` is `[Ord(a)] (a, min: a, max: a) -> a`.
    let source =
        normalized """import nanyx/console
import nanyx/math as math

println("{math.clamp(120, min = 0, max = 100)} {math.clamp(-5, min = 0, max = 100)} {math.clamp(5, min = 0, max = 100)}")
println("{math.clamp(1.5, min = 0.0, max = 1.0)} {math.clamp(-0.5, min = 0.0, max = 1.0)} {math.clamp(0.25, min = 0.0, max = 1.0)}")
println("{2.5 \math.clamp(min = 0.0, max = 1.0)} {"m" \math.clamp(min = "a", max = "f")}")
"""
    runSource source |> assertRan "100 0 5\n1.0 0.0 0.25\n1.0 f\n"

[<Fact>]
let ``Run - nanyx/math abs, min and max are called from generic functions`` () =
    // #610: a function that requires `Num` and `Ord` passes them on, and `x * 2` runs at `int` and at `float`.
    let source =
        normalized """import nanyx/console
import nanyx/math as math

def double: [Num(a)] a -> a = { x -> x * 2 }
def distance: [Num(a) & Ord(a)] a, a -> a = { p, q -> math.abs(p - q) }
def widest: [Ord(a)] a, a, a -> a = { p, q, r -> math.max(p, math.max(q, r)) }
println("{double(21)} {double(1.5)}")
println("{distance(3, 10)} {distance(1.5, 0.25)}")
println("{widest(3, 9, 4)} {widest(0.5, 0.25, 0.75)} {widest("b", "c", "a")}")
"""
    runSource source |> assertRan "42 3.0\n7 1.25\n9 0.75 c\n"

[<Fact>]
let ``Run - nanyx/math clamp takes its named arguments when piped`` () =
    let source =
        normalized """import nanyx/console
import nanyx/math as math

def x = 50
println("{x \math.clamp(min = 0, max = 10)} {x \math.clamp(min = 60, max = 70)} {x \math.clamp(min = 0, max = 99)}")
"""
    runSource source |> assertRan "10 60 50\n"

[<Fact>]
let ``Run - nanyx/math sqrt, floor, ceil and round`` () =
    let source =
        normalized """import nanyx/console
import nanyx/math as math

def s = math.sqrt(81.0)
def t = math.sqrt(2.0)
println("{s} {t}")
println("{math.floor(3.9)} {math.floor(-3.1)} {math.floor(4.0)}")
println("{math.ceil(3.1)} {math.ceil(-3.9)} {math.ceil(4.0)}")
println("{math.round(3.5)} {math.round(2.5)} {math.round(-2.5)} {math.round(2.4999)} {math.round(-0.4)}")
"""
    runSource source |> assertRan "9.0 1.4142135623730951\n3 -4 4\n4 -3 4\n4 3 -3 2 0\n"

[<Fact>]
let ``Run - nanyx/math truncate drops the fraction towards zero`` () =
    let source =
        normalized """import nanyx/console
import nanyx/math as math

println("{math.truncate(3.9)} {math.truncate(-3.9)} {math.truncate(4.0)} {math.truncate(-0.5)}")
println("{math.truncate(int.toFloat(10) / 4.0)}")
"""
    runSource source |> assertRan "3 -3 4 0\n2\n"

[<Fact>]
let ``Run - nanyx/math truncate of an infinity stops the program`` () =
    let source =
        normalized """import nanyx/math as math

dbg(math.truncate(1.0 / 0.0))
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("wasm trap: integer overflow", outcome.StandardError)

[<Fact>]
let ``Run - nanyx/math floor of a float out of the range of int stops the program`` () =
    let source =
        normalized """import nanyx/console
import nanyx/math as math

println("before")
def n = math.floor(1.0e19)
println("after {n}")
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains("wasm trap: integer overflow", outcome.StandardError)

[<Fact>]
let ``Run - nanyx/math round of NaN stops the program`` () =
    let source =
        normalized """import nanyx/math as math

dbg(math.round(0.0 / 0.0))
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("wasm trap: invalid conversion to integer", outcome.StandardError)
