module Nanyx.Compiler.Tests.Features.UndeterminedContextTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A context in a def's type applied to a type variable that no parameter or the result has: nothing can say what the
// variable is, so no call could supply the context (#1138).

[<Fact>]
let ``A context on a type variable used nowhere else in the type is an error at the context`` () =
    let code, text =
        singleError
            """
context Log = (log: string -> ())

def greet: [Log & ToString(a)] string -> () = { name -> log("hello {name}") }
"""
    Assert.Equal(DiagnosticCodes.UndeterminedContextType.Code, code)
    Assert.Equal("ToString(a)", text)

[<Fact>]
let ``The error says the variable is not the type of a parameter or the result`` () =
    let result =
        Compiler.compile
            """
def show: [ToString(a)] string -> string = { s -> s }
"""
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(
        "'a' in 'ToString(a)' is not the type of any parameter of 'show' or of its result, so no call can supply 'ToString(a)'",
        diagnostic.Message)

[<Fact>]
let ``A context on a type variable of a parameter or the result is not an error`` () =
    let result =
        Compiler.compile
            """
def show: [ToString(a)] a -> string = { x -> "{x}" }

def larger: [Ord(a)] list(a), a -> a = { xs, x -> xs \fold(x) { m, y -> if y > m then y else m } }
"""
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``A hole in a def whose type writes only ToString(a) says that a is not the parameter's type`` () =
    let result =
        Compiler.compile
            """
context Log = (log: string -> ())

def greet: [Log & ToString(a)] = { name -> log("hello {name}") }
"""
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal(
        "Interpolating a value of type 'a' requires the context 'ToString(a)', which is not available here: the 'a' in 'ToString(a)' in the type of 'greet' is not the type of this value, because that type writes no parameters: write the type of the parameter, as in '[ToString(a)] a -> ...'",
        diagnostic.Message)
    Assert.Empty(diagnostic.Fixes)
