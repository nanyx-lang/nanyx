module Nanyx.Compiler.Tests.Features.YieldGivenToEmitter

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda with `yield` in it is a lazy sequence, and a builder such as `list.select` takes a function that calls `emit`
// instead (#2566). One given to the other is NYX0201 at its first `yield`, saying what to write, not a mismatch between
// a sequence's record and `()` (#2571).

let private advice =
    "This `yield` makes the lambda a lazy sequence, but it is given where a function that calls `emit` is expected"

[<Fact>]
let ``A lambda that yields given to list.select is NYX0201 at the yield`` () =
    let code, message, text = onlyDiagnostic "def ys = [1, 2] \\list.select { x -> if x > 0 then yield x }\n"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("yield x", text)
    Assert.StartsWith(advice, message)
    Assert.EndsWith("write `emit(x)` for `yield x`", message)

[<Fact>]
let ``A builder's block that yields is NYX0201 at its first yield, once`` () =
    let source =
        normalized """def xs: list(int) = list.build {
  for 1..3 | i -> yield i
  yield 4
}
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("yield i", text)
    Assert.StartsWith(advice, message)

[<Fact>]
let ``A sequence made inside a builder's lambda is not the lambda's own yield`` () =
    let source =
        normalized """def counts = [1, 2] \list.select { x ->
  emit(seq { yield x } \seq.toList \list.length)
}
"""
    Assert.Equal("list(int)", typeIn (compileClean source) "counts")

[<Fact>]
let ``A lambda that yields is still a sequence where one is expected`` () =
    let source = normalized "def first = [1, 2] \\list.selectOne { x -> if x > 1 then yield x }\n"
    Assert.Equal("#some(int) | #notFound", typeIn (compileClean source) "first")
