/// A type description's `build` runs (see `CoreIrLowerBuild`): a record's makes a value from each field's value, or gives
/// the first failure, and a case's makes a value of its union from its payload.
module TranspilerWasmTests.Features.Types.RunTypeBuilds

open Xunit
open TranspilerWasmTestHelpers

let private zeroOf =
    """def zeroOf: Type(a) -> #some(a) | #error(string) = { t ->
  match t
    | #int -> #some(0)
    | #string -> #some("none")
    | #bool -> #some(false)
    | #unit -> #some(())
    | #list(elem) -> #some([])
    | #record(r) -> r.build { f -> zeroOf(f.type) }
    | #union(u) ->
      match u.cases
        | [c, ..._] ->
          match zeroOf(c.type)
            | #some(payload) -> #some(c.build(payload))
            | #error(e) -> #error(e)
        | [] -> #error("a union with no cases")
    | else -> #error("no zero")
}

def show = { result ->
  match result
    | #some(value) -> println("{value}")
    | #error(e) -> println("error: {e}")
}

"""

[<Fact>]
let ``Run - a record and a union case are built from their descriptions`` () =
    let source =
        zeroOf
        + """type Role = #member(int) | #admin

type User = (name: string, age: int, tags: list(string), role: Role, settings: (dark: bool, level: int))

show(zeroOf(type User))
show(zeroOf(type list(User)))
show(zeroOf(type Role))
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "(age = 0, name = none, role = #member(0), settings = (dark = false, level = 0), tags = [])\n[]\n#member(0)\n"

[<Fact>]
let ``Run - a record whose fields are named values and env is built from its description`` () =
    let source =
        zeroOf
        + """type Match = (values: list(int), env: string, arms: list(string))

show(zeroOf(type Match))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "(arms = [], env = none, values = [])\n"

[<Fact>]
let ``Run - a build whose function fails on one field gives that failure, and no value`` () =
    let source =
        zeroOf
        + """type Reading = (label: string, value: float, count: int)

show(zeroOf(type Reading))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "error: no zero\n"

[<Fact>]
let ``Run - a generic decoder reads JSON into any record, union, list and built-in type`` () =
    let source =
        """::install nanyx.json 0.5.0

import nanyx.json/json as json

def decodeAt: json.Json, Type(a) -> #some(a) | #error(string) = { j, t ->
  match t
    | #int ->
      match j
        | #number(n) -> #some(float.toInt(n, #truncate))
        | _ -> #error("expected a number")
    | #string ->
      match j
        | #string(s) -> #some(s)
        | _ -> #error("expected a string")
    | #bool ->
      match j
        | #bool(b) -> #some(b)
        | _ -> #error("expected a bool")
    | #unit -> #some(())
    | #list(elem) ->
      match j
        | #array(items) -> decodeAll(items, elem)
        | _ -> #error("expected an array")
    | #record(r) ->
      match j
        | #object(members) ->
          r.build { f ->
            match json.member(members, f.name)
              | #some(v) -> decodeAt(v, f.type)
              | _ -> #error("missing {f.name}")
          }
        | _ -> #error("expected an object")
    | #union(u) ->
      match j
        | #object(o) ->
          match json.member(o, "tag")
            | #some(#string(tag)) ->
              def payload =
                match json.member(o, "value")
                  | #some(v) -> v
                  | _ -> #null
              def unknown: #some(a) | #error(string) = #error("unknown tag {tag}")
              u.cases
                \list.fold(unknown, { found, c ->
                  if c.name == tag then
                    match decodeAt(payload, c.type)
                      | #some(p) -> #some(c.build(p))
                      | #error(e) -> #error(e)
                  else found
                })
            | _ -> #error("expected a tag")
        | _ -> #error("expected an object")
    | else -> #error("can't decode this type")
}

def decodeAll: list(json.Json), Type(b) -> #some(list(b)) | #error(string) = { items, t ->
  match items
    | [] -> #some([])
    | [first, ...rest] ->
      match decodeAt(first, t)
        | #some(x) ->
          match decodeAll(rest, t)
            | #some(xs) -> #some([x, ...xs])
            | #error(e) -> #error(e)
        | #error(e) -> #error(e)
}

type Role = #admin | #member(int)

type User = (name: string, age: int, roles: list(Role), active: bool)

def object: list((string, json.Json)) -> json.Json = { members -> #object(members) }

def show = { result ->
  match result
    | #some(user) -> println("{user}")
    | #error(e) -> println("error: {e}")
}

def roles = #array([object([("tag", #string("admin"))]), object([("tag", #string("member")), ("value", #number(2.0))])])

show(decodeAt(object([("name", #string("Ada")), ("age", #number(36.0)), ("roles", roles), ("active", #bool(true))]), type User))
show(decodeAt(object([("name", #string("Ada")), ("age", #string("old")), ("roles", #array([])), ("active", #bool(true))]), type User))
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "(active = true, age = 36, name = Ada, roles = [#admin, #member(2)])\nerror: expected a number\n"

[<Fact>]
let ``Run - a record holding a map and a set is rebuilt from its fields, its map and set still equal and found in`` () =
    let source =
        """type Stock = (counts: map(string, int), seen: set(int), name: string)

def copyOf: a, Type(a) -> #some(a) | #error(string) = { value, t ->
  match t
    | #record(r) -> r.build { f -> #some(f.get(value)) }
    | else -> #error("not a record")
}

def stock: Stock = (counts = ["a" => 1, "b" => 2, "c" => 3], seen = set.fromList([1, 2, 3]), name = "s")

match copyOf(stock, type Stock)
  | #some(copy) ->
    println(copy == stock)
    println(copy.counts("b"))
    println(map.containsKey(copy.counts, "d"))
    println(set.contains(copy.seen, 2))
    println(copy.counts == (["c" => 3, "b" => 2] \map.insert("a", 1)))
    println(copy.seen == (set.fromList([3, 1]) \set.insert(2)))
    println(map.toList(copy.counts) == map.toList(stock.counts))
  | #error(e) -> println(e)
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "true\n#some(2)\nfalse\ntrue\ntrue\ntrue\ntrue\n"
