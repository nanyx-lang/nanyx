/// A map or a set read through a type description, whose keys or elements an arm narrows to a type it knows, and one made
/// where the arm knows them and given back as the description's: its keys are boxed as the description has them, and
/// unboxed as the arm reads them, both ways (#2881).
module TranspilerWasmTests.Features.Types.RunTypeCollectionNarrowing

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a map's keys and a set's elements read from a record's field are those it holds`` () =
    let source =
        """type Holder = (scores: map(string, list(int)), tags: set(string))

def sizesIn: a, Type(a) -> list(string) = { value, t ->
  match t
    | #map(key, item) -> sizesOfMap(value, key, item)
    | #set(elem) -> setText(value, elem)
    | #record(r) -> list.flatMap(r.fields, { f -> sizesIn(f.get(value), f.type) })
    | else -> []
}

def sizesOfMap: map(k, v), Type(k), Type(v) -> list(string) = { m, key, item ->
  match key
    | #string -> (match item
      | #list(_) -> map.toList(m) \list.map { pair -> "{pair.0}:{list.length(pair.1)}" } \list.sort
      | else -> [])
    | else -> []
}

def setText: set(e), Type(e) -> list(string) = { s, elem ->
  match elem
    | #string -> set.toList(s) \list.sort
    | else -> []
}

println(sizesIn(["a" => [1, 2, 3]], type map(string, list(int))))
println(sizesIn(Holder(scores = ["a" => [1, 2], "b" => [10]], tags = set.fromList(["xy", "z"])), type Holder))
"""
    runSource (normalized source) |> assertRan "[a:3]\n[a:2, b:1, xy, z]\n"

[<Fact>]
let ``Run - a map and a set made where their keys are known build a record's fields`` () =
    let source =
        """def zeroOf: Type(a) -> #some(a) | #error(string) = { t ->
  match t
    | #int -> #some(0)
    | #map(key, item) -> (match key
      | #string -> (match zeroOf(item)
        | #some(v) -> #some(["zero" => v, "nil" => v])
        | #error(e) -> #error(e))
      | else -> #error("no zero"))
    | #set(elem) -> (match elem
      | #string -> #some(set.fromList(["zero"]))
      | else -> #error("no zero"))
    | #record(r) -> r.build { f -> zeroOf(f.type) }
    | else -> #error("no zero")
}

type Counts = (byName: map(string, int), names: set(string))

match zeroOf(type Counts)
  | #some(counts) ->
    println(map.toList(counts.byName) \list.sort)
    println(set.toList(counts.names))
  | #error(e) -> println("error: {e}")
"""
    runSource (normalized source) |> assertRan "[(nil, 0), (zero, 0)]\n[zero]\n"
