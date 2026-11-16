module TranspilerWasmTests.Features.StdLib.RunFloatFormat

open TranspilerWasmTestHelpers
open Xunit

let private formats (cases: (string * int) list) =
    let lines =
        cases
        |> List.map (fun (x, decimals) -> $"""  println(float.format({x}, decimals = {decimals}))""")
        |> String.concat "\n"

    runSource $"@entry\ndef main = {{\n{lines}\n}}\n"

[<Fact>]
let ``Run - float.format writes a float with the given number of decimals`` () =
    formats [ "3.14159", 2; "2.0", 3; "0.5", 1; "123.456", 0; "7.0", 0; "0.0", 2; "1234567.25", 2 ]
    |> assertRan "3.14\n2.000\n0.5\n123\n7\n0.00\n1234567.25\n"

[<Fact>]
let ``Run - float.format rounds halves away from zero`` () =
    formats [ "2.5", 0; "3.5", 0; "-2.5", 0; "0.125", 2; "-1.875", 2; "0.96", 1 ]
    |> assertRan "3\n4\n-3\n0.13\n-1.88\n1.0\n"

[<Fact>]
let ``Run - float.format writes negative numbers with a sign, unless they round to zero`` () =
    formats [ "-3.14159", 3; "-0.5", 2; "-0.001", 2; "-0.0", 1; "-0.4", 0 ]
    |> assertRan "-3.142\n-0.50\n0.00\n0.0\n0\n"

[<Fact>]
let ``Run - float.format takes decimals below 0 as 0, and writes zeros past what a float holds`` () =
    formats [ "12.7", -1; "0.1", 20; "1000000000000000.0", 3 ]
    |> assertRan "13\n0.10000000000000000000\n1000000000000000.000\n"

[<Fact>]
let ``Run - float.format writes NaN and the infinities as float.toString does`` () =
    formats [ "0.0 / 0.0", 2; "1.0 / 0.0", 2; "-1.0 / 0.0", 0 ]
    |> assertRan "NaN\nInfinity\n-Infinity\n"

[<Fact>]
let ``Run - float.format in an interpolation hole, through a pipe`` () =
    let source =
        """@entry
def main = {
  def price = 2.5
  println("Total: {price \float.format(decimals = 2)}")
}
"""

    runSource source |> assertRan "Total: 2.50\n"
