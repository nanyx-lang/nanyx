module TranspilerWasmTests.Features.OperatorContexts.Run

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a def that does not write its type adds whatever type it is called with`` () =
    // `def add = { x, y -> x + y }` was `int, int -> int`. It is `[Add(a)] a, a -> a`, and `lerp` `[Num(a)]`.
    let source =
        normalized """import nanyx/console

type Money = protected (pence: int)
def Money.zero: Money = Money(pence = 0)
def Money.`+`: Money, Money -> Money = { p, q -> Money(pence = p.pence + q.pence) }
def Money.`-`: Money, Money -> Money = { p, q -> Money(pence = p.pence - q.pence) }
def Money.negate: Money -> Money = { p -> Money(pence = 0 - p.pence) }

def add = { x, y -> x + y }
def neg = { x -> -x }
def lerp = { a, b, t -> a + (b - a) * t }
def sumOfSquares = { xs -> xs \list.map { x -> x * x } \list.sum }
def local = { n ->
  def twice = { v -> v + v }
  twice(n) + 1
}
@entry
def main = {
  println("{add(1, 2)} {add(1.5, 2.25)} {add(Money(pence = 1), Money(pence = 2)).pence}")
  println("{neg(4)} {neg(2.5)} {neg(Money(pence = 7)).pence}")
  println("{lerp(0.0, 10.0, 0.5)} {lerp(0, 10, 2)}")
  println("{sumOfSquares([1, 2, 3])} {sumOfSquares([0.5, 1.5])} {local(5)}")
}
"""
    runSource source |> assertRan "3 3.75 3\n-4 -2.5 -7\n5.0 20\n14 2.5 11\n"

[<Fact>]
let ``Run - an operator required by its member is passed the instance of the prelude's context`` () =
    let source =
        normalized """import nanyx/console

def add: [`+`: a, a -> a] = { + }
def scale: [(`+`: a, a -> a) & (`*`: a, a -> a)] a, a -> a = { x, k -> x * k + x }
@entry
def main = {
  println("{add(1, 2)} {add(1.5, 2.25)} {scale(3, 2)} {scale(1.5, 2.0)}")
}
"""
    runSource source |> assertRan "3 3.75 9 4.5\n"

[<Fact>]
let ``Run - a def that compares values of two type variables calls each one's instance`` () =
    // #690: `same2` is `[Eq(a) & Eq(b)] a, a, b, b -> bool`, and each `==` calls the member of its own instance.
    let source =
        """
import nanyx/console

def same2 = { x, y, p, q -> x == y and p == q }

@entry
def main = {
    println(if same2(1, 1, "a", "a") then "T" else "F")
    println(if same2(1, 1, 2, 3) then "T" else "F")
}
"""
    runSource source |> assertRan "T\nF\n"

[<Fact>]
let ``Run - arithmetic on two type variables calls each one's instance`` () =
    let source =
        """
import nanyx/console

def both: [Add(a) & Add(b)] a, a, b, b -> (a, b) = { x, y, p, q -> (x + y, p - q) }
def same: [Eq(a) & Eq(b)] a, a, b, b -> bool = { x, y, p, q -> x == y and p != q }
def r = { x, y, p, q -> (x + y, p * q) }
def negated = { x, p -> (-x, -p) }
def withSum = { x, y, xs -> (x + y, xs \list.sum) }

@entry
def main = {
    def (i, f) = both(1, 2, 1.5, 2.0)
    def (j, g) = r(3, 4, 1.5, 2.0)
    def (k, h) = negated(1, 2.5)
    def (l, m) = withSum(3, 4, [1.5, 2.5])
    println("{i} {f} {j} {g} {k} {h} {l} {m}")
    println(if same(1, 1, "a", "b") then "T" else "F")
}
"""
    runSource source |> assertRan "3 -0.5 7 3.0 -1 -2.5 7 4.0\nT\n"

[<Fact>]
let ``Run - ++ on a type variable is read through Append, whose instances join strings, lists and types that attach ++`` () =
    let source =
        """
import nanyx/console

type Path = protected (parts: list(string))
def Path.`++`: Path, Path -> Path = { p, q -> Path(parts = p.parts ++ q.parts) }

def surround: [Append(a)] a, a -> a = { edge, x -> edge ++ x ++ edge }
def join = { x, y -> x ++ y }
@entry
def main = {
  println(surround("*", "hi"))
  println("{surround([0], [1, 2])} {join("a", "b")} {join([1.5], [2.5])}")
  println("{join(Path(parts = ["a"]), Path(parts = ["b", "c"])).parts}")
}
"""
    runSource source |> assertRan "*hi*\n[0, 1, 2, 0] ab [1.5, 2.5]\n[a, b, c]\n"
