module TranspilerWasmTests.Features.Nominals.RunPrivateInstances

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a private type is compared, ordered and hashed by the ==, compare and hash it attaches`` () =
    let source =
        normalized
            """module ids =
  export type UserId = private (n: int)
  export def UserId.new: int -> UserId = { n -> UserId(n = n) }
  def value: UserId -> int = { u -> u.n }
  export def UserId.`==`: UserId, UserId -> bool = { x, y -> value(x) % 10 == value(y) % 10 }
  export def UserId.compare: UserId, UserId -> #less | #equal | #greater = { x, y -> int.compare(value(y), value(x)) }
  export def UserId.hash: UserId -> int = { x -> value(x) % 10 }

def a = ids.UserId.new(1)
def b = ids.UserId.new(2)
def c = ids.UserId.new(11)
println(a == c)
println(a != b)
println(a < b)
println([a, b] \list.sort \list.length)
println([b] \list.contains(c))
println(set[a, b, c] \set.length)
println([a => "ada"] \map.get(c))
"""
    runSource source |> assertRan "true\ntrue\nfalse\n2\nfalse\n2\n#some(ada)\n"
