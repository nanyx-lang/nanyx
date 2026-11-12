module TranspilerWasmTests.Features.Functions.RunArmsAfterParameters

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a lambda's arms after its parameters match the arguments`` () =
    let source =
        """import nanyx/console

def `>>=`: #some(a) | #none, (a -> #some(b) | #none) -> #some(b) | #none = { m, f ->
  | #some(v), f -> f(v)
  | #none, _ -> #none
}

def describe = { o ->
  | #some(n) -> "some {n}"
  | #none -> "none"
}

def pick = { a, b, c -> | 0, _, _ -> b | _, _, _ -> c + a }

@entry
def main = {
  println(describe(#some(2) >>= { n -> #some(n * 10) }))
  println(describe(#none >>= { n -> #some(n * 10) }))
  println("{pick(0, 5, 6)} {pick(1, 5, 6)}")
}
"""
            .ReplaceLineEndings "\n"
    runSource source |> assertRan "some 20\nnone\n5 7\n"
