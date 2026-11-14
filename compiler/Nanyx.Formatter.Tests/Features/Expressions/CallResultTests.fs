module Nanyx.Formatter.Tests.Features.Expressions.CallResult

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A call on a call's result is printed as written`` () =
    unchanged "def h = g()()\n"
    unchanged "def h = f(1)(2)(3)\n"
    unchanged "def k = { add(10)(5) }\n"
    unchanged (normalized """def main = {
  mk()()
  println("{add(1)(2)}")
}""")
    unchanged (lines [ "def r = match x"; "  | 1 -> mk()()"; "  | _ -> ()" ])
    Assert.Equal("def h = add(1)(2, 3)\n", format "def h = add( 1 )(2,3)")
