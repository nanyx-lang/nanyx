module TranspilerWasmTests.Features.Operators.RunComparisonLiterals

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - generic code compares with integer literals, at ints and floats`` () =
    // #678: a literal beside a comparison was an `int`, so none of these compiled.
    let source =
        """
import nanyx/console
import nanyx/math as math

def isNegative: [Num(a) & Ord(a)] a -> bool = { n -> n < 0 }
def atMost: [Num(a) & Ord(a)] a -> bool = { n -> n <= 3 }
def below: [Num(a) & Ord(a)] a -> bool = { n -> 3 > n }
def atLeast: [Num(a) & Ord(a)] a -> bool = { n -> n >= 1 }
def isZero: [Num(a) & Eq(a)] a -> bool = { n -> 0 == n }
def notTwo: [Num(a) & Eq(a)] a -> bool = { n -> n != 2 }
@entry
def main = {
  println("{isNegative(-2)} {isNegative(0)} {isNegative(-2.5)} {isNegative(0.5)}")
  println("{atMost(3)} {atMost(4)} {atMost(3.0)} {atMost(3.5)}")
  println("{below(2)} {below(3)} {below(2.5)} {below(3.0)}")
  println("{atLeast(1)} {atLeast(0)} {atLeast(1.0)} {atLeast(0.5)}")
  println("{isZero(0)} {isZero(1)} {isZero(0.0)} {isZero(-0.0)} {isZero(0.5)}")
  println("{notTwo(2)} {notTwo(3)} {notTwo(2.0)} {notTwo(2.5)}")
  println("{math.abs(-3)} {math.abs(4)} {math.abs(-2.5)} {math.abs(-0.0)}")
}
"""
            .ReplaceLineEndings "\n"
    runSource source
    |> assertRan (
        """true false true false
true false true false
true false true false
true false true false
true false true true false
false true false true
3 4 2.5 0.0
"""
            .ReplaceLineEndings "\n"
    )
