module TranspilerWasmTests.Features.Records.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a nominal value over a primitive is made and taken apart with its type's name`` () =
    let source =
        normalized """import nanyx/console

type Money = private int

def Money.toString: Money -> string = { m ->
  def Money(pence) = m
  "{pence / 100}.{pence % 100}"
}

def describe: Money -> string = { m ->
  match m
    | Money(0) -> "free"
    | Money(p) -> "{p}p"
}

def main = {
  def price = Money(289)
  println(Money.toString(price))
  println(describe(price))
  println(describe(Money(0)))
  def (n, Money(q)) = (5, price)
  println("{n + q}")
}

main()
"""
    runSource source |> assertRan "2.89\n289p\nfree\n294\n"

[<Fact>]
let ``Run - tuple destructuring with def`` () =
    // Regression: `def (x, y) = point` was a parse error (#41).
    let source =
        normalized """import nanyx/console

def (a, b) = (1, 2)
@entry
def main = {
  def point = (10, 20)
  def (x, y) = point
  def (_, (name, age)) = (0, ("Ada", 36))
  println("{x},{y}")
  println("{a}+{b}={a + b}")
  println(name ++ " {age}")
}
"""
    runSource source |> assertRan "10,20\n1+2=3\nAda 36\n"

[<Fact>]
let ``Run - tuple destructuring with def and no brackets`` () =
    let source =
        normalized """import nanyx/console

def a, b = (1, 2)
@entry
def main = {
  def x, (name, age) = (10, ("Ada", 36))
  println("{a}+{b}={a + b}")
  println(name ++ " {age} {x}")
}
"""
    runSource source |> assertRan "1+2=3\nAda 36 10\n"

[<Fact>]
let ``Run - with record update`` () =
    // Regression: `alice with age = 31` was a parse error (#40).
    let source =
        normalized """import nanyx/console

type Point = (x: int, y: int)
def shiftedX: Point -> int = { p -> (p with x = p.x + 1).x }
@entry
def main = {
  def alice = (name = "Alice", age = 30)
  def older = alice with age = 31
  def renamed = older with name = "Al" ++ "ice", age = older.age + 1
  println("{older.age} {alice.age} {renamed.name} {renamed.age}")
  println("{shiftedX(Point(x = 3, y = 4))}")
}
"""
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
        normalized """import nanyx/console

type Point = (x: int, y: int)
def shift: Point -> Point = { q -> Point(x = q.x + 1, y = q.y + 1) }
def pair: (string, string) -> (first: string, second: string) = { a, b -> (first = a, second = b) }
@entry
def main = {
  def q = shift(Point(x = 3, y = 4))
  println("{q.x} {q.y} {shift(q).x}")
  def r = shift(shift(q))
  println("{r.x} {r.y}")
  println(pair("a", "b").second ++ pair("c", "d").first)
}
"""
    runSource source |> assertRan "4 5 5\n6 7\nbc\n"

[<Fact>]
let ``Run - nested record field access`` () =
    // Regression: `line.start.x` failed with "cannot determine layout for .x" (#30).
    let source =
        normalized """import nanyx/console

type Point = (x: int, y: int)
def origin = (x = 0, y = 0)
def unit = Point(x = 1, y = 1)
def main = {
  def line = (start = (x = 1, y = 2), finish = (x = 4, y = 6))
  def deep = (outer = (inner = (label = "deep")))
  dbg(line.finish.x - line.start.x)
  dbg(line.finish.y - line.start.y)
  println(deep.outer.inner.label)
  dbg(origin.x + origin.y)
  dbg(unit.x + unit.y)
}
main()
"""
    // The last line covers #31: a record built through a type constructor at the top level.
    runSource source |> assertRan "dbg: 3\ndbg: 4\ndeep\ndbg: 0\ndbg: 2\n"

[<Fact>]
let ``Run - fields of a record bound by a match pattern`` () =
    // The pattern's `a` shadows the top-level `a`, whose type is a tag rather than a record.
    let source =
        normalized """import nanyx/console

def a = #some((x = "ax", y = "ay"))
def b: typeof(a) = #some((x = "bx", y = "by"))

match b
| #some(a) ->
    def x = a.x
    println("x = {x}, y = {a.y}")
| _ -> println("none")

def main = {
  def person = #some((name = "pat", age = 3))
  match person, 1
  | #some(p), n -> println("{p.name} {n}")
  | _, _ -> println("none")
}

main()
"""
    runSource source |> assertRan "x = bx, y = by\npat 1\n"

[<Fact>]
let ``Run - a function reading fields takes any record that has them`` () =
    // The records' fields are in different places, so each read finds its field from the record's layout.
    let source =
        normalized """import nanyx/console

def label = { p -> p.name ++ " from " ++ p.city }
def show = {
  | #some(p), n -> println(p.name ++ " " ++ n)
  | _, _ -> println("none")
}
def twice = { p -> label(p) ++ "; " ++ label(p) }

println(label((age = 3, name = "a", city = "Leeds")))
println(label((city = "York", name = "b")))
show(#some((name = "c", age = 3)), "one")
show(#some((zip = 1, name = "d")), "two")
def home = (name = "e", city = "Bath")
def moved = home with city = "Ely"
println(label(moved))
println(twice((name = "f", city = "Hull", extra = 0)))
def people = [(name = "g", city = "X"), (name = "h", city = "Y")]
for people \ list.map(label) | s -> println(s)
"""
    runSource source |> assertRan "a from Leeds\nb from York\nc one\nd two\ne from Ely\nf from Hull; f from Hull\ng from X\nh from Y\n"

[<Fact>]
let ``Run - records, tuples and nominal values through the core IR`` () =
    // `nOf` is specialised for each record it is given (see CoreIrTests.fs).
    let source =
        normalized """import nanyx/console

type Person = (name: string, age: int)

type Money = private int

def older: Person -> Person = { p -> p with age = p.age + 1 }

def nOf = { r -> r.n }

def pence: Money -> int = { m ->
  def Money(p) = m
  p
}

def swapped: (int, int) -> (int, int) = { t ->
  match t
    | (a, b) -> (b, a)
}

def show = { n -> "{n * 1}" }

def main = {
  def ada = older(Person(name = "Ada", age = 36))
  println(ada.name)
  println(show(ada.age))
  println(show(nOf((n = 1)) + nOf((a = 20, n = 2)) + nOf((a = 30, b = 40, n = 3))))
  println(show(pence(Money(250))))
  println(show(swapped((1, 2)).0))
}

main()
"""
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

[<Fact>]
let ``Run - an open record parameter takes a record with more fields, and a named row keeps them`` () =
    let source = """
import nanyx/console

def g: (x: int, y: int, ...) -> int = { x, y -> x + y }

def moveRight: (x: int, ...r) -> (x: int, ...r) = { p -> p with x = p.x + 1 }

def main = {
  def data = (x = 1, y = 2, z = 3)
  println(g(data))
  println(g((x = 10, y = 20, label = "a")))
  def moved = moveRight((x = 1, y = 2, label = "a"))
  println("{moved.label} {moved.x} {moved.y}")
}

main()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3\n30\na 2 2\n"

[<Fact>]
let ``Run - a def that sums a field of its argument's records requires Add at the field's type`` () =
    // #958: the def's type has an open record in it, `list((price: b, ...a)) -> b`.
    let source = """
import nanyx/console

def total = { items -> items \list.map { .price } \list.sum }

def total2 = { items -> list.sum(list.map(items, { x -> x.price })) }

println(total([(price = 1), (price = 2)]))
println(total2([(price = 1.5, name = "a"), (price = 2.0, name = "b")]))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3\n3.5\n"
