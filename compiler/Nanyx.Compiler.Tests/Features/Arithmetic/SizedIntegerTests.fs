module Nanyx.Compiler.Tests.Features.SizedIntegers

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The type of `name` in `source`, which has no diagnostics, as the compiler prints it.
let private typeOf (name: string) (source: string) =
    Unifier.tyToString ((compileClean (source.ReplaceLineEndings "\n")).Types.[name])

[<Theory>]
[<InlineData("I8")>]
[<InlineData("I16")>]
[<InlineData("I32")>]
[<InlineData("U8")>]
[<InlineData("U16")>]
[<InlineData("U32")>]
[<InlineData("U64")>]
let ``A sized integer type is a number of its own name`` (name: string) =
    let source =
        $"""
def f = {{ (a: {name}), (b: {name}) -> -a * b + a / b - a %% b }}
def less = {{ (a: {name}), (b: {name}) -> a < b }}
def same = {{ (a: {name}), (b: {name}) -> a == b }}
"""
    Assert.Equal($"{name}, {name} -> {name}", typeOf "f" source)
    Assert.Equal($"{name}, {name} -> bool", typeOf "less" source)
    Assert.Equal($"{name}, {name} -> bool", typeOf "same" source)

[<Fact>]
let ``A sized integer is not an int`` () =
    let source = "def mixed = { (a: I32), (b: int) -> a + b }\n"
    let result = Compiler.compile source
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("Type mismatch: I32 vs int.", diagnostic.Message)

[<Fact>]
let ``A sized integer has the compiler's own Num`` () =
    let source =
        """
def total: [Num(a)] list(a) -> a = {
  | [] -> zero
  | [x, ...rest] -> x * 2 + total(rest)
}

def bytes = total([U8.fromInt(1), U8.fromInt(2)])
def text = I16.toString(I16.zero)
"""
    Assert.Equal("U8", typeOf "bytes" source)
    Assert.Equal("string", typeOf "text" source)

[<Fact>]
let ``A definition attached to a sized integer is reached by a pipe`` () =
    let source =
        """
def I32.halve = { (n: I32) -> n / I32.fromInt(2) }
def half = { (n: I32) -> n \halve }
"""
    Assert.Equal("I32 -> I32", typeOf "half" source)

[<Theory>]
[<InlineData("int")>]
[<InlineData("I8")>]
[<InlineData("U32")>]
[<InlineData("U64")>]
let ``Each integer type has arithmetic that wraps around`` (name: string) =
    let source =
        $"""
def hash = {{ (a: {name}), (b: {name}) -> {name}.wrappingMul({name}.wrappingAdd(a, b), {name}.wrappingSub(a, b)) }}
"""
    Assert.Equal($"{name}, {name} -> {name}", typeOf "hash" source)