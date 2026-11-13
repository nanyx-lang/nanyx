module Nanyx.Compiler.Tests.Features.Lambdas.ResultAnnotation

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda's result can be annotated after its parameters, `{ (x: int): int -> x * 2 }` (#442).

[<Fact>]
let ``A lambda's result annotation is its result type`` () =
    let result = compileClean "def double = { (x: int): int -> x * 2 }"
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int", []), result.Types.["double"])

[<Fact>]
let ``A result annotation settles a result inference would leave open`` () =
    let result = compileClean "def none = { (): list(string) -> [] }"
    Assert.Equal(TyFunc(TyPrimitive "unit", TyApply("list", [ TyPrimitive "string" ]), []), result.Types.["none"])

[<Fact>]
let ``A body that is not of the annotated result type is a type mismatch`` () =
    let source = "def double = { (x: int): string -> x * 2 }"
    let diagnostic = diagnosticOf source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("x * 2", textAt source (spanOf diagnostic))
