module TranspilerWasmTests.Features.Equality.RunImported

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an imported type's inferred == compares values made without a written type`` () =
    // `Obj.==` is inferred to require `Eq(a)`; a call to it, and to the `==` of a union around it, reads that from
    // `Obj(a)`, not from the type's own parameter (#2695).
    use directory = new TempDirectory()
    directory.Write
        "objs.nyx"
        """module objs

export type Obj(a) = protected (members: list((string, a)))

export def Obj.make = { members: list((string, a)) -> Obj(members = members) }

export def Obj.`==` = { x: Obj(a), y: Obj(a) ->
  list.all(x.members, { m -> list.contains(y.members, m) }) and x.members.length == y.members.length
}
"""
    directory.Write
        "main.nyx"
        """import objs as (Obj)

type J = #s(string) | #o(Obj(J))

@entry
def main = {
  def p = Obj.make([("a", #s("1")), ("b", #s("2"))])
  def q = Obj.make([("b", #s("2")), ("a", #s("1"))])
  def jp: J = #o(p)
  def jq: J = #o(q)
  println(jp == jq)
  println(#o(Obj.make([("k", jp)])) == #o(Obj.make([("k", jq)])))
  println(Obj.make([("k", jp)]) == Obj.make([("k", jq)]))
  println(Obj.make([("k", 1)]) == Obj.make([("k", 2)]))
}
"""
    runFile (System.IO.Path.Combine(directory.Path, "main.nyx")) |> assertRan "true\ntrue\ntrue\nfalse\n"

[<Fact>]
let ``Run - an imported type's inferred == is passed to a function that requires Eq`` () =
    // `Obj.==` is inferred to require `Eq(a)`, with no type written (#2814).
    use directory = new TempDirectory()
    directory.Write
        "objs.nyx"
        """module objs

export type Obj(a) = protected (members: list((string, a)))

export def Obj.make = { members: list((string, a)) -> Obj(members = members) }

export def Obj.`==` = { x: Obj(a), y: Obj(a) ->
  list.all(x.members, { m -> list.contains(y.members, m) }) and x.members.length == y.members.length
}
"""
    directory.Write
        "main.nyx"
        """import objs as (Obj)

def sameT: [Eq(t)] t, t -> bool = { a, b -> a == b }

@entry
def main = {
  def p = Obj.make([("a", 1), ("b", 2)])
  def q = Obj.make([("b", 2), ("a", 1)])
  println(sameT(p, q))
  println(sameT(p, Obj.make([("a", 1)])))
}
"""
    runFile (System.IO.Path.Combine(directory.Path, "main.nyx")) |> assertRan "true\nfalse\n"