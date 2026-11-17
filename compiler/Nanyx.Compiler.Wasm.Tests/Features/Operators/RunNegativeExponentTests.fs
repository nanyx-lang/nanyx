/// `x ** n` with a negative `n`: `1 / x ** -n` on a float, and a crash on an integer type, which can't hold the result
/// (#1875). `__pow` asks `Num`'s `isInteger` which it is.
module TranspilerWasmTests.Features.Operators.RunNegativeExponent

open Xunit
open TranspilerWasmTestHelpers

let private message = "crash: -1 is a negative exponent, and an integer raised to one isn't an integer\n"

[<Fact>]
let ``Run - a float raised to a negative power is one over its positive power`` () =
    let source =
        normalized """import nanyx/math as math

def inverse: [Num(a)] a -> a = { x -> x ** -1 }

println("{2.0 ** -1} {F32.fromInt(4) ** -2} {math.pow(10.0, -2)} {inverse(0.5)}")
"""
    runSource source |> assertRan "0.5 0.0625 0.01 2.0\n"

[<Fact>]
let ``Run - an int raised to a negative power stops the program with a message`` () =
    let source =
        normalized """println("before")
println(2 ** -1)
"""
    let outcome = runSource source
    Assert.Equal("before\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal(message, withoutStack outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)

[<Theory>]
[<InlineData("I8.fromInt(2) ** -1")>]
[<InlineData("I16.fromInt(2) ** -1")>]
[<InlineData("I32.fromInt(2) ** -1")>]
[<InlineData("U8.fromInt(2) ** -1")>]
[<InlineData("U16.fromInt(2) ** -1")>]
[<InlineData("U32.fromInt(2) ** -1")>]
[<InlineData("U64.fromInt(2) ** -1")>]
[<InlineData("math.pow(1, -1)")>]
let ``Run - every integer type raised to a negative power stops the program`` (expression: string) =
    let source = $"""import nanyx/math as math

println({expression})
"""
    let outcome = runSource (source.ReplaceLineEndings "\n")
    Assert.Equal(message, withoutStack outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)
