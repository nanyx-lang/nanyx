/// Type arguments (see `TypeArguments`): a function that takes a type, `type a` or `into: type`, runs with a context
/// chosen by the type passed. A `Type` value holds nothing at run time.
module TranspilerWasmTests.Features.Functions.RunTypeArguments

open Xunit
open TranspilerWasmTestHelpers

let private decode =
    """context Decode(a) = (decode: string -> a)

type Point = (x: int, y: int)

def Point.decode: string -> Point = { text -> (x = text.length, y = 0) }

def int.decode: string -> int = { text -> text.length * 10 }

def deserialise: [Decode(a)] (type a, string) -> a = { t, text -> decode(text) }

def parse: [Decode(into)] (string, into: type) -> into = { text, into -> decode(text) }

def read: [Decode(a)] (string, type a) -> a = { text, t -> decode(text) }
"""

let private run (source: string) (expected: string) =
    runSource (decode + source) |> assertRan expected

[<Fact>]
let ``Run - a positional and a named type argument choose the context`` () =
    run
        """
println(deserialise(type Point, "abc").x)
println(parse("abcd", into = type Point).x)
println(deserialise(type int, "ab"))
"""
        "3\n4\n20\n"

[<Fact>]
let ``Run - a type argument is passed in a pipe and through partial application`` () =
    run
        """
def readPoint = { text -> deserialise(type Point, text) }

println("hello" \read(type int))
println(readPoint("xy").x)
"""
        "50\n2\n"

[<Fact>]
let ``Run - generic code passes on a type argument of its own type variable`` () =
    run
        """
def again: [Decode(a)] (type a, string) -> a = { t, text -> parse(text, into = type a) }

def both: [Decode(a)] (type a, string) -> list(a) = { t, text -> [deserialise(t, text), read(text, t)] }

println(again(type Point, "ab").x)
println(both(type int, "abc"))
"""
        "2\n[30, 30]\n"

[<Fact>]
let ``Run - a function that takes a type argument is passed as a value`` () =
    run
        """
def empty: (type a) -> list(a) = { t -> [] }

def build: ((type a) -> list(a)) -> list(a) = { f -> f(type a) }

def apply: ((type a, string) -> a), string -> a = { f, text -> f(type a, text) }

def numbers: list(int) = build(empty)
def point: Point = apply({ t, text -> deserialise(t, text) }, "abcde")

println(numbers)
println(point.x)
"""
        "[]\n5\n"
