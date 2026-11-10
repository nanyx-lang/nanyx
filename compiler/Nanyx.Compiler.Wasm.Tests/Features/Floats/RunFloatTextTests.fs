module TranspilerWasmTests.Features.Floats.RunFloatText

open Xunit
open TranspilerWasmTestHelpers

/// A program that prints each of `holes`, a float expression, interpolated on a line of its own.
let private printing (holes: string list) =
    [ "import nanyx/console"
      "import nanyx/math as math"
      ""
      "def zero = 0.0"
      ""
      "@entry"
      "def main = {"
      yield! holes |> List.map (fun hole -> $"  println(\"{{{hole}}}\")")
      "}" ]
    |> String.concat "\n"

[<Fact>]
let ``Run - a float is written with enough digits to read it back`` () =
    // #547: the fraction was rounded to six digits, so 1e-7 printed as 0.0 and 0.1 + 0.2 as 0.3.
    printing [ "0.0000001"; "0.1 + 0.2"; "0.1 + 0.2 == 0.3"; "1.0 / 3.0"; "123456.789012345" ]
    |> runSource
    |> assertRan "1.0e-7\n0.30000000000000004\nfalse\n0.3333333333333333\n123456.789012345\n"

[<Fact>]
let ``Run - a float is written with the fewest digits that read it back`` () =
    // 17 significant digits where 16 would read back as a neighbour, and as few as one where that is enough.
    printing [ "1.7976931348623157e308"; "0.1"; "1.5"; "6.28"; "0.0 - 1.5"; "2.0 / 3.0"; "9007199254740993.0" ]
    |> runSource
    |> assertRan "1.7976931348623157e308\n0.1\n1.5\n6.28\n-1.5\n0.6666666666666666\n9007199254740992.0\n"

[<Fact>]
let ``Run - a whole float keeps its point`` () =
    printing [ "5.0"; "100.0"; "1.0e15"; "1.0e16"; "18446744073709551616.0"; "1.0e19"; "1.0e20" ]
    |> runSource
    |> assertRan
        "5.0\n100.0\n1000000000000000.0\n10000000000000000.0\n18446744073709552000.0\n10000000000000000000.0\n100000000000000000000.0\n"

[<Fact>]
let ``Run - a float below 1e-6 or from 1e21 up is written with an exponent`` () =
    // #522: a float of 2^64 or more printed as 0.0.
    printing [ "0.000001"; "0.00001234"; "2.5e-7"; "1.0e21"; "1.0e30"; "2.2250738585072014e-308"; "5.0e-324" ]
    |> runSource
    |> assertRan "0.000001\n0.00001234\n2.5e-7\n1.0e21\n1.0e30\n2.2250738585072014e-308\n5.0e-324\n"

[<Fact>]
let ``Run - NaN, the infinities and the zeros are written as themselves`` () =
    // #522: NaN and the infinities printed as 0.0 or -0.0.
    printing [ "math.sqrt(-1.0)"; "1.0 / zero"; "-1.0 / zero"; "zero"; "zero * -1.0" ]
    |> runSource
    |> assertRan "NaN\nInfinity\n-Infinity\n0.0\n-0.0\n"

[<Fact>]
let ``Run - Float.toString and dbg write a float as interpolation does`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(Float.toString(0.1 + 0.2))\n" +
        "  dbg(1.0e-7)\n" +
        "}\n"
    runSource source |> assertRan "0.30000000000000004\ndbg: 1.0e-7\n"
