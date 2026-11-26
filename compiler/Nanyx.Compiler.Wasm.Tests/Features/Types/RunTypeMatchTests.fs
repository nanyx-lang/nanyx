/// A match on a `Type(a)` runs (see `CoreIrLowerTypeMatch`): at a known type it picks its arm at compile time, and at a
/// field's or an element's type, only known when the program runs, it matches on the type's description.
module TranspilerWasmTests.Features.Types.RunTypeMatches

open Xunit
open TranspilerWasmTestHelpers

let private describe =
    """def describe: a, Type(a) -> string = { value, t ->
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

/// Counts the ints in a value, through its cases' and fields' `get`s.
let private count =
    """def count: a, Type(a) -> int = { value, t ->
  match t
    | #int -> 1
    | #list(elem) -> list.sum(list.map(value, { x -> count(x, elem) }))
    | #record(r) -> list.sum(list.map(r.fields, { f -> count(f.get(value), f.type) }))
    | #union(u) ->
        list.sum(
          list.map(u.cases, { c ->
            match c.get(value)
              | #some(p) -> count(p, c.type)
              | #none -> 0
          })
        )
    | else -> 0
}

"""

[<Fact>]
let ``Run - a recursive union's case gives its payload`` () =
    let source =
        count
        + """type R = #rarr(list(R)) | #n(int)

def r: R = #rarr([#n(1), #n(2)])

println(count(r, type R))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "2\n"

[<Fact>]
let ``Run - a tree is walked through its cases and the fields of their payloads`` () =
    let source =
        count
        + """type Tree = #node(Tree, int, Tree) | #leaf

def tree: Tree = #node(#node(#leaf, 1, #leaf), 2, #node(#leaf, 3, #leaf))

println(count(tree, type Tree))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3\n"

[<Fact>]
let ``Run - an arm at a type only known when the program runs gives a value of the narrowed type back`` () =
    let source =
        describe
        + """def zero: Type(a) -> #some(a) | #none = { t ->
  match t
    | #int -> #some(0)
    | #string -> #some("")
    | #list(elem) -> #some([])
    | else -> #none
}

def zeros: a, Type(a) -> string = { value, t ->
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
        """::install nanyx.json 0.4.2

import nanyx.json/json as json

def encodeAt: a, Type(a) -> json.Json = { value, t ->
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
        """{"name":"Ada","active":true,"age":36,"roles":[{"value":null,"tag":"admin"},{"value":2,"tag":"member"}]}
"""

let private naming =
    """def attributesOf: list((name: string, arguments: list((string, string)))) -> string = { attributes ->
  attributes
    \list.map { a -> "@" ++ a.name ++ "(" ++ (a.arguments \list.map { p -> p.0 ++ "=" ++ p.1 } \string.join(",")) ++ ")" }
    \string.join("")
}

def named: a, Type(a) -> string = { value, t ->
  match t
    | #int -> "{value}"
    | #string -> value
    | #record(r) ->
      def fields = r.fields \list.map { f -> f.name ++ attributesOf(f.attributes) ++ " = " ++ named(f.get(value), f.type) } \string.join(", ")
      "{r.name} ({r.doc}): {fields}"
    | #nominal(n) -> "nominal {n.name} from {n.module} ({n.doc})"
    | else -> "?"
}
"""

[<Fact>]
let ``Run - a description has the type's name, doc comment and the attributes on its fields`` () =
    let source =
        naming
        + """
--| Someone who can sign in.
type Account = (
  @json(name = "user_id")
  id: int
  @deprecated
  @range(1, 10)
  level: int
)

--| An email address.
type Email = protected (address: string)

println(named(Account(id = 7, level = 3), type Account))
println(named(Email(address = "a@b"), type Email))
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "Account (Someone who can sign in.): id@json(name=user_id) = 7, level@deprecated()@range(0=1,1=10) = 3\nEmail (An email address.): address = a@b\n"

[<Fact>]
let ``Run - a nominal type from another module is described by its name alone`` () =
    let people =
        """module people

--| A person, made by `Person.new`.
export type Person = protected (name: string)

export def Person.new: string -> Person = { name -> Person(name = name) }
"""
    let main =
        $"""module main

import people as p

{naming}
type Team = (lead: p.Person, size: int)

@entry
def main = {{
  println(named(p.Person.new("Ada"), type p.Person))
  println(named(Team(lead = p.Person.new("Ada"), size = 3), type Team))
}}
"""
    let outcome, _ = nanyxOnPackage [ "people.nyx", people; "main.nyx", main ] [ "run"; "{package}" ]
    assertRan "nominal Person from people (A person, made by `Person.new`.)\nTeam (): lead = nominal Person from people (A person, made by `Person.new`.), size = 3\n" outcome

[<Fact>]
let ``Run - a walker another module exports picks its arm at the types it is called at`` () =
    let walker =
        """module walker

export def size: a, Type(a) -> int = { value, t ->
  match t
    | #int -> value
    | #list(elem) -> value \list.map { x -> size(x, elem) } \list.fold(0, { total, n -> total + n })
    | else -> 0
}
"""
    let main =
        """module main

import walker as w

@entry
def main = {
  println(w.size([[1, 2], [3]], type list(list(int))))
}
"""
    let outcome, _ = nanyxOnPackage [ "walker.nyx", walker; "main.nyx", main ] [ "run"; "{package}" ]
    assertRan "6\n" outcome

[<Fact>]
let ``Run - a Type match tells apart types that share a layout, as a list and a set do`` () =
    let source =
        """def kind: Type(a) -> string = { t ->
  match t
    | #list(e) -> "list"
    | #set(e) -> "set"
    | else -> "other"
}

type Box(a) = (get: () -> string)

def boxFor: Type(a) -> Box(a) = { t -> Box(get = { -> kind(t) }) }

def run: Box(a) -> string = { b ->
  def get = b.get
  get()
}

def viaTyped: [Typed(a)] a -> string = { x -> kind(typeOf) }

println(run(boxFor(type list(int))))
println(run(boxFor(type set(int))))
println(kind(type list(int)))
println(kind(type set(int)))
println(viaTyped([1]))
println(viaTyped(set.fromList([1])))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "list\nset\nlist\nset\nlist\nset\n"

[<Fact>]
let ``Run - map.map at a type only known when the program runs maps its values`` () =
    let source =
        """def total: a, Type(a) -> int = { value, t ->
  match t
    | #int -> value
    | #map(_, item) -> list.sum(map.values(map.map(value, { x -> total(x, item) })))
    | #record(r) -> list.sum(list.map(r.fields, { f -> total(f.get(value), f.type) }))
    | else -> 0
}

println(total((m = ["a" => 1, "b" => 2]), type (m: map(string, int))))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "3\n"

[<Fact>]
let ``Run - a char is described as #char`` () =
    // #2658: `char` had no description, so a match on `type char` couldn't compile.
    let source =
        """def kind: Type(a) -> string = { t ->
  match t
    | #char -> "a char"
    | #string -> "a string"
    | else -> "something else"
}

println("{kind(type char)} {kind(type string)} {kind(type int)}")
"""
    runSource (normalized source) |> assertRan "a char a string something else\n"

[<Fact>]
let ``Run - json.encode of a record with a char field is an error it gives, not a compile failure`` () =
    // #2658: nanyx.json 0.4.2 gives a description kind it doesn't know, such as `#char`, as an EncodeError.
    let source =
        """::install nanyx.json 0.4.2

import nanyx.json/json as json

type Grade = (name: string, letter: char)

def letter: char = "A"

match json.encode(Grade(name = "Ada", letter = letter))
  | #some(text) -> println(text)
  | #cannotEncode(e) -> println(e.message)
"""
    runSource (normalized source) |> assertRan "A value of this type can't be written as JSON by this version of nanyx.json\n"
