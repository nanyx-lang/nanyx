/// A lambda of several parameters checked against a function type of one parameter that is no record (#2659).
module Nanyx.Compiler.Tests.Features.Lambdas.LambdaArity

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A lambda with more parameters than its type gives it says both counts, at its parameters`` () =
    let source = withFinalNewline "def add: int -> int = { a, b -> a }"
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("This lambda takes 2 parameters, but its type gives it one, of type 'int'", message)
    Assert.Equal("a, b", at)

[<Fact>]
let ``A function's parameters in parentheses are one parameter, and the message says where the parentheses go`` () =
    let code, message, _ = onlyDiagnostic (withFinalNewline "def iter: (seq(a), a -> ()) -> () = { xs, f -> () }")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal(
        "This lambda takes 2 parameters, but its type gives it one, of type '(seq(a), a -> ())': "
        + "for 2 parameters, the last of them a function, put the parentheses round that function, 'seq(a), (a -> ()) -> ()'",
        message)
    compileClean "def iter: seq(a), (a -> ()) -> () = { xs, f -> () }" |> ignore

[<Fact>]
let ``A function parameter that takes another number of parameters than the lambda gets no suggestion`` () =
    let _, message, _ = onlyDiagnostic (withFinalNewline "def both: (int, int -> int) -> int = { a, b, c -> 0 }")
    Assert.Equal("This lambda takes 3 parameters, but its type gives it one, of type '(int, int -> int)'", message)

[<Fact>]
let ``A lambda of as many parameters as a tuple input, or of one, still checks`` () =
    compileClean "def add: (int, int) -> int = { a, b -> a + b }\ndef first: int, int -> int = { pair -> pair.0 }" |> ignore
