module Nanyx.Compiler.Tests.Features.PolymorphicRecursion

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A def that writes its type sees itself, while its body is checked, generic in the variables its annotation writes,
// so it can call itself at other types, as a walker over a value's parts does (see `InferModule`).

[<Fact>]
let ``A def that writes its type calls itself at another type`` () =
    let typed =
        compileClean (
            normalized
                """def count: a, int -> int = { x, n -> if n == 0 then 0 else 1 + count("s", n - 1) + count(1, n - 1) }
"""
        )
    Assert.Equal("a, int -> int", Unifier.tyToString typed.Types.["count"])

[<Fact>]
let ``A def that writes no type still sees itself at one type`` () =
    let result =
        Compiler.compile (
            normalized
                """def count = { x, n -> if n == 0 then 0 else count("s", n - 1) + count(1, n - 1) }
"""
        )
    assertReports DiagnosticCodes.TypeMismatch result

[<Fact>]
let ``A def with a hole in its type sees itself at it as it is`` () =
    let result =
        Compiler.compile (
            normalized
                """def count: _, int -> int = { x, n -> if n == 0 then 0 else count("s", n - 1) + count(1, n - 1) }
"""
        )
    assertReports DiagnosticCodes.TypeMismatch result
