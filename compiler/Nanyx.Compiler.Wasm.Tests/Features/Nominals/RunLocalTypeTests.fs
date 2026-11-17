module TranspilerWasmTests.Features.Nominals.RunLocalType

open TranspilerWasmTestHelpers
open Xunit

[<Fact>]
let ``Run - a local type alias names its type in the block that declares it`` () =
    let source =
        normalized """def total = { n: int ->
  type Pair = (int, int)
  def p: Pair = (n, n + 1)
  def (a, b) = p
  a + b
}

println("{total(20)}")
"""
    runSource source |> assertRan "41\n"

[<Fact>]
let ``Run - a local tag union is built and matched in the block that declares it`` () =
    let source =
        normalized """def f = { n: int ->
  type Shape = #circle(float) | #dot
  def s: Shape = if n > 0 then #circle(1.0) else #dot
  match s
    | #circle(r) -> r
    | #dot -> 0.0
}

println("{f(1)} {f(0)}")
"""
    runSource source |> assertRan "1.0 0.0\n"

[<Fact>]
let ``Run - a local protected type is built and read in the block that declares it`` () =
    let source =
        normalized """def price = { pence: int ->
  type Money = protected (pence: int)
  def m: Money = Money(pence = pence)
  m.pence * 2
}

println("{price(21)}")
"""
    runSource source |> assertRan "42\n"

[<Fact>]
let ``Run - a local type is used in a lambda nested in the block that declares it`` () =
    let source =
        normalized """def evaluate = { words: list(string) ->
  type Token = #op(int, int -> int) | #num(int)
  def classify: string -> Token = {
    | "+" -> #op({ + })
    | word -> #num(word \int.fromString \orCrash("not a number"))
  }
  def step: list(int), Token -> list(int) = {
    | stack, #num(n) -> [n, ...stack]
    | [a, b, ...rest], #op(op) -> [op(a, b), ...rest]
    | _, #op(_) -> crash("underflow")
  }
  words \map(classify) \reverse \fold([], step)
}

println("{evaluate(["+", "2", "3"])}")
"""
    runSource source |> assertRan "[5]\n"
