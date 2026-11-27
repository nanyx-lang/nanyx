module TranspilerWasmTests.Features.Equality.Run

open Xunit
open TranspilerWasmTestHelpers


[<Fact>]
let ``Run - records and tuples compare field by field`` () =
    comparing
        [ "type Point = (x: int, y: int)" ]
        [ "(x = 1, y = 2) == (x = 1, y = 2)"
          "(x = 1, y = 2) == (x = 1, y = 3)"
          "Point(x = 1, y = 2) != Point(x = 1, y = 3)"
          """(1, s) == (1, "aa")"""
          """(1, s) == (1, "ab")"""
          "(f = 1.5, g = [2.0]) == (f = 1.5, g = [2.0])"
          "(f = 1.5, g = [2.0]) == (f = 1.5, g = [2.5])" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nF\nT\nF\n"

[<Fact>]
let ``Run - lists compare element by element`` () =
    comparing
        [ "def empty: list(int) = []" ]
        [ "[1, 2, 3] == [1, 2, 3]"
          "[1, 2] == [1, 2, 3]"
          "[1, 2, 3] == [1, 2]"
          """[[s], ["b"]] == [["aa"], ["b"]]"""
          "empty == [1]"
          "empty == []" ]
    |> runSource
    |> assertRan "T\nF\nF\nT\nF\nT\n"

[<Fact>]
let ``Run - tags compare by name, then payload by its type`` () =
    // A payload on the heap is compared by what it holds, not where it is (#526).
    comparing
        [ "type Point = (x: int, y: int)"
          "type Mixed = #i(int) | #s(string) | #p(Point) | #nothing"
          "def eqMixed: Mixed, Mixed -> bool = { a, b -> a == b }"
          "def eqResult: #ok(list(string)) | #error(string), #ok(list(string)) | #error(string) -> bool = { a, b -> a == b }" ]
        [ """#some(s) == #some("aa")"""
          """#some(s) == #some("ab")"""
          """eqMixed(#s(s), #s("aa"))"""
          "eqMixed(#p(Point(x = 1, y = 2)), #p(Point(x = 1, y = 2)))"
          "eqMixed(#s(s), #p(Point(x = 1, y = 2)))"
          "eqMixed(#nothing, #nothing)"
          "eqMixed(#nothing, #i(1))"
          "eqMixed(#i(2), #i(2))"
          """eqResult(#ok([s]), #ok(["aa"]))"""
          """eqResult(#ok([s]), #error("aa"))""" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nF\nT\nF\nT\nT\nF\n"

[<Fact>]
let ``Each type compared gets one equality function, and its parts their own`` () =
    let wat =
        transpileWat (
            withFinalNewline """def a: list((x: int, y: string)), list((x: int, y: string)) -> bool = { p, q -> p == q }
def b: list((x: int, y: string)), list((x: int, y: string)) -> bool = { p, q -> p != q }""")
    Assert.Equal(1, countOccurrences "(func $__nanyx_equals_list_" wat)
    Assert.Equal(1, countOccurrences "(func $__nanyx_equals_rec_" wat)

[<Fact>]
let ``Run - a function that compares its type variable compares each type it is called with`` () =
    comparing
        [ "type Point = (x: int, y: int)"
          "def same = { x, y -> x == y }"
          "def differ = { x, y -> x != y }"
          "def holds = { xs, x -> xs \\list.any({ y -> y == x }) }"
          "def written: [Eq(a)] a, list(a) -> bool = { x, xs -> x != x or xs == [x] }" ]
        [ "same(1, 1)"
          """same(s, "ab")"""
          "same(Point(x = 1, y = 2), Point(x = 1, y = 2))"
          "same([1.5], [1.5])"
          """same(#some(s), #some("aa"))"""
          "differ(1, 1)"
          """differ([s], ["b"])"""
          "holds([1, 2, 3], 3)"
          "written(1, [1])"
          """list.distinct([s, "aa", "b"]) == ["aa", "b"]""" ]
    |> runSource
    |> assertRan "T\nF\nT\nT\nT\nF\nT\nT\nT\nT\n"

[<Fact>]
let ``Run - a local function compares at each type it is called with`` () =
    comparing
        [ "def both = { v, w ->"
          "  def same = { x, y -> x == y }"
          "  same(v, w)"
          "}" ]
        [ "both(1, 1)"
          "both(1, 2)"
          "both(#red, #red)" ]
    |> runSource
    |> assertRan "T\nF\nT\n"

[<Fact>]
let ``Run - a local function compares strings, lists and tags on the heap by what they hold`` () =
    // One closure for each type it is used at, each comparing as that type does, and freeing what it is given (#616).
    """
type Shape = #circle(string) | #square(int)

def show = { b -> if b then "T" else "F" }

@entry
def main = {
  def s = "a" ++ "a"
  def same = { x, y -> x == y }
  def differ = { x, y -> x != y }
  def circle: Shape = #circle(s)
  def square: Shape = #square(1)
  println(show(same(s, "aa")))
  println(show(same(s, "ab")))
  println(show(same([s], ["aa"])))
  println(show(same([["a"], [s]], [["a"], ["aa"]])))
  println(show(same([["a"]], [["b"]])))
  println(show(same(#circle(s), #circle("aa"))))
  println(show(same(circle, square)))
  println(show(differ(s, "aa")))
  println(show(differ([s], ["b"])))
  println(show(same(1, 1)))
  println(show(same(2, 3)))
}
"""
    |> runSource
    |> assertRan "T\nF\nT\nT\nF\nT\nF\nF\nT\nT\nF\n"

[<Fact>]
let ``Run - a lambda compares through a local function it captures, at each type it uses it at`` () =
    // The lambda captures a closure of `same` for each type it calls it at, each freeing what it is given (#751).
    """
def show = { b -> if b then "T" else "F" }

@entry
def main = {
  def same = { x, y -> x == y }
  def check = { a -> same(a, "aa") }
  println(show(check("a" ++ "a")))
  println(show(check("a" ++ "b")))
  def all = { s, xs, nested -> show(same(s, "aa")) ++ show(same(xs, ["b" ++ "c"])) ++ show(same(nested, [["d"], ["e" ++ "f"]])) }
  println(all("a" ++ "a", ["b" ++ "c"], [["d"], ["ef"]]))
  println(all("ab", ["c"], [["d"], ["e"]]))
}
"""
    |> runSource
    |> assertRan "T\nF\nTTT\nFFF\n"

[<Fact>]
let ``Run - a lambda in a lambda, or a local generic function, compares through a local function it captures`` () =
    // Captured through two lambdas, by a generic lambda at each type it is used at, and by another local generic
    // function, whose closure at each type makes the captured one's at that type (#751).
    """
def show = { b -> if b then "T" else "F" }

@entry
def main = {
  def eq = { x, y -> x == y }
  def same = { x, y -> eq(x, y) }
  def outer = { s -> { t -> same(s, t) } }
  def inner = outer("x" ++ "y")
  println(show(inner("xy")))
  println(show(inner("yx")))
  def twice = { v -> eq(v, v) }
  println(show(twice("p" ++ "q")) ++ show(twice(["r" ++ "s"])) ++ show(twice([["t"]])))
  println(show(same(["a" ++ "b"], ["ab"])) ++ show(same([["a"]], [["b"]])))
}
"""
    |> runSource
    |> assertRan "T\nF\nTTT\nTF\n"

[<Fact>]
let ``Run - a function that compares its type variable uses the == attached to a type`` () =
    comparing
        [ "type Email = protected string"
          "def Email.`==`: Email, Email -> bool = { a, b -> true }"
          "def same = { x, y -> x == y }"
          "def differ = { x, y -> x != y }" ]
        [ """same(Email("a"), Email("b"))"""
          """differ(Email("a"), Email("b"))""" ]
    |> runSource
    |> assertRan "T\nF\n"

[<Fact>]
let ``Run - comparing with a tag takes a value of any union that has it`` () =
    comparing
        [ "def isNone = { o -> o == #none }" ]
        [ "isNone(#none)"
          "isNone(#some(3))"
          "isNone(#some(s))" ]
    |> runSource
    |> assertRan "T\nF\nF\n"

[<Fact>]
let ``Run - comparing an open union with a tag compares its payload with the == attached to the payload's type`` () =
    // The tags the union does not name, and two different tags, are compared by the compiler's own == (#830).
    comparing
        [ "type Email = protected string"
          "def Email.`==`: Email, Email -> bool = { a, b -> true }"
          """def same = { o -> o == #some(Email("a")) }"""
          """def differ = { o -> o != #some(Email("a")) }"""
          """def either = { o -> o == #some(Email("a")) or o == #other(2) }"""
          """def sameClosed: #some(Email) -> bool = { o -> o == #some(Email("a")) }"""
          "def opened: [Eq(a)] (#x(a) | r), (#x(a) | r) -> bool = { v, w -> v == w }" ]
        [ """same(#some(Email("b")))"""
          "same(#none)"
          """differ(#some(Email("b")))"""
          "differ(#other(1))"
          "either(#other(2))"
          "either(#other(3))"
          """sameClosed(#some(Email("b")))"""
          """opened(#x(Email("a")), #x(Email("b")))"""
          "opened(#x(1), #y)" ]
    |> runSource
    |> assertRan "T\nF\nF\nT\nT\nF\nT\nT\nF\n"

[<Fact>]
let ``Run - a call that requires Eq of an open union is passed the == derived through the tags it names`` () =
    // As `v == w` compares the payloads with Email's `==`, so does a function the union is passed to (#2751).
    comparing
        [ "type Email = protected string"
          "def Email.`==`: Email, Email -> bool = { a, b -> string.lowercase(a) == string.lowercase(b) }"
          "def Email.hash: Email -> int = { e -> string.hash(string.lowercase(e)) }"
          "def same: [Eq(a)] a, a -> bool = { x, y -> x == y }"
          "def called: (#x(Email) | r), (#x(Email) | r) -> bool = { v, w -> same(v, w) }"
          "def compared: (#x(Email) | r), (#x(Email) | r) -> bool = { v, w -> v == w }" ]
        [ """called(#x(Email("A@b")), #x(Email("a@B")))"""
          """compared(#x(Email("A@b")), #x(Email("a@B")))"""
          """called(#x(Email("a@b")), #x(Email("c@d")))"""
          """called(#x(Email("a@b")), #y)"""
          "called(#y, #y)" ]
    |> runSource
    |> assertRan "T\nT\nF\nF\nT\n"

[<Fact>]
let ``Run - a function that passes a union on to one that compares it with a tag compares at each type`` () =
    comparing
        [ "def isNone = { o -> o == #none }"
          "def viaCaller = { o -> isNone(o) }"
          "def isNoneTyped: #none | r -> bool = { o -> o == #none }" ]
        [ "viaCaller(#none)"
          "viaCaller(#some([1, 2]))"
          "isNoneTyped(#none)"
          "isNoneTyped(#some(s))" ]
    |> runSource
    |> assertRan "T\nF\nT\nF\n"

[<Fact>]
let ``Run - a concrete comparison uses the == attached to a nominal type`` () =
    comparing
        [ "type Point = protected (x: int, y: int)"
          "def Point.`==`: Point, Point -> bool = { p, q -> p.x == q.x }" ]
        [ "Point(x = 1, y = 2) == Point(x = 1, y = 3)"
          "Point(x = 1, y = 2) != Point(x = 1, y = 3)"
          "Point(x = 1, y = 2) == Point(x = 2, y = 2)" ]
    |> runSource
    |> assertRan "T\nF\nF\n"

[<Fact>]
let ``Run - a list of a type with its own == is compared through it, element by element`` () =
    comparing
        [ "type Email = protected string"
          "def Email.`==`: Email, Email -> bool = { a, b -> true }"
          "def same: [Eq(a)] list(a), list(a) -> bool = { xs, ys -> xs == ys }" ]
        [ """[Email("a")] == [Email("b")]"""
          """[Email("a")] != [Email("b")]"""
          """[Email("a")] == [Email("a"), Email("b")]"""
          """same([Email("a")], [Email("b")])"""
          """["a"] == ["b"]""" ]
    |> runSource
    |> assertRan "T\nF\nF\nT\nF\n"

[<Fact>]
let ``Run - a type's inferred == is passed to a function that requires Eq`` () =
    // `Obj.==` is inferred to require `Eq(a)`, with no type written (#2814).
    let source =
        """type Obj(a) = protected (members: list((string, a)))

def Obj.`==` = { x: Obj(a), y: Obj(a) ->
  list.all(x.members, { m -> list.contains(y.members, m) }) and x.members.length == y.members.length
}

def sameT: [Eq(t)] t, t -> bool = { a, b -> a == b }

@entry
def main = {
  def p = Obj(members = [("a", 1), ("b", 2)])
  def q = Obj(members = [("b", 2), ("a", 1)])
  println(sameT(p, q))
  println(sameT(p, Obj(members = [("a", 1)])))
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "true\nfalse\n"