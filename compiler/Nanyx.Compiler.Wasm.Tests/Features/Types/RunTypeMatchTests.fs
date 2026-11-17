/// A match on a `Type(a)` runs (see `CoreIrLowerTypeMatch`): at a known type it picks its arm at compile time, and at a
/// field's or an element's type, only known when the program runs, it matches on the type's description.
module TranspilerWasmTests.Features.Types.RunTypeMatches

open Xunit
open TranspilerWasmTestHelpers

let private describe =
    """def describe: a, type a -> string = { value, t ->
  match t
    | #int -> "{value + 1}"
    | #float -> "{value}"
    | #string -> "'" ++ value ++ "'"
    | #bool -> if value then "yes" else "no"
    | #unit -> "()"
    | #list(elem) -> "[" ++ (value \list.map { x -> describe(x, elem) } \string.join(", ")) ++ "]"
    | #set(elem) -> "a set of {set.length(value)}"
    | #array(elem) -> "an array of {array.length(value)}"
    | #map(k, v) -> "a map of {map.length(value)}"
    | #record(r) -> r.name ++ "(" ++ (r.fields \list.map { f -> f.name ++ " = " ++ describe(f.get(value), f.type) } \string.join(", ")) ++ ")"
    | #union(u) ->
        u.cases
          \list.map { c ->
            match c.get(value)
              | #some(payload) -> "#" ++ c.name ++ " " ++ describe(payload, c.type)
              | #none -> ""
          }
          \string.join("")
    | else -> "?"
}

"""

[<Fact>]
let ``Run - a Type match picks the arm for a known type, which narrows the value's type`` () =
    let source =
        describe
        + """println(describe(41, type int))
println(describe("a", type string))
println(describe([1, 2], type list(int)))
println(describe([[true], [false, true]], type list(list(bool))))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "42\n'a'\n[2, 3]\n[[yes], [no, yes]]\n"

[<Fact>]
let ``Run - a record's fields are walked in the order the type writes them, each at its own type`` () =
    let source =
        describe
        + """type Point = (x: int, y: int)

println(describe(Point(x = 1, y = 2), type Point))
println(describe((name = "n", tags = ["a", "b"], at = Point(x = 5, y = 6)), type (name: string, tags: list(string), at: Point)))
println(describe((n = 1, m = ["a" => 1], s = set.fromList([1, 2]), xs = Array[1, 2, 3], f = 1.5), type (n: int, m: map(string, int), s: set(int), xs: array(int), f: float)))
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "Point(x = 2, y = 3)\n(name = 'n', tags = ['a', 'b'], at = Point(x = 6, y = 7))\n(n = 2, m = a map of 1, s = a set of 2, xs = an array of 3, f = 1.5)\n"

[<Fact>]
let ``Run - a union's cases give the payload of a value of their case`` () =
    let source =
        describe
        + """type Shape = #circle(float) | #square(int) | #empty

def shapes: list(Shape) = [#square(3), #empty]

println(describe(#circle(1.5), type Shape))
println(describe(shapes, type list(Shape)))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#circle 1.5\n[#square 4, #empty ()]\n"

[<Fact>]
let ``Run - an arm at a type only known when the program runs gives a value of the narrowed type back`` () =
    let source =
        describe
        + """def zero: type a -> #some(a) | #none = { t ->
  match t
    | #int -> #some(0)
    | #string -> #some("")
    | #list(elem) -> #some([])
    | else -> #none
}

def zeros: a, type a -> string = { value, t ->
  match t
    | #record(r) ->
        r.fields
          \list.map { f ->
            match zero(f.type)
              | #some(z) -> f.name ++ " = " ++ describe(z, f.type)
              | #none -> f.name ++ " has none"
          }
          \string.join(", ")
    | else -> "not a record"
}

println(zeros((n = 3, s = "x", xs = [true], b = false), type (n: int, s: string, xs: list(bool), b: bool)))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "n = 1, s = '', xs = [], b has none\n"

[<Fact>]
let ``Run - a generic encoder writes JSON for any record, union, list and built-in type`` () =
    let source =
        """::dependency nanyx.json

import nanyx.json/json as json

def encodeAt: a, type a -> json.Json = { value, t ->
  match t
    | #int -> #number(int.toFloat(value))
    | #float -> #number(value)
    | #string -> #string(value)
    | #bool -> #bool(value)
    | #list(elem) -> #array(value \list.map { x -> encodeAt(x, elem) })
    | #record(r) -> #object(r.fields \list.map { f -> (f.name, encodeAt(f.get(value), f.type)) } \map.fromList)
    | #union(u) ->
        def nothing: json.Json = #null
        u.cases
          \list.fold(nothing, { found, c ->
            match c.get(value)
              | #some(payload) -> #object(map.fromList([("tag", #string(c.name)), ("value", encodeAt(payload, c.type))]))
              | #none -> found
          })
    | else -> #null
}

type Role = #admin | #member(int)

type User = (name: string, age: int, roles: list(Role), active: bool)

println(json.print(encodeAt(User(name = "Ada", age = 36, roles = [#admin, #member(2)], active = true), type User)))
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan
        """{"name":"Ada","age":36,"roles":[{"tag":"admin","value":null},{"tag":"member","value":2}],"active":true}
"""
