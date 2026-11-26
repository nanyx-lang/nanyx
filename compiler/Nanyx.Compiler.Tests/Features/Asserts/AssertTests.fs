/// `assert`: built in, as `dbg` is, taking a `bool` and giving `()` (#2934). What it prints is the backend's tests'.
module Nanyx.Compiler.Tests.Features.Asserts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``assert takes a bool and gives ()`` () =
    Assert.Equal("()", typeOf "def checked = assert(1 < 2)" "checked")

[<Fact>]
let ``assert of a value that is not a bool is a mismatch`` () =
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'int' vs 'bool'", "3"), onlyDiagnostic "def x = assert(3)")

[<Fact>]
let ``assert given no argument is an error at its name`` () =
    Assert.Equal(
        (DiagnosticCodes.InvalidBuiltInCall.Code, "`assert` takes one argument, a `bool`, not 0", "assert"),
        onlyDiagnostic "def x = assert()")

[<Fact>]
let ``A def named assert is called in its place`` () =
    let source = normalized """def assert = { n: int -> n + 1 }
def two = assert(1)"""
    Assert.Equal("int", Unifier.tyToString (definedTypes source).["two"])
