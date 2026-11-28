/// A pipe to `toString`, `tryFormat`, `hash` or `compare` from a value whose type attaches none, a structure or a unit
/// quantity, calls the prelude's wrapper of the member, so it finds the instance the compiler derives, or the one in
/// scope (#3227, #3230).
module TranspilerWasmTests.Features.Contexts.RunPipedDerivedMembers

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a pipe to toString writes a list, a tuple, a tag, a record and a unit quantity by their derived ToString`` () =
    runSource
        """println([1, 2] \toString)
println((1, "a") \toString)
println(#some(3) \toString)
println((x = 1, y = 2) \toString)
println(1500ms \toString)
"""
    |> assertRan "[1, 2]\n(1, a)\n#some(3)\n(x = 1, y = 2)\n1500ms\n"

[<Fact>]
let ``Run - a pipe to tryFormat on a unit quantity writes it by its derived Format`` () =
    runSource
        """match 1500ms \tryFormat(".1f s")
  | #some(text) -> println(text)
  | #badFormat(bad) -> println(bad.expected)
match 1500ms \tryFormat(".1f parsec")
  | #some(text) -> println(text)
  | #badFormat(bad) -> println(bad.at)
"""
    |> assertRan "1.5s\n4\n"

[<Fact>]
let ``Run - in a function that requires ToString, a pipe to toString uses the instance it requires`` () =
    runSource
        """type Cents = protected int

def show: [ToString(a)] a -> string = { x -> x \toString }

def label = {
  use ToString(Cents)(toString = { c -> "cents" })
  show(Cents(5))
}

println(show([3]))
println(label())
"""
    |> assertRan "[3]\ncents\n"

[<Fact>]
let ``Run - a toString of the module's own is called, not the wrapper, and a type's attached one too`` () =
    runSource
        """type Point = (x: int, y: int)
def Point.toString = { p -> "P{p.x}" }
def toString = { xs: list(int) -> "mine" }

println([1] \toString)
println(Point(x = 4, y = 5) \toString)
println(3 \toString)
"""
    |> assertRan "mine\nP4\n3\n"

[<Fact>]
let ``Run - a pipe to hash or compare on a structure finds its derived Hash or Ord`` () =
    runSource
        """println([1, 2] \hash == __hashOf([1, 2]))
println((x = 1, y = "a") \hash == __hashOf((x = 1, y = "a")))
println(#some(3) \hash == __hashOf(#some(3)))
println((1, 2) \compare((1, 3)))
println([2, 1] \compare([1, 9]))
"""
    |> assertRan "true\ntrue\ntrue\n#less\n#greater\n"

[<Fact>]
let ``Run - a hash or compare of the module's own is called, not the wrapper`` () =
    runSource
        """def hash = { xs: list(int) -> 7 }
def compare = { a: (int, int), b: (int, int) -> #equal }

println([1] \hash)
println((1, 2) \compare((3, 4)))
"""
    |> assertRan "7\n#equal\n"
