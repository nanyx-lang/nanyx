module TranspilerWasmTests.Features.Contexts.RunExpectedTypeInstances

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a call finds its instance at the type its result is expected to have`` () =
    let source =
        """context Fallback(a) = (fallback: string -> a)
def int.fallback: string -> int = { s -> 7 }
def string.fallback: string -> string = { s -> "<" ++ s ++ ">" }
def get: [Fallback(a)] string -> a = { s -> fallback(s) }

def n: int = get("x")
def m: string -> int = { s -> get(s) }
def k = { xs -> [get("x")] \List.map({ v -> v + 1 }) }
def j = { s -> get(s) + 1 }
def t: string = get("y")

@entry
def main = {
  println("{n} {m("a")} {k(())} {j("b")} {t}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "7 7 [8] 8 <y>\n"

[<Fact>]
let ``Run - a call finds an instance of FromString at the type a def writes`` () =
    let source =
        """def readAll: [FromString(a)] list(string) -> list(#some(a) | failure) = { texts -> texts \List.map({ t -> FromString(a).fromString(t) }) }
def ints: list(#some(int) | #invalidInt) = readAll(["1", "x"])

@entry
def main = { dbg(ints) }
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "dbg: [#some(1), #invalidInt]\n"
