module Nanyx.Compiler.Tests.Features.CallArgumentMismatch

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `f(a, b, c)` passes its arguments as one tuple. When they don't fit the callee's parameters, the mismatch is reported
// at the argument that doesn't, not at all of them (#2452).

[<Fact>]
let ``An argument that fixes the type a later lambda's result must have is where the lambda's result does not fit`` () =
    let source =
        normalized """def f: map(string, int) -> #some(int) | #none = { m ->
  map.fold(m, #none, { first, key, w -> if w > 0 then #some(w) else first })
}
"""
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'#some(int) | #none' vs '#none'", "#none"), onlyDiagnostic source)

[<Fact>]
let ``A number literal that fixes the type a later lambda's result must have is where the lambda's result does not fit`` () =
    let source =
        normalized """def f: list(int) -> int = { xs ->
  list.fold(
    xs,
    0,
    { acc, x -> "no" }
  )
}
"""
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'string' vs 'int'", "0"), onlyDiagnostic source)

[<Fact>]
let ``A lambda whose result does not fit its parameter's is where it does not fit`` () =
    let source = "def f: list(int) -> list(int) = { xs -> list.filter(xs, { x -> x + 1 }) }\n"
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'int' vs 'bool'", "{ x -> x + 1 }"), onlyDiagnostic source)

[<Fact>]
let ``An argument that does not fit its parameter is where it does not fit`` () =
    let source = "def f: list(int) -> list(int) = { xs -> list.take(xs, \"a\") }\n"
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'string' vs 'int'", "\"a\""), onlyDiagnostic source)
