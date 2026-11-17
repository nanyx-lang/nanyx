module TranspilerWasmTests.Features.Contexts.RunAttachedInstanceContexts

open Xunit
open TranspilerWasmTestHelpers

// A generic nominal type's attached `==`, `hash` and `toString` require the context of its elements. Each is the type's
// instance wherever one is read, passed the instances it requires as a call to it would find them.

/// `Box(a)`, with an `==`, a `hash` and a `toString` that each require the context of `a`, then `rest`.
let private withBox (rest: string) =
    let box = """
type Box(a) = protected (items: list(a))

def Box.`==`: [Eq(a)] Box(a), Box(a) -> bool = { p, q ->
  list.length(p.items) == list.length(q.items) and p.items == q.items
}

def Box.hash: [Hash(a)] Box(a) -> int = { b -> __listHashBy(0, b.items, hash) }

def Box.toString: [ToString(a)] Box(a) -> string = { b -> "Box{b.items}" }

def a = Box(items = [1])

def b = Box(items = [1])

def c = Box(items = [2])
"""
    (box + rest).ReplaceLineEndings "\n"

[<Fact>]
let ``Run - an attached toString that requires a context writes the value in a hole, and inside a list and a box`` () =
    withBox """
@entry
def main = {
  println("{a}")
  println(a)
  println("{[a, c]}")
  println("{Box(items = [a, c])}")
  println("{#some(c)}")
}
"""
    |> runSource
    |> assertRan "Box[1]\nBox[1]\n[Box[1], Box[2]]\nBox[Box[1], Box[2]]\n#some(Box[2])\n"

[<Fact>]
let ``Run - an attached == that requires a context compares lists, tuples and records of the type`` () =
    withBox """
@entry
def main = {
  println("{[a] == [b]} {[a] == [c]}")
  println("{(a, 1) == (b, 1)} {(a, 1) == (c, 1)}")
  println("{(x = a) == (x = b)} {(x = a) == (x = c)}")
}
"""
    |> runSource
    |> assertRan "true false\ntrue false\ntrue false\n"

[<Fact>]
let ``Run - an attached == and hash that require a context make the type a map key and a set element`` () =
    withBox """
@entry
def main = {
  def m = [a => "a", c => "c"]
  println("{m(b)} {m(c)}")
  println("{set.length(set.fromList([a, b, c]))}")
}
"""
    |> runSource
    |> assertRan "#some(a) #some(c)\n2\n"

[<Fact>]
let ``Run - an attached ==, hash and toString that require a context are passed to functions that require them`` () =
    withBox """
def same: [Eq(a)] a, a -> bool = { x, y -> x == y }

def hashed: [Hash(a)] a -> int = { x -> hash(x) }

def show: [ToString(a)] a -> string = { x -> "<{x}>" }

def boxesEqual: [Eq(a)] Box(a), Box(a) -> bool = { x, y -> same(x, y) }

@entry
def main = {
  println("{same(a, b)} {same(a, c)} {boxesEqual(a, b)}")
  println(hashed(a) == hashed(b))
  println(show(a))
  println(show(Box(items = [a])))
}
"""
    |> runSource
    |> assertRan "true false true\ntrue\n<Box[1]>\n<Box[Box[1]]>\n"

[<Fact>]
let ``Run - a queue is written, compared and used as a map key by its own toString, == and hash`` () =
    let source = """
import nanyx/queue

@entry
def main = {
  def q = Queue[1, 2] \enqueue(3)
  def r = Queue.fromList([1, 2, 3])
  def m = [q => "found"]
  println("{q} {q == r} {m(r)}")
  println("{[q]}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "Queue[1, 2, 3] true #some(found)\n[Queue[1, 2, 3]]\n"
