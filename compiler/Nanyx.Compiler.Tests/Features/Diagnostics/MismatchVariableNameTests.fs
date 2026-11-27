/// The variables the compiler made up in a mismatch's types, named for a reader across the message (#2077).
module Nanyx.Compiler.Tests.Features.Diagnostics.MismatchVariableNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The message of the one diagnostic of `source`, which must be a type mismatch.
let private mismatch (source: string) =
    let code, message, _ = onlyDiagnostic (withFinalNewline source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    message

[<Fact>]
let ``An open record's fields are a, b, ... and its rest r`` () =
    Assert.Equal("'int' vs '(name: a, ...r)'", mismatch "def f = { x -> x.name }\ndef y = f(5)")

[<Fact>]
let ``Made-up variables are a, b, ... in the order the message writes them`` () =
    Assert.Equal("'int' vs '(a, b)'", mismatch "def z = match 1\n  | (x, y) -> x")
    Assert.Equal("'int' vs '[a] int -> b'", mismatch "def n = 5\ndef m = n(3)")

[<Fact>]
let ``A variable in both of a mismatch's types has one name`` () =
    Assert.Equal("'a' vs '[c] a -> b', which contains it, so the type would be infinite", mismatch "def selfApply = { x -> x(x) }")
