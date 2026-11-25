module TranspilerWasmTests.Features.Tags.RunExpectedPayload

open Xunit
open TranspilerWasmTestHelpers

// #1798: a literal written where a type is expected is checked against it, through a tag's payload, a list's elements
// and a map's values, so lists holding different tags of one union are lists of the union (see `ExpectedLiterals`).

[<Fact>]
let ``Run - a recursive union's value written as one literal, with lists of different tags, checks against the declared type`` () =
    runSource
        """
type T = #n(int) | #b(bool) | #arr(list(T))

def v: T = #arr([#arr([#n(1)]), #arr([#b(true)])])

println(v)
"""
    |> assertRan "#arr([#arr([#n(1)]), #arr([#b(true)])])\n"

[<Fact>]
let ``Run - a list of lists of different tags checks against a declared list type`` () =
    runSource
        """
type T = #n(int) | #b(bool) | #arr(list(T))

def w: list(T) = [#arr([#n(1)]), #arr([#b(true)])]
def nested: list(list(#n(int) | #b(bool))) = [[#n(1)], [#b(true)]]

println(w)
println(nested)
"""
    |> assertRan "[#arr([#n(1)]), #arr([#b(true)])]\n[[#n(1)], [#b(true)]]\n"

[<Fact>]
let ``Run - a map literal's values and an empty map in a tag's payload check against the declared union`` () =
    runSource
        """
type J = #num(float) | #flag(bool) | #arr(list(J)) | #obj(map(string, J))

def value: J = #obj(["a" => #arr([#num(1.0)]), "b" => #arr([#flag(true)]), "c" => #obj([])])

match value
  | #obj(members) -> println(map.keys(members))
  | _ -> println("not an object")
"""
    |> assertRan "[b, a, c]\n"

[<Fact>]
let ``Run - an argument's literal is checked against the parameter's type`` () =
    runSource
        """
type T = #n(int) | #b(bool) | #arr(list(T))

def count: T -> int = {
  | #arr(items) -> list.sum(items \map(count))
  | _ -> 1
}

println(count(#arr([#arr([#n(1), #n(2)]), #arr([#b(true)])])))
"""
    |> assertRan "3\n"
