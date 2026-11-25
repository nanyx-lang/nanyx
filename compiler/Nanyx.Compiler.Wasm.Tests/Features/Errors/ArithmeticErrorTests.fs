/// Integer overflow, division by zero and a float that can't be an int stop the program with a message that says what
/// went wrong, and the stack, innermost first, rather than the engine's backtrace (#1770, #2039, see
/// `WasmRunner.trapped`).
module TranspilerWasmTests.Features.Errors.ArithmeticErrors

open Xunit
open TranspilerWasmTestHelpers

/// Asserts that `source` stopped with exit code 1 and only the runtime error `message` on stderr, then the stack, whose
/// frames in the program, `main.nyx`, are `stack`. The frames in the standard library are left out, so that the test
/// doesn't depend on the library's lines.
let private assertStopsWith (message: string) (stack: string list) (source: string) =
    let outcome = runSource (normalized source)
    let lines = outcome.StandardError.ReplaceLineEndings("\n").TrimEnd('\n').Split '\n'
    Assert.Equal($"Runtime error: {message}", lines.[0])
    Assert.Equal<string list>(stack, lines.[1..] |> Array.filter (fun line -> line.StartsWith "  ") |> Array.map _.Trim() |> Array.filter (fun frame -> frame.Contains "(main.nyx:") |> List.ofArray)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``Integer overflow says so, with the stack rather than the engine's backtrace`` () =
    """println(9223372036854775807 + 1)
"""
    |> assertStopsWith "integer overflow: the result is out of the range of int" [ "<top level> (main.nyx:1)" ]

[<Fact>]
let ``Integer overflow names the function it happened in`` () =
    """def grow: int -> int = { n -> if n > 4611686018427387904 then n * 2 else grow(n * 2) }

println(grow(1))
"""
    |> assertStopsWith "integer overflow: the result is out of the range of int" [ "grow (main.nyx:1)"; "<top level> (main.nyx:3)" ]

[<Fact>]
let ``Division by zero says so`` () =
    """def z = list.length([])
println(7 // z)
"""
    |> assertStopsWith "division by zero" [ "<top level> (main.nyx:2)" ]

[<Fact>]
let ``Division by zero names the function it happened in`` () =
    """def divide: int, int -> int = { a, b -> if a == 0 then 0 else a // b + divide(a - 1, b) }

println(divide(3, list.length([])))
"""
    |> assertStopsWith "division by zero" [ "divide (main.nyx:1)"; "<top level> (main.nyx:3)" ]

[<Fact>]
let ``The remainder by zero is division by zero`` () =
    """def z = list.length([])
println(7 % z)
"""
    |> assertStopsWith "division by zero" [ "<top level> (main.nyx:2)" ]

[<Fact>]
let ``Dividing the smallest value of a signed type by -1 to a whole number overflows`` () =
    """def smallest: I8 = -128
def minusOne: I8 = -1
println(smallest // minusOne)
"""
    |> assertStopsWith "integer overflow: the result is out of the range of I8" [ "<top level> (main.nyx:3)" ]
    """def smallest = -9223372036854775807 - 1
println(smallest // -1)
"""
    |> assertStopsWith "integer overflow: the result is out of the range of int" [ "<top level> (main.nyx:2)" ]

[<Fact>]
let ``Dividing with a fraction by zero is infinite, not an error`` () =
    let outcome = runSource "def z = list.length([])\nprintln(\"{7 / z} {0 / z}\")\n"
    assertRan "Infinity NaN\n" outcome

[<Fact>]
let ``A float that is NaN made an int says which floats can't be, and what to use instead`` () =
    """import nanyx/math as math

println(math.floor(math.nan))
"""
    |> assertStopsWith
        "a float that is NaN, infinite or out of the range of int converted to an int (float.tryToInt gives #outOfRange for one)"
        [ "<top level> (main.nyx:3)" ]

[<Fact>]
let ``An int a sized integer can't hold names the type`` () =
    """println(I8.fromInt(300))
"""
    |> assertStopsWith "an int out of the range of I8 converted to I8" [ "<top level> (main.nyx:1)" ]

[<Fact>]
let ``The smallest int's absolute value overflows`` () =
    """import nanyx/math as math

println(math.abs(-9223372036854775807 - 1))
"""
    |> assertStopsWith
        "integer overflow: the result is out of the range of int"
        [ "<top level> (main.nyx:3)" ]

[<Fact>]
let ``Another trap is reported as it was`` () =
    let wat = """(module (func $start unreachable) (start $start))"""
    match Nanyx.Runtime.WasmRunner.run Nanyx.Runtime.Inherited [ "program" ] [] wat with
    | Error(Nanyx.Runtime.Trapped(message, _)) -> Assert.Contains("wasm trap: wasm `unreachable` instruction executed", message)
    | other -> Assert.Fail $"%A{other}"
