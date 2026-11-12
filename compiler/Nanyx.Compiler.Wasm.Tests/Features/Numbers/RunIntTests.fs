module TranspilerWasmTests.Features.Numbers.RunInts

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an int is 64 bits, in locals, fields, payloads, lists and closures`` () =
    // #342: an int was an `i32`, so each of these wrapped past about 2.1 billion.
    let source =
        """
import nanyx/console

type Point = (x: int, label: string, y: int)

def sum = { xs: list(int) ->
  match xs
  | [] -> 0
  | [x, ...rest] -> x + sum(rest)
}

@entry
def main = {
  def big = 3_000_000_000 + 1
  println("{big}")
  def p: Point = (x = 5_000_000_000, label = "p", y = -7)
  println("{p.x} {p.label} {p.y}")
  println("{sum([1, 2, 3_000_000_000])}")
  match #some(9_000_000_000)
  | #some(n) -> println("{n}")
  | #none -> println("none")
  for [10, 20] ++ [4_000_000_000] | n -> println("{n}")
  def add = { a: int -> a + big }
  println("{add(1)}")
  println("{-9_223_372_036_854_775_807 - 1}")
  println("{3_000_000_000 > 1} {(2, 5_000_000_000) == (2, 5_000_000_000)}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "3000000001\n5000000000 p -7\n3000000003\n9000000000\n10\n20\n4000000000\n3000000002\n-9223372036854775808\ntrue true\n"

[<Theory>]
[<InlineData("9_223_372_036_854_775_807 + 1")>]
[<InlineData("-9_223_372_036_854_775_807 - 2")>]
[<InlineData("4_000_000_000 * 4_000_000_000")>]
[<InlineData("(-9_223_372_036_854_775_807 - 1) * -1")>]
[<InlineData("(-9_223_372_036_854_775_807 - 1) / -1")>]
let ``Run - int arithmetic that overflows stops the program`` (expression: string) =
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
let ``Run - int arithmetic that comes close to overflowing does not stop the program`` () =
    let source =
        """
import nanyx/console

@entry
def main = {
  println("{9_223_372_036_854_775_806 + 1}")
  println("{-9_223_372_036_854_775_807 - 1}")
  println("{3_037_000_499 * 3_037_000_499}")
  println("{-1 * 9_223_372_036_854_775_807}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "9223372036854775807\n-9223372036854775808\n9223372030926249001\n-9223372036854775807\n"
