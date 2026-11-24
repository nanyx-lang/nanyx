/// List literals, `[a, b]`: checked against a `list(a)`, each element is given where an `a` is expected; otherwise the
/// elements are joined as an `if`'s branches are.
module Nanyx.Compiler.Tests.Features.ListLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Lambdas in a list that give back different tags of the expected union each fit it`` () =
    let source =
        withFinalNewline
            """def firsts: list((int -> #some(int) | #none)) -> int = { fs -> 1 }

println(firsts([{ x -> #some(x) }, { x -> #none }]))"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``An element that doesn't fit the expected element type is reported at the element`` () =
    let source =
        withFinalNewline
            """def firsts: list((int -> #some(int) | #none)) -> int = { fs -> 1 }

println(firsts([{ x -> #some(x) }, { x -> #other }]))"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("{ x -> #other }", at)

[<Fact>]
let ``Tags in a list with nothing expected of it are joined`` () =
    Assert.Equal("list(#red | #green)", typeOf "def l = [#red, #green]" "l")
