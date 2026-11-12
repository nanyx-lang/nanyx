module TranspilerWasmTests.Features.Numbers.RunF32

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an F32 computes as an f32 and prints the shortest digits that read back as it`` () =
    let source =
        """
import nanyx/console

def total: [Num(a)] list(a) -> a = {
  | [] -> zero
  | [x, ...rest] -> x + total(rest)
}

def pow: F32, int -> F32 = { x, n -> if n == 0 then F32.fromInt(1) else x * pow(x, n - 1) }

@entry
def main = {
  def a = F32.fromInt(1) / F32.fromInt(10)
  def two = F32.fromInt(2)
  println("{a} {F32.fromInt(16_777_217)} {-a} {F32.fromInt(7) % two} {a < two} {F32.fromInt(1) / F32.fromInt(3)}")
  println("{total([a, a])} {F32.zero / F32.zero} {F32.fromInt(1) / F32.zero} {a == a}")
  println("{F32.fromInt(1) / pow(two, 126)} {F32.fromInt(1) / pow(two, 126) / pow(two, 23)} {pow(two, 128)}")
  println("{F32.compare(a, two)} {F32.hash(F32.zero) == F32.hash(-F32.zero)} {F32.toString(F32.fromInt(-3))}")
  dbg(a)
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "0.1 16777216.0 -0.1 1.0 true 0.33333334\n0.2 NaN Infinity true\n1.1754944e-38 1.0e-45 Infinity\n#less true -3.0\ndbg: 0.1\n"

[<Fact>]
let ``Run - an F32 is kept in fields, payloads, lists and closures`` () =
    let source =
        """
import nanyx/console

type Sample = (x: F32, n: int)

@entry
def main = {
  def a = F32.fromInt(3) / F32.fromInt(4)
  def s: Sample = (x = a, n = 2)
  println("{s.x} {[a, a + a]}")
  match #some(a)
  | #some(x) -> println("{x}")
  | #none -> println("none")
  def scale = { y: F32 -> y * a }
  println("{scale(F32.fromInt(2))} {[a] == [a]} {(a, 1) < (a, 2)}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "0.75 [0.75, 1.5]\n0.75\n1.5 true true\n"
