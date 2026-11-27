/// Square-bracket literals of any type with a `fromArray`: `[1, 2]` where an `Array(int)` is expected, and
/// `Array[1, 2]` (#1543).
module TranspilerWasmTests.Features.Collections.Literals

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a literal builds the array, set or map a def is declared as`` () =
    runSource
        """
def a: Array(int) = [1, 2, 3]
def s: set(int) = [1, 2, 2, 3]
def m: map(string, int) = ["a" => 1, "b" => 2]

println("{a}")
println(set.length(s))
println(map.get(m, "b"))
"""
    |> assertRan "[1, 2, 3]\n3\n#some(2)\n"

[<Fact>]
let ``Run - a type's name before the brackets builds that type`` () =
    runSource
        """
def a = Array[4, 5]
def s = set[1, 2, 2, 3]
def m = map["x" => 10]
def l = List[7, 8]

println("{a}")
println(set.length(s))
println(map.get(m, "x"))
println("{l}")
"""
    |> assertRan "[4, 5]\n3\n#some(10)\n[7, 8]\n"

[<Fact>]
let ``Run - a literal passed where a function takes an array builds one`` () =
    runSource
        """
import nanyx/array

def total: Array(int) -> int = { xs -> array.fold(xs, 0) { acc, x -> acc + x } }
def both: Array(int), set(int) -> int = { xs, s -> total(xs) + set.length(s) }

println(total([7, 8, 9]))
println(both([1], [5, 5, 6]))
"""
    |> assertRan "24\n3\n"

[<Fact>]
let ``Run - an empty literal is the expected type's empty value, and a list without one`` () =
    runSource
        """
import nanyx/array

def a: Array(int) = []
def s: set(string) = []
def m: map(string, int) = []
def e = Array[]
def l = []

println(array.length(a))
println(set.length(s))
println(map.length(m))
println(array.length(e))
println(list.length(l))
"""
    |> assertRan "0\n0\n0\n0\n0\n"

[<Fact>]
let ``Run - a list and an array spread into an array`` () =
    runSource
        """
def xs = [10, 20]
def ys = Array[30]
def c = Array[...xs, ...ys, 40]
def d: Array(int) = [0, ...ys, ...xs]
def l = [1, ...xs]

println("{c}")
println("{d}")
println("{l}")
"""
    |> assertRan "[10, 20, 30, 40]\n[0, 30, 10, 20]\n[1, 10, 20]\n"

[<Fact>]
let ``Run - a user's type with a fromArray has literals, and one that requires a context makes the literal require it`` () =
    runSource
        """
import nanyx/mstack

type Bag = protected (items: list(int))

def Bag.fromArray: Array(int) -> Bag = { xs -> Bag(items = list.fromArray(xs)) }

def count: Bag -> int = { b -> list.length(b.items) }

def bag: Bag = [1, 2, 3]

println(count(bag))
println(count(Bag[4]))

def top = memory {
  def stack = MStack[1, 2, 3]
  MStack.pop(stack)
}

println(top)
"""
    |> assertRan "3\n1\n#some(1)\n"

[<Fact>]
let ``Run - a literal builds the type a lambda's result, a record field and a spread's def expect`` () =
    // The expected type comes from a lambda's result, a function argument's result and a record field's type too (#1868).
    runSource
        """
type Box = (xs: Array(int))

def mk: () -> Array(int) = { -> [1, 2] }
def twice: (int -> Array(int)) -> Array(int) = { f -> f(2) }
def box: Box = (xs = [3, 4, 5])
def ys = [6]
def a: Array(int) = [...ys]

println(Array.length(mk()))
println(Array.length(twice({ n -> [n, n] })))
println(Array.length(box.xs))
println(Array.length(a))
"""
    |> assertRan "2\n2\n3\n1\n"
