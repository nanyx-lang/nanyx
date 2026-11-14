module TranspilerWasmTests.Features.Dbg.Run

open Xunit
open TranspilerWasmTestHelpers

/// Runs `source` with its memory checked, and asserts it printed `expected`. Both are normalised to `\n`, and the line break that starts `expected` is left out.
let private printsAs (expected: string) (source: string) =
    runSource (source.ReplaceLineEndings "\n") |> assertRan (expected.ReplaceLineEndings("\n").TrimStart('\n'))

[<Fact>]
let ``Run - dbg as a pipe prints each kind of value`` () =
    """
"hi" \dbg
5 \dbg
#ok \dbg
#some(3) \dbg
"""
    |> printsAs """
dbg: "hi"
dbg: 5
dbg: #ok
dbg: #some(3)
"""

[<Fact>]
let ``Run - dbg piped from a call in a for loop`` () =
    """
def show = { i -> "{i}" }

for 1..3 | i ->
    show(i) \dbg
"""
    |> printsAs """
dbg: "1"
dbg: "2"
dbg: "3"
"""

[<Fact>]
let ``Run - dbg prints ints, floats and bools as their literals`` () =
    """
def flag = 1 < 2
dbg(42)
dbg(-7)
dbg(1.5)
dbg(2.0)
dbg(flag)
dbg(false)
"""
    |> printsAs """
dbg: 42
dbg: -7
dbg: 1.5
dbg: 2.0
dbg: true
dbg: false
"""

[<Fact>]
let ``Run - dbg prints the bool a comparison gives`` () =
    """
type Point = (x: int, y: int)
def a = Point(x = 0, y = 0)
def b = Point(x = 0, y = 0)
dbg(a == b)
dbg(1 == 2)
"""
    |> printsAs "dbg: true\ndbg: false\n"

[<Fact>]
let ``Run - dbg prints a string quoted and escaped`` () =
    """
dbg("Ada")
dbg("say \"hi\"\n\ttab\\")
dbg("")
"""
    |> printsAs """
dbg: "Ada"
dbg: "say \"hi\"\n\ttab\\"
dbg: ""
"""

[<Fact>]
let ``Run - dbg prints a tag with its payload`` () =
    """
def flag = 1 < 2
dbg(#some(5))
dbg(#some("hi"))
dbg(#some(flag))
dbg(if flag then #yes(false) else #no)
dbg(#pair(1, "a"))
dbg(#rect(width = 1.0, height = 2.0))
dbg(#done(()))
"""
    |> printsAs """
dbg: #some(5)
dbg: #some("hi")
dbg: #some(true)
dbg: #yes(false)
dbg: #pair(1, "a")
dbg: #rect(height = 2.0, width = 1.0)
dbg: #done()
"""

[<Fact>]
let ``Run - dbg prints tuples, records and unit`` () =
    """
dbg((1, "a"))
dbg((name = "Ada", age = 36))
dbg(())
"""
    |> printsAs """
dbg: (1, "a")
dbg: (age = 36, name = "Ada")
dbg: ()
"""

[<Fact>]
let ``Run - dbg prints lists, maps and sets`` () =
    """
def empty: list(int) = []
dbg([1, 2])
dbg(empty)
dbg(["tea" => 3, "cake" => 4])
def none: map(string, int) = map.empty
dbg(none)
dbg(set.fromList([3, 1, 3]))
def noInts: set(int) = set.empty
dbg(noInts)
"""
    |> printsAs """
dbg: [1, 2]
dbg: []
dbg: Map["tea" => 3, "cake" => 4]
dbg: Map[]
dbg: Set[3, 1]
dbg: Set[]
"""

[<Fact>]
let ``Run - dbg prints a nested value part by part`` () =
    """
dbg([#some((1, ["x"])), #none])
dbg((point = (x = 1.5, y = -2.0), tags = [#a, #b]))
"""
    |> printsAs """
dbg: [#some(1, ["x"]), #none]
dbg: (point = (x = 1.5, y = -2.0), tags = [#a, #b])
"""

[<Fact>]
let ``Run - dbg prints a value of a type that holds itself`` () =
    """
type Tree = #leaf | #node(Tree, int, Tree)
def tree: Tree = #node(#leaf, 1, #node(#leaf, 2, #leaf))
dbg(tree)
"""
    |> printsAs "dbg: #node(#leaf, 1, #node(#leaf, 2, #leaf))\n"

[<Fact>]
let ``Run - dbg prints a nominal value by its name around its shape`` () =
    """
type Money = protected int
type Point = protected (x: int, y: int)
dbg(Money(250))
dbg(Point(x = 1, y = 2))
"""
    |> printsAs "dbg: Money(250)\ndbg: Point(x = 1, y = 2)\n"

[<Fact>]
let ``Run - dbg prints a protected value's shape outside its module`` () =
    """
module ids =
  export type UserId = private int
  export def make = { n: int -> UserId(n) }

dbg(ids.make(42))
"""
    |> printsAs "dbg: ids.UserId(42)\n"

[<Fact>]
let ``Run - dbg ignores the ToString a type has`` () =
    """
type Money = protected int
def Money.toString: Money -> string = { m ->
  def Money(pence) = m
  "£{pence}"
}
def price = Money(250)
dbg(price)
dbg("{price}")
"""
    |> printsAs "dbg: Money(250)\ndbg: \"£250\"\n"

[<Fact>]
let ``Run - dbg prints a value of a generic function's type at the type it is called at`` () =
    """
def trace = { x -> dbg(x) }
trace((trace(1), trace([#ok])))
"""
    |> printsAs """
dbg: 1
dbg: [#ok]
dbg: (1, [#ok])
"""

[<Fact>]
let ``Run - dbg prints a for loop's elements and a match's bindings`` () =
    """
for [[1], [2, 3]] | xs ->
  dbg(xs)
match #some((1, "x"))
  | #some(pair) -> dbg(pair)
  | #none -> (0, "")
"""
    |> printsAs """
dbg: [1]
dbg: [2, 3]
dbg: (1, "x")
"""

[<Fact>]
let ``Run - dbg of a function says that it is one and of what type, and gives the function`` () =
    """
def increment = { n -> n + 1 }
dbg(increment)
def again = dbg({ n: int -> "{n * 2}" })
dbg(again(4))
"""
    |> printsAs """
dbg: <function int -> int>
dbg: <function int -> string>
dbg: "8"
"""

[<Fact>]
let ``Run - dbg gives back the value it prints`` () =
    """
def total = dbg(3) + 1
dbg(total)
def names = dbg(["a", "b"])
dbg(names)
"""
    |> printsAs """
dbg: 3
dbg: 4
dbg: ["a", "b"]
dbg: ["a", "b"]
"""
