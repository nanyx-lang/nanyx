module TranspilerWasmTests.Features.Operators.RunCoalesce

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - ?? gives the value inside #some, or else the fallback, which runs only when it is needed`` () =
    let source =
        """
import nanyx/console

def fallback: [Console] string -> int = { name ->
  println("fallback {name}")
  0
}

def lookup: int -> #some(int) | #missing = { k -> if k > 0 then #some(k * 10) else #missing }

@entry
def main = {
  println(lookup(1) ?? fallback("a"))
  println(lookup(0) ?? fallback("b"))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "10\nfallback b\n0\n"

[<Fact>]
let ``Run - a chain of ?? tries each option in turn`` () =
    let source =
        """
import nanyx/console

def lookup: int -> #some(int) | #missing = { k -> if k > 0 then #some(k * 10) else #missing }

@entry
def main = {
  println(lookup(0) ?? lookup(2) ?? 7)
  println(lookup(0) ?? lookup(0) ?? 7)
  println(lookup(3) ?? 1 == 30)
  println([lookup(1), lookup(0)] \map { ?? 9 })
  println(someOrElse(lookup(0), { 5 }))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "20\n7\ntrue\n[10, 9]\n5\n"
