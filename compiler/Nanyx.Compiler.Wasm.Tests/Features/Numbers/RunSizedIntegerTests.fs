module TranspilerWasmTests.Features.Numbers.RunSizedIntegers

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - sized integers compute, compare and print as their types`` () =
    let source =
        """
import nanyx/console

def total: [Num(a)] list(a) -> a = {
  | [] -> zero
  | [x, ...rest] -> x + total(rest)
}

@entry
def main = {
  def a = I32.fromInt(2_000_000_000)
  def b = U8.fromInt(200)
  def c = U64.fromInt(9_000_000_000_000_000_000)
  println("{I32.fromInt(5) + I32.fromInt(-7)} {b} {c + c} {a < I32.fromInt(3)} {c > U64.fromInt(1)}")
  println("{total([U8.fromInt(1), U8.fromInt(2)])} {-I16.fromInt(300)} {I8.fromInt(-128) / I8.fromInt(3)}")
  println("{U32.fromInt(4_000_000_000) / U32.fromInt(3)} {U32.fromInt(4_000_000_000) > U32.fromInt(1)}")
  println("{a == a} {I32.compare(a, I32.zero)} {U16.hash(U16.fromInt(65535))} {I8.toString(I8.fromInt(-5))}")
  dbg(b)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "-2 200 18000000000000000000 false true\n3 -300 -42\n1333333333 true\ntrue #greater 65535 -5\ndbg: 200\n"

[<Fact>]
let ``Run - sized integers are kept in fields, payloads, lists and closures`` () =
    let source =
        """
import nanyx/console

type Pixel = (r: U8, g: U8, big: U64, n: I16)

@entry
def main = {
  def p: Pixel = (r = U8.fromInt(255), g = U8.fromInt(7), big = U64.fromInt(5_000_000_000), n = I16.fromInt(-2))
  println("{p.r} {p.g} {p.big} {p.n}")
  match #some(I32.fromInt(-9))
  | #some(n) -> println("{n}")
  | #none -> println("none")
  for [U64.fromInt(1), U64.fromInt(2)] | n -> println("{n}")
  def add = { x: I8 -> x + I8.fromInt(1) }
  println("{add(I8.fromInt(3))} {[I32.fromInt(1)] == [I32.fromInt(1)]}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "255 7 5000000000 -2\n-9\n1\n2\n4 true\n"

[<Fact>]
let ``Run - a literal is of the sized type the code around it expects`` () =
    let source =
        """
import nanyx/console

def classify = { (b: U8) ->
  match b
  | 0 -> "zero"
  | 255 -> "max"
  | _ -> "other"
}

@entry
def main = {
  def small: U8 = 200
  def huge: U64 = 18_446_744_073_709_551_615
  def local: I16 = -300
  def ratio: F32 = 0.1
  def wide: U32 = 4_000_000_000
  println("{small} {huge} {local * 2} {classify(255)} {classify(3)} {ratio} {wide > 3_000_000_000}")
  println("{[1, 2] ++ [3]} {I8.zero} {3_000_000_000 + 1}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "200 18446744073709551615 -600 max other 0.1 true\n[1, 2, 3] 0 3000000001\n"

[<Theory>]
[<InlineData("I8.fromInt(127) + I8.fromInt(1)")>]
[<InlineData("I16.fromInt(-32768) - I16.fromInt(1)")>]
[<InlineData("I32.fromInt(65536) * I32.fromInt(65536)")>]
[<InlineData("I32.fromInt(-2_147_483_648) / I32.fromInt(-1)")>]
[<InlineData("U8.fromInt(0) - U8.fromInt(1)")>]
[<InlineData("U16.fromInt(256) * U16.fromInt(256)")>]
[<InlineData("U32.fromInt(4_294_967_295) + U32.fromInt(1)")>]
[<InlineData("U64.fromInt(1) - U64.fromInt(2)")>]
[<InlineData("U64.fromInt(5_000_000_000) * U64.fromInt(5_000_000_000)")>]
[<InlineData("-U8.fromInt(1)")>]
[<InlineData("U8.fromInt(256)")>]
[<InlineData("U64.fromInt(-1)")>]
let ``Run - sized integer arithmetic that does not fit stops the program`` (expression: string) =
    let source =
        $"""
import nanyx/console

@entry
def main = {{
  println("before")
  println("{{{expression}}}")
}}
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.Contains("wasm trap: integer overflow", outcome.StandardError)

[<Fact>]
let ``Run - sized integer arithmetic that comes close to not fitting does not stop the program`` () =
    let source =
        """
import nanyx/console

@entry
def main = {
  println("{I8.fromInt(126) + I8.fromInt(1)} {I8.fromInt(-127) - I8.fromInt(1)} {U8.fromInt(15) * U8.fromInt(17)}")
  println("{U32.fromInt(65535) * U32.fromInt(65537)} {I32.fromInt(-2_147_483_648) / I32.fromInt(1)}")
  println("{U64.fromInt(9_223_372_036_854_775_807) + U64.fromInt(9_223_372_036_854_775_807) + U64.fromInt(1)}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "127 -128 255\n4294967295 -2147483648\n18446744073709551615\n"

[<Fact>]
let ``Run - the wrapping operations keep the low bits of the result`` () =
    let source =
        """
import nanyx/console

def fnv: list(U32) -> U32 = {
  | [] -> 2_166_136_261
  | [x, ...rest] -> U32.wrappingMul(fnv(rest), 16_777_619) \U32.wrappingAdd(x)
}

@entry
def main = {
  def big: I8 = 127
  def zero: U8 = 0
  println("{I8.wrappingAdd(big, 1)} {U8.wrappingSub(zero, 1)} {I16.wrappingMul(300, 300)} {U16.wrappingAdd(65535, 2)}")
  println("{int.wrappingAdd(9_223_372_036_854_775_807, 1)} {U64.wrappingSub(0, 1)} {I32.wrappingMul(65536, 65536)} {U32.wrappingSub(0, 1)}")
  println("{fnv([1, 2, 3])} {I8.wrappingSub(-128, 1)} {I64.wrappingMul(3, 4)}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "-128 255 24464 1\n-9223372036854775808 18446744073709551615 0 4294967295\n3199756825 127 12\n"