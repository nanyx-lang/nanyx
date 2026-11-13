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

/// A program that holds `I8`, `I16`, `U8` and `U16` values, at the ends of their ranges, in record fields, list cells,
/// tag payloads and a closure, and reads them back, compares, orders, hashes and copies them there.
let private narrowStorageSource =
    """
import nanyx/console

type Colour = (r: U8, g: U8, b: U8, depth: I16, wide: int)

def widen = { (n: I8) -> I8.toInt(n) }

def sameHash: [Hash(a)] a, a -> bool = { x, y -> hash(x) == hash(y) }

@entry
def main = {
  def c: Colour = (r = 255, g = 0, b = 128, depth = -32_768, wide = -1)
  println("{c.r} {c.g} {c.b} {c.depth} {c.wide}")
  def brighter = c with b = 7, depth = 32_767
  println("{brighter.b} {brighter.depth} {c == brighter} {c == c} {sameHash(c, c)}")
  def bytes: list(I8) = [-128, -1, 0, 127]
  println(list.map(bytes ++ [5], widen) \list.fold("", { acc, n -> acc ++ "{n} " }))
  println("{[I8.fromInt(-1)] < [I8.fromInt(1)]} {bytes == bytes}")
  match #some(U16.fromInt(65_535))
  | #some(n) -> println("{n}")
  | #none -> println("none")
  def level: I16 = -2
  def shifted = { x: I16 -> x + level }
  println("{shifted(I16.fromInt(-5))}")
}
"""

[<Fact>]
let ``Run - narrow sized integers keep their values at their own width`` () =
    runSource (narrowStorageSource.ReplaceLineEndings "\n")
    |> assertRan "255 0 128 -32768 -1\n7 32767 false true true\n-128 -1 0 127 5 \ntrue true\n65535\n-7\n"

[<Fact>]
let ``Transpile - narrow sized integers are stored in 1 and 2 bytes`` () =
    let wat = transpileWat (narrowStorageSource.ReplaceLineEndings "\n")
    Assert.Contains("i32.store8", wat)
    Assert.Contains("i32.store16", wat)
    Assert.Contains("i32.load8_u", wat)
    Assert.Contains("i32.load8_s", wat)
    Assert.Contains("i32.load16_s", wat)

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

[<Fact>]
let ``Run - a number converts to another type, keeping its value or saying it is out of range`` () =
    let source =
        """
import nanyx/console
import nanyx/math as math

@entry
def main = {
  def n: I32 = -5
  def big = 300
  def huge: U64 = 18_446_744_073_709_551_615
  println("{I32.toInt(n)} {n \toInt} {int.toU8(big)} {int.toU8(200)} {I32.toU32(n)} {U64.toInt(huge)} {U64.toI8(huge)}")
  println("{U8.toI8(255)} {I8.toU16(-1)} {I16.toI8(-128)} {U32.toInt(4_000_000_000)} {int.toU64(-1)} {I8.toU64(5)}")
  println("{I64.toFloat(3)} {U64.toFloat(huge)} {I32.toF32(16_777_217)} {float.toF32(0.1)} {F32.toFloat(0.1)} {float.toF32(1.0e300)}")
  println("{float.toInt(2.5, #round)} {float.toInt(-2.5, #round)} {float.toInt(2.7, #floor)} {float.toInt(-2.7, #ceil)} {float.toInt(-2.7, #truncate)}")
  println("{float.tryToInt(1.0e19, #round)} {float.tryToInt(0.0 / 0.0, #round)} {float.tryToInt(42.4, #round)} {F32.toInt(3.5, #round)} {F32.tryToInt(1.0e30, #floor)}")
  println("{math.round(2.5)} {math.floor(-0.5)} {math.ceil(0.1)} {math.truncate(-9.9)}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan (
        """-5 -5 #outOfRange #some(200) #outOfRange #outOfRange #outOfRange
#outOfRange #outOfRange #some(-128) 4000000000 #outOfRange #some(5)
3.0 18446744073709552000.0 16777216.0 0.1 0.10000000149011612 Infinity
3 -3 2 -2 -2
#outOfRange #outOfRange #some(42) 4 #outOfRange
3 -1 1 -9
""".ReplaceLineEndings "\n")

[<Fact>]
let ``Run - converting a float that is not a whole int stops the program`` () =
    let source =
        """
import nanyx/console

@entry
def main = {
  println("before")
  println("{float.toInt(1.0e19, #round)}")
}
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)