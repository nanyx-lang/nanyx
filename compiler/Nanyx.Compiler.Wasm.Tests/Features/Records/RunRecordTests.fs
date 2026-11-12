module TranspilerWasmTests.Features.Records.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a nominal value over a primitive is made and taken apart with its type's name`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type $Money = private int\n" +
        "\n" +
        "def $Money.toString: $Money -> string = { m ->\n" +
        "  def $Money(pence) = m\n" +
        "  \"{pence / 100}.{pence % 100}\"\n" +
        "}\n" +
        "\n" +
        "def describe: $Money -> string = { m ->\n" +
        "  match m\n" +
        "    | $Money(0) -> \"free\"\n" +
        "    | $Money(p) -> \"{p}p\"\n" +
        "}\n" +
        "\n" +
        "def main = {\n" +
        "  def price = $Money(289)\n" +
        "  println($Money.toString(price))\n" +
        "  println(describe(price))\n" +
        "  println(describe($Money(0)))\n" +
        "  def (n, $Money(q)) = (5, price)\n" +
        "  println(\"{n + q}\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "2.89\n289p\nfree\n294\n"

[<Fact>]
let ``Run - tuple destructuring with def`` () =
    // Regression: `def (x, y) = point` was a parse error (#41).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def (a, b) = (1, 2)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def point = (10, 20)\n" +
        "  def (x, y) = point\n" +
        "  def (_, (name, age)) = (0, (\"Ada\", 36))\n" +
        "  println(\"{x},{y}\")\n" +
        "  println(\"{a}+{b}={a + b}\")\n" +
        "  println(name ++ \" {age}\")\n" +
        "}\n"
    runSource source |> assertRan "10,20\n1+2=3\nAda 36\n"

[<Fact>]
let ``Run - tuple destructuring with def and no brackets`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def a, b = (1, 2)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def x, (name, age) = (10, (\"Ada\", 36))\n" +
        "  println(\"{a}+{b}={a + b}\")\n" +
        "  println(name ++ \" {age} {x}\")\n" +
        "}\n"
    runSource source |> assertRan "1+2=3\nAda 36 10\n"

[<Fact>]
let ``Run - with record update`` () =
    // Regression: `alice with age = 31` was a parse error (#40).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def shiftedX: Point -> int = { p -> (p with x = p.x + 1).x }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def alice = (name = \"Alice\", age = 30)\n" +
        "  def older = alice with age = 31\n" +
        "  def renamed = older with name = \"Al\" ++ \"ice\", age = older.age + 1\n" +
        "  println(\"{older.age} {alice.age} {renamed.name} {renamed.age}\")\n" +
        "  println(\"{shiftedX(Point(x = 3, y = 4))}\")\n" +
        "}\n"
    runSource source |> assertRan "31 30 Alice 32\n4\n"

[<Fact>]
let ``Run - with takes its updates in parentheses, as the records tour writes them`` () =
    // Regression: `user with (` read `with` as an identifier (#204).
    let source =
        """
import nanyx/console

def user = (name = "Alice", age = 30, email = "old@example.com")
def updatedUser = user with (
  age = 31
  email = "alice@example.com"
)
def renamed = user with (name = "Bob")

def line = (
  range = (
    start = (character = 0)
  )
)

def updatedLine = line with (
  range = line.range with (
    start = line.range.start with (
      character = 4
    )
  )
)

@entry
def main = {
  println("{updatedUser.name} {updatedUser.age} {updatedUser.email}")
  println("{renamed.name} {renamed.age}")
  println("{updatedLine.range.start.character} {line.range.start.character}")
}
"""
    runSource (source.TrimStart().ReplaceLineEndings "\n") |> assertRan "Alice 31 alice@example.com\nBob 30\n4 0\n"

[<Fact>]
let ``Run - member access on the result of a call`` () =
    // Regression: `shift(...)` returned a Point but `q.x` on it was "Record 'q' has no member 'x'" (#60).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def shift: Point -> Point = { q -> Point(x = q.x + 1, y = q.y + 1) }\n" +
        "def pair: (string, string) -> (first: string, second: string) = { a, b -> (first = a, second = b) }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def q = shift(Point(x = 3, y = 4))\n" +
        "  println(\"{q.x} {q.y} {shift(q).x}\")\n" +
        "  def r = shift(shift(q))\n" +
        "  println(\"{r.x} {r.y}\")\n" +
        "  println(pair(\"a\", \"b\").second ++ pair(\"c\", \"d\").first)\n" +
        "}\n"
    runSource source |> assertRan "4 5 5\n6 7\nbc\n"

[<Fact>]
let ``Run - nested record field access`` () =
    // Regression: `line.start.x` failed with "cannot determine layout for .x" (#30).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def origin = (x = 0, y = 0)\n" +
        "def unit = Point(x = 1, y = 1)\n" +
        "def main = {\n" +
        "  def line = (start = (x = 1, y = 2), finish = (x = 4, y = 6))\n" +
        "  def deep = (outer = (inner = (label = \"deep\")))\n" +
        "  dbg(line.finish.x - line.start.x)\n" +
        "  dbg(line.finish.y - line.start.y)\n" +
        "  println(deep.outer.inner.label)\n" +
        "  dbg(origin.x + origin.y)\n" +
        "  dbg(unit.x + unit.y)\n" +
        "}\n" +
        "main()\n"
    // The last line covers #31: a record built through a type constructor at the top level.
    runSource source |> assertRan "dbg: 3\ndbg: 4\ndeep\ndbg: 0\ndbg: 2\n"

[<Fact>]
let ``Run - fields of a record bound by a match pattern`` () =
    // The pattern's `a` shadows the top-level `a`, whose type is a tag rather than a record.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def a = #some((x = \"ax\", y = \"ay\"))\n" +
        "def b: typeof(a) = #some((x = \"bx\", y = \"by\"))\n" +
        "\n" +
        "match b\n" +
        "| #some(a) ->\n" +
        "    def x = a.x\n" +
        "    println(\"x = {x}, y = {a.y}\")\n" +
        "| _ -> println(\"none\")\n" +
        "\n" +
        "def main = {\n" +
        "  def person = #some((name = \"pat\", age = 3))\n" +
        "  match person, 1\n" +
        "  | #some(p), n -> println(\"{p.name} {n}\")\n" +
        "  | _, _ -> println(\"none\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "x = bx, y = by\npat 1\n"

[<Fact>]
let ``Run - a function reading fields takes any record that has them`` () =
    // The records' fields are in different places, so each read finds its field from the record's layout.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def label = { p -> p.name ++ \" from \" ++ p.city }\n" +
        "def show = {\n" +
        "  | #some(p), n -> println(p.name ++ \" \" ++ n)\n" +
        "  | _, _ -> println(\"none\")\n" +
        "}\n" +
        "def twice = { p -> label(p) ++ \"; \" ++ label(p) }\n" +
        "\n" +
        "println(label((age = 3, name = \"a\", city = \"Leeds\")))\n" +
        "println(label((city = \"York\", name = \"b\")))\n" +
        "show(#some((name = \"c\", age = 3)), \"one\")\n" +
        "show(#some((zip = 1, name = \"d\")), \"two\")\n" +
        "def home = (name = \"e\", city = \"Bath\")\n" +
        "def moved = home with city = \"Ely\"\n" +
        "println(label(moved))\n" +
        "println(twice((name = \"f\", city = \"Hull\", extra = 0)))\n" +
        "def people = [(name = \"g\", city = \"X\"), (name = \"h\", city = \"Y\")]\n" +
        "for people \\ list.map(label) | s -> println(s)\n"
    runSource source |> assertRan "a from Leeds\nb from York\nc one\nd two\ne from Ely\nf from Hull; f from Hull\ng from X\nh from Y\n"

[<Fact>]
let ``Run - records, tuples and nominal values through the core IR`` () =
    // `nOf` is specialised for each record it is given (see CoreIrTests.fs).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Person = (name: string, age: int)\n" +
        "\n" +
        "type $Money = private int\n" +
        "\n" +
        "def older: Person -> Person = { p -> p with age = p.age + 1 }\n" +
        "\n" +
        "def nOf = { r -> r.n }\n" +
        "\n" +
        "def pence: $Money -> int = { m ->\n" +
        "  def $Money(p) = m\n" +
        "  p\n" +
        "}\n" +
        "\n" +
        "def swapped: (int, int) -> (int, int) = { t ->\n" +
        "  match t\n" +
        "    | (a, b) -> (b, a)\n" +
        "}\n" +
        "\n" +
        "def show = { n -> \"{n * 1}\" }\n" +
        "\n" +
        "def main = {\n" +
        "  def ada = older(Person(name = \"Ada\", age = 36))\n" +
        "  println(ada.name)\n" +
        "  println(show(ada.age))\n" +
        "  println(show(nOf((n = 1)) + nOf((a = 20, n = 2)) + nOf((a = 30, b = 40, n = 3))))\n" +
        "  println(show(pence($Money(250))))\n" +
        "  println(show(swapped((1, 2)).0))\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "Ada\n37\n6\n250\n2\n"

[<Fact>]
let ``Run - a field read on a lambda's parameter uses the record type from the type checker`` () =
    // Regression for #250: the backend had no record layout for `p`.
    let source =
        """
import nanyx/console

type P = (x: int)

def big: list(P) -> list(P) = { ps -> list.filter(ps, { p -> p.x > 1 }) }

println("{list.length(big([P(x = 1), P(x = 2)]))}")
"""
    runSource source |> assertRan "1\n"

[<Fact>]
let ``Run - field reads on the parameters of lambdas passed to list functions`` () =
    let source =
        """
import nanyx/console
import nanyx/text

type Product = (name: string, price: int, stock: int)

def products = [Product(name = "a", price = 2, stock = 3), Product(name = "b", price = 5, stock = 1)]

println(string.join(list.map(products, { p -> p.name }), ","))
println("{list.length(products \list.filter({ p -> p.price < 3 }))}")
println("{list.fold(products, 0, { total, p -> total + p.price * p.stock })}")
println(string.join(list.map([1, 2], { n -> "{n}" }), ", "))
"""
    runSource source |> assertRan "a,b\n1\n11\n1, 2\n"

[<Fact>]
let ``Run - a field read on a for loop's element inside an interpolation`` () =
    let source =
        """
import nanyx/console

type Product = (name: string, price: int)

for [Product(name = "a", price = 2), Product(name = "b", price = 5)] | p ->
  println("{p.name}: {p.price}")
"""
    runSource source |> assertRan "a: 2\nb: 5\n"
