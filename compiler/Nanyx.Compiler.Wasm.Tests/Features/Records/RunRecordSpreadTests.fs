module TranspilerWasmTests.Features.Records.RunRecordSpread

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a record spread takes every field of its parts, a later part's replacing an earlier one's`` () =
    let source =
        """
def defaults = (host = "localhost", port = 8080)
def env = (port = 9000)
def cli = (logLevel = "debug")

def config = (
  ...defaults
  protocol = "https"
  ...env
  ...cli
)

def main = {
  println("{config.host} {config.port} {config.protocol} {config.logLevel}")
  def local = (...config, host = "example.com", port = 1)
  println("{local.host} {local.port} {config.host}")
}

main()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "localhost 9000 https debug\nexample.com 1 localhost\n"

[<Fact>]
let ``Run - a record spread evaluates its parts once each, in the order written`` () =
    let source =
        """
def made = { name, value ->
  println(name)
  (field = value, other = name)
}

def main = {
  def r = (...made("a", 1), field = made("b", 2).field, ...made("c", 3))
  println("{r.field} {r.other}")
}

main()
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "a\nb\nc\n3 c\n"

[<Fact>]
let ``Run - a spread of an open record sets its known fields and keeps the others`` () =
    // The record made is of the open record's own type, so the fields it doesn't know of come through (#1866).
    runSource
        """
def moveRight: (x: int, ...r) -> (x: int, ...r) = { p -> (...p, x = p.x + 1) }

def moved = moveRight(x = 1, y = 2, label = "a")
def again = moveRight(x = 10, name = "b")

println("{moved.label} is at {moved.x}, {moved.y}")
println("{again.name} is at {again.x}")
"""
    |> assertRan "a is at 2, 2\nb is at 11\n"
