/// A local function whose type requires a parameterised context is a function value that carries the context's
/// members, at the types it is called with: each call passes those the type checker found there (#1368).
module TranspilerWasmTests.Features.Contexts.LocalInstanceContexts

open Xunit
open TranspilerWasmTestHelpers

let private showContext = """context Show(a) = (show: a -> string)
type Point = (x: int, y: int)
type Size = (w: int)
def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }
def Size.show: Size -> string = { s -> "size {s.w}" }
def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }
"""

[<Fact>]
let ``Run - a local function is passed the instance each call finds, not the def around it's`` () =
    let source = showContext + """def local = { v ->
  def f: [Show(b)] b -> string = { x -> describe(x) }
  f(v) ++ " / " ++ f(Size(w = 3))
}
@entry
def main = { println(local(Point(x = 1, y = 2))) }
"""
    runSource (normalized source) |> assertRan "(1, 2) / size 3\n"

[<Fact>]
let ``Run - a local function that requires a parameterised context, in a def that requires none`` () =
    let source = showContext + """@entry
def main = {
  def f: [Show(a)] a -> string = { x -> describe(x) }
  println(f(Point(x = 1, y = 2)))
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\n"

[<Fact>]
let ``Run - a local function that writes its context as the def around it does`` () =
    let source = showContext + """def local: [Show(a)] a -> string = { v ->
  def f: [Show(a)] a -> string = { x -> "[" ++ describe(x) ++ "]" }
  f(v)
}
@entry
def main = {
  println(local(Point(x = 1, y = 2)))
  println(local(Size(w = 4)))
}
"""
    runSource (normalized source) |> assertRan "[(1, 2)]\n[size 4]\n"

[<Fact>]
let ``Run - a local function that requires a context without parameters too`` () =
    let source = showContext + """@entry
def main = {
  def say: [Console, Show(b)] b -> () = { x -> println(describe(x)) }
  say(Point(x = 1, y = 2))
  say(Size(w = 5))
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\nsize 5\n"

[<Fact>]
let ``Run - a call to a local function finds an instance a use puts in scope`` () =
    let source = showContext + """type Bare = (n: int)
@entry
def main = {
  def f: [Show(b)] b -> string = { x -> describe(x) }
  println(f(Point(x = 1, y = 2)))
  use Show(Bare)(show = { b -> "bare" }) in println(f(Bare(n = 1)))
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\nbare\n"

[<Fact>]
let ``Run - a local function that requires a context at a type it names`` () =
    let source = showContext + """@entry
def main = {
  def f: [Show(Size)] Size -> string = { x -> "<" ++ describe(x) ++ ">" }
  println(f(Size(w = 6)))
}
"""
    runSource (normalized source) |> assertRan "<size 6>\n"

[<Fact>]
let ``Run - a local function that does not write its type is inferred to require the context (#550)`` () =
    let source = showContext + """def local = { v ->
  def f = { x -> describe(x) }
  f(v)
}
@entry
def main = { println(local(Point(x = 1, y = 2))) }
"""
    runSource (normalized source) |> assertRan "(1, 2)\n"

[<Fact>]
let ``Run - a local function with an inferred context, called at two types in a def that requires none`` () =
    let source = showContext + """@entry
def main = {
  def f = { x -> "<" ++ describe(x) ++ ">" }
  println(f(Point(x = 1, y = 2)) ++ " / " ++ f(Size(w = 3)))
}
"""
    runSource (normalized source) |> assertRan "<(1, 2)> / <size 3>\n"
[<Fact>]
let ``Run - a local function that writes an operator's context is passed its instance at each type`` () =
    let source = """@entry
def main = {
  def f: [Add(a)] a, a -> a = { x, y -> x + y }
  println(f(1, 2))
  println(f(1.5, 2.0))
}
"""
    runSource (normalized source) |> assertRan "3\n3.5\n"

[<Fact>]
let ``Run - a local function with an inferred context that runs in Console, in a def that writes its type (#807)`` () =
    let source = "" + showContext + """def main: [Console] () -> () = {
  def f = { x -> println(describe(x)) }
  f(Point(x = 1, y = 2))
  def g = { x -> println(x) }
  g(3)
}
main()
"""
    runSource (normalized source) |> assertRan "(1, 2)\n3\n"

[<Fact>]
let ``Run - a local function with an inferred context that runs in Console, in a def that doesn't write its type`` () =
    let source = showContext + """@entry
def main = {
  def f = { x -> println(describe(x)) }
  f(Point(x = 1, y = 2))
  def g = { x -> println(x) }
  g(3)
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\n3\n"

[<Fact>]
let ``Run - a local function with an inferred context passed as a value, once the call settles its type (#1390)`` () =
    let source = "" + showContext + """def apply = { g, v -> g(v) }
def main: [Console] () -> () = {
  def p = { x -> println(x) }
  [1, 2] \list.map(p)
  def f = println
  [3] \list.map(f)
  def d = { x -> describe(x) }
  println(apply(d, Point(x = 1, y = 2)))
  def g = { x -> describe(x) }
  dbg(g)
  ()
}
main()
"""
    runSource (normalized source) |> withoutDbgLocations |> assertRan "1\n2\n3\n(1, 2)\n<function () -> string>\n"

[<Fact>]
let ``Run - a local function called at a variable of the def around it, in a lambda and passed as a value (#1397)`` () =
    let source = showContext + """def apply = { g, v -> g(v) }
def inLambda = { v ->
  def f = { x -> describe(x) }
  apply({ x -> f(x) }, v)
}
def asValue = { v ->
  def f: [Show(b)] b -> string = { x -> describe(x) }
  apply(f, v)
}
@entry
def main = {
  println(inLambda(Point(x = 1, y = 2)))
  println(asValue(Size(w = 3)))
}
"""
    runSource (normalized source) |> assertRan "(1, 2)\nsize 3\n"

[<Fact>]
let ``Run - a local function calls its parameterised context's members by their own names, with the instance a use loads (#1574)`` () =
    let source = """context Foo(a) = (
  bar: (a, a -> a)
  baz: (a, a -> a)
)

def outer: int -> string = { n ->
  def local: [Foo(a)] a -> a = { y -> bar(y, baz(y, y)) }
  use Foo(int)(bar = { x, y -> x + y }, baz = { x, y -> x * y }) in
  use Foo(string)(bar = { x, y -> x ++ y }, baz = { x, y -> y }) in
  "{local(n)} {local("s")}"
}

def single: int -> int = { n ->
  def local: [Foo(a)] a -> a = { y -> baz(y, y) }
  use Foo(int)(bar = { x, y -> x + y }, baz = { x, y -> x * y }) in local(n)
}

println(outer(3))
println("{single(3)}")
"""
    runSource (normalized source) |> assertRan "12 ss\n9\n"

[<Fact>]
let ``Run - a local function that requires Eq, Ord or Hash is passed the compiler's own at a built-in type`` () =
    // The compiler's own member is a generic function, passed at the member's type at the call (#2809).
    let source = """type Obj = protected (n: int, junk: int)
def Obj.`==` = { x: Obj, y: Obj -> x.n == y.n }
@entry
def main = {
  def same: [Eq(t)] t, t -> bool = { a, b -> a == b }
  println("{same(1, 1)} {same(1, 2)} {same("a", "a")} {same([1, 2], [1, 3])}")
  println(same(Obj(n = 1, junk = 1), Obj(n = 1, junk = 2)))
  def less: [Ord(t)] t, t -> bool = { a, b -> a < b }
  println("{less(1, 2)} {less("b", "a")} {less([1], [1, 0])}")
  def hashed: [Hash(t)] t -> bool = { a -> hash(a) == hash(a) }
  println(hashed(3))
}
"""
    runSource (normalized source) |> assertRan "true false true false\ntrue\ntrue false true\ntrue\n"
