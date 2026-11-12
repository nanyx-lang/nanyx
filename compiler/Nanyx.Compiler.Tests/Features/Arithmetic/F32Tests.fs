module Nanyx.Compiler.Tests.Features.F32

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The type of `name` in `source`, which has no diagnostics, as the compiler prints it.
let private typeOf (name: string) (source: string) =
    Unifier.tyToString ((compileClean (source.ReplaceLineEndings "\n")).Types.[name])

[<Fact>]
let ``F32 is a number of its own name`` () =
    let source =
        """
def f = { (a: F32), (b: F32) -> -a * b + a / b - a % b }
def less = { (a: F32), (b: F32) -> a < b }
def total: [Num(a)] list(a) -> a = {
  | [] -> zero
  | [x, ...rest] -> x * 2 + total(rest)
}
def floats = total([F32.fromInt(1), F32.zero])
"""
    Assert.Equal("F32, F32 -> F32", typeOf "f" source)
    Assert.Equal("F32, F32 -> bool", typeOf "less" source)
    Assert.Equal("F32", typeOf "floats" source)

[<Fact>]
let ``An F32 is not a float`` () =
    let source = "def mixed = { (a: F32), (b: float) -> a + b }\n"
    let result = Compiler.compile source
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("Type mismatch: F32 vs float.", diagnostic.Message)
