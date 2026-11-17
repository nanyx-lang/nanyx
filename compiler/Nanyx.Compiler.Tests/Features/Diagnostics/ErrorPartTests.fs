/// A value made of one that failed to check, such as a list of them, is not another error where it is used (#1771):
/// only the error inside it is reported.
module Nanyx.Compiler.Tests.Features.Diagnostics.ErrorParts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private assertOnlyMismatchAt (written: string) (source: string) =
    let code, message, text = onlyDiagnostic (normalized source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'#some(a)' is not a number, so it cannot be used with +", message)
    Assert.Equal(written, text)

[<Fact>]
let ``A list of a value that failed to check is printed with no error of its own`` () =
    """
println([#some(1) + 1])
"""
    |> assertOnlyMismatchAt "#some(1) + 1"

[<Fact>]
let ``A list of a value that failed to check is interpolated with no error of its own`` () =
    """
println("{list.map([1], { v -> #some(v) + 1 })}")
"""
    |> assertOnlyMismatchAt "#some(v) + 1"

[<Fact>]
let ``A def of a list of a value that failed to check is printed with no error of its own`` () =
    """
def xs = list.map([1], { v -> #some(v) + 1 })
println(xs)
"""
    |> assertOnlyMismatchAt "#some(v) + 1"
