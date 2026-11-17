/// Integer overflow, division by zero and a float that can't be an int stop the program with a message that says what
/// went wrong, and in which function, rather than the engine's backtrace (#1770, see `WasmRunner.trapped`).
module TranspilerWasmTests.Features.Errors.ArithmeticErrors

open Xunit
open TranspilerWasmTestHelpers

/// Asserts that `source` stopped with exit code 1 and only the runtime error `message` on stderr.
let private assertStopsWith (message: string) (source: string) =
    let outcome = runSource (normalized source)
    Assert.Equal($"Runtime error: {message}\n", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``Integer overflow says so, without a backtrace`` () =
    """println(9223372036854775807 + 1)
"""
    |> assertStopsWith "integer overflow: the result is out of the range of int"

[<Fact>]
let ``Integer overflow names the function it happened in`` () =
    """def grow: int -> int = { n -> if n > 4611686018427387904 then n * 2 else grow(n * 2) }

println(grow(1))
"""
    |> assertStopsWith "integer overflow in 'grow': the result is out of the range of int"

[<Fact>]
let ``Division by zero says so`` () =
    """def z = list.length([])
println(7 / z)
"""
    |> assertStopsWith "division by zero"

[<Fact>]
let ``Division by zero names the function it happened in`` () =
    """def divide: int, int -> int = { a, b -> if a == 0 then 0 else a / b + divide(a - 1, b) }

println(divide(3, list.length([])))
"""
    |> assertStopsWith "division by zero in 'divide'"

[<Fact>]
let ``The remainder by zero is division by zero`` () =
    """def z = list.length([])
println(7 % z)
"""
    |> assertStopsWith "division by zero"

[<Fact>]
let ``A float that is NaN made an int says which floats can't be, and what to use instead`` () =
    """import nanyx/math as math

println(math.floor(math.nan))
"""
    |> assertStopsWith "a float that is NaN, infinite or out of the range of int converted to an int (float.tryToInt gives #outOfRange for one)"

[<Fact>]
let ``An int a sized integer can't hold names the type`` () =
    """println(I8.fromInt(300))
"""
    |> assertStopsWith "an int out of the range of I8 converted to I8"

[<Fact>]
let ``The smallest int's absolute value overflows`` () =
    """import nanyx/math as math

println(math.abs(-9223372036854775807 - 1))
"""
    |> assertStopsWith "integer overflow: the result is out of the range of int"

[<Fact>]
let ``Another trap is reported as it was`` () =
    let wat = """(module (func $start unreachable) (start $start))"""
    match Nanyx.Runtime.WasmRunner.run Nanyx.Runtime.Inherited [ "program" ] [] wat with
    | Error(Nanyx.Runtime.Trapped message) -> Assert.Contains("wasm trap: wasm `unreachable` instruction executed", message)
    | other -> Assert.Fail $"%A{other}"
