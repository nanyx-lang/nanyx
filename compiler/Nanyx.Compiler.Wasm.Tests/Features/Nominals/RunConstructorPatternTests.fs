module TranspilerWasmTests.Features.Nominals.RunConstructorPatterns

open Xunit
open TranspilerWasmTestHelpers

// A value is taken apart by its type's constructor: one pattern matches the whole of what the type is made from, and
// several, a trailing `...` or named fields take a record's fields (#2504). `runSource` checks the memory.

[<Fact>]
let ``Run - one pattern takes the whole value, as a lambda's parameter, in a match and in a def`` () =
    let source =
        normalized """type Email = protected string
type Point = protected (x: int, y: int)

def address: Email -> string = { Email(s) -> s }
def total: Point -> int = { Point(p) -> p.x + p.y }

def shout = Email("ada@example.com") \match
  | Email(s) -> s ++ "!"

def Email(own) = Email("own@example.com")

println(address(Email("bob@example.com")))
println(total(Point(x = 3, y = 4)))
println(shout)
println(own)
"""
    runSource source |> assertRan "bob@example.com\n7\nada@example.com!\nown@example.com\n"

[<Fact>]
let ``Run - several patterns, ... or named fields take a record's fields`` () =
    let source =
        normalized """type Email = protected (address: string, verified: bool)
type Pair = (string, int)

def address: Email -> string = { Email(address = s, ...) -> s }
def label = { Pair(name, count) -> "{name}: {count}" }

def describe: Email -> string = {
  | Email(address = a, verified = true) -> "{a} (verified)"
  | Email(address, ...) -> address
}

def Pair(first, ...) = Pair("left", 1)

println(address(Email(address = "a@example.com", verified = false)))
println(label(Pair("apples", 3)))
println(describe(Email(address = "b@example.com", verified = true)))
println(describe(Email(address = "c@example.com", verified = false)))
println(first)
"""
    runSource source |> assertRan "a@example.com\napples: 3\nb@example.com (verified)\nc@example.com\nleft\n"
