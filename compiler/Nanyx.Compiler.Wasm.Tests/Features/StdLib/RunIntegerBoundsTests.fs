module TranspilerWasmTests.Features.StdLib.RunIntegerBounds

open Xunit
open TranspilerWasmTestHelpers

// #2538: `maxValue` and `minValue` on `int` and each sized integer type.

[<Fact>]
let ``Run - each integer type's maxValue and minValue are its bounds`` () =
    normalized
        """@entry
def main = {
  println("{int.minValue} {int.maxValue}")
  println("{I8.minValue} {I8.maxValue}")
  println("{I16.minValue} {I16.maxValue}")
  println("{I32.minValue} {I32.maxValue}")
  println("{U8.minValue} {U8.maxValue}")
  println("{U16.minValue} {U16.maxValue}")
  println("{U32.minValue} {U32.maxValue}")
  println("{U64.minValue} {U64.maxValue}")
}
"""
    |> runSource
    |> assertRan (
        normalized
            """-9223372036854775808 9223372036854775807
-128 127
-32768 32767
-2147483648 2147483647
0 255
0 65535
0 4294967295
0 18446744073709551615
""")

[<Fact>]
let ``Run - converting I32.maxValue to int and back fits, and one more is out of range`` () =
    normalized
        """@entry
def main = {
  println(int.toI32(I32.toInt(I32.maxValue)))
  println(int.toI32(I32.toInt(I32.maxValue) + 1))
  println(int.toI32(I32.toInt(I32.minValue) - 1))
}
"""
    |> runSource
    |> assertRan "#some(2147483647)\n#outOfRange\n#outOfRange\n"

[<Fact>]
let ``Run - I32.maxValue + 1 stops the program with I32's overflow`` () =
    let outcome =
        normalized
            """@entry
def main = {
  def one: I32 = 1
  println(I32.maxValue + one)
}
"""
        |> runSource
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("integer overflow: the result is out of the range of I32", outcome.StandardError)
    Assert.Equal("", outcome.StandardOutput)
