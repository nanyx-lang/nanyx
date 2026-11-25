module TranspilerWasmTests.Features.StdLib.RunFloatPow

open System
open System.Globalization
open Xunit
open TranspilerWasmTestHelpers

// #2537: `float.pow` with an exponent that isn't a whole number, within a unit in the last place of the correctly
// rounded power, as .NET's `Math.Pow` gives it.

/// `x` as a Nanyx literal with `decimals` decimals.
let private literal (decimals: int) (x: float) = x.ToString($"F{decimals}", CultureInfo.InvariantCulture)

/// `float.pow(x, y)` for the literals `x` and `y`, with what `Math.Pow` gives for them.
let private powCall (x: string) (y: string) =
    $"float.pow({x}, {y})", Math.Pow(Double.Parse(x, CultureInfo.InvariantCulture), Double.Parse(y, CultureInfo.InvariantCulture))

[<Fact>]
let ``Run - float.pow of a fractional exponent is within a unit of the exact power, where its log is large`` () =
    // 38 units out at 10 ** 15.5 and about 160 at 7 ** 100.5 before, when the error of ln(x) was multiplied by y.
    assertWithinUlp
        [ powCall "10.0" "0.5"
          powCall "10.0" "15.5"
          powCall "7.0" "100.5"
          powCall "1.5" "1700.25"
          powCall "2.0" "1023.5"
          powCall "0.5" "1074.25" ]

[<Fact>]
let ``Run - float.pow is within a unit of the exact power at 300 pairs, from near 1e-321 to near 1e307`` () =
    let random = Random 2537
    [ for _ in 1..300 do
          // A base from e ** -10 to e ** 10, and an exponent that brings the power to e ** z, for a z spread from
          // -740 to 707, so that results near the ends of the range are among them.
          let x = literal 6 (Math.Exp(random.NextDouble() * 20.0 - 10.0))
          let z = random.NextDouble() * 1447.0 - 740.0
          let y = z / Math.Log(Double.Parse(x, CultureInfo.InvariantCulture))
          let y = if Math.Round(y, 4) = Math.Floor(Math.Round(y, 4)) then y + 0.00005 else y
          yield powCall x (literal 4 y) ]
    |> assertWithinUlp
