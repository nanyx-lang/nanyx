module Nanyx.Compiler.Tests.Features.Calls.BuiltInCalls

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``dbg given no argument is an error at its name`` () =
    Assert.Equal(
        (DiagnosticCodes.InvalidBuiltInCall.Code, "`dbg` takes one argument, not 0", "dbg"),
        onlyDiagnostic "def x = dbg()")

[<Fact>]
let ``with given a field without a name is an error at the field`` () =
    let source =
        normalized """def point = (x = 1, y = 2)
def moved = point with (3)"""
    Assert.Equal(
        (DiagnosticCodes.InvalidRecordUpdate.Code, "This field has no name, and `with` updates fields by name: write `name = value`", "3"),
        onlyDiagnostic source)
