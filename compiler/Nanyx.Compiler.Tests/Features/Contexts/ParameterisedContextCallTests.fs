module Nanyx.Compiler.Tests.Features.ParameterisedContextCalls

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A call to a function that requires a parameterised context finds the instance from its arguments' types. Arguments
// that do not fit the callee leave those types open, and the mismatch is the error, not the context (#1045).

let private named = """def named = { r, w: int -> "{r}" }
"""

[<Fact>]
let ``A call with too many arguments to a function that requires ToString(a) is a type mismatch`` () =
    let code, at = singleError (named + "def t = named(1, 2, 3)\n")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("1, 2, 3", at)

[<Fact>]
let ``A call that names an argument the function takes by position is a type mismatch when it requires ToString(a)`` () =
    let code, _ = singleError (named + "def t = named(1, w = 6)\n")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
let ``A call with the wrong arguments to an annotated function that requires ToString(a) is a type mismatch`` () =
    let source = """def named: [ToString(a)] a, int -> string = { r, w -> "{r}" }
def t = named(1, w = 6)
"""
    let code, _ = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
