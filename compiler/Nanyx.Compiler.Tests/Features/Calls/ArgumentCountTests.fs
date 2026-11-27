/// A call that gives its callee another number of arguments than it takes (#2075).
module Nanyx.Compiler.Tests.Features.Calls.ArgumentCount

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic of `source`, which must be `NYX0304`: its message and the text it is reported at.
let private argumentCount (source: string) =
    let code, message, at = onlyDiagnostic (withFinalNewline source)
    Assert.Equal(DiagnosticCodes.ArgumentCount.Code, code)
    message, at

[<Fact>]
let ``A call with too few arguments says how many the function takes and the call gives`` () =
    Assert.Equal(
        ("'f' takes 2 arguments, but this call gives 1", "1"),
        argumentCount "def f: int, int -> int = { a, b -> a + b }\ndef z = f(1)")

[<Fact>]
let ``A call with too many arguments says so, at its arguments`` () =
    Assert.Equal(
        ("'g' takes 1 argument, but this call gives 2", "1, 2"),
        argumentCount "def g: int -> int = { a -> a }\ndef w = g(1, 2)")
    Assert.Equal(
        ("'f' takes 2 arguments, but this call gives 3", "1, 2, 3"),
        argumentCount "def f: int, int -> int = { a, b -> a + b }\ndef z = f(1, 2, 3)")

[<Fact>]
let ``A call of an inferred function with too few arguments reports no context it requires`` () =
    Assert.Equal(
        ("'h' takes 2 arguments, but this call gives 1", "1"),
        argumentCount "def h = { x, y -> x + y }\ndef v = h(1)")

[<Fact>]
let ``A pipe counts its piped value`` () =
    Assert.Equal(
        ("'g' takes 1 argument, but this pipe gives it 2, counting the piped value", "3 \\g(4)"),
        argumentCount "def g: int -> int = { a -> a }\ndef p = 3 \\g(4)")
    Assert.Equal(
        ("'f' takes 2 arguments, but this pipe gives it 1, counting the piped value", "3 \\f"),
        argumentCount "def f: int, int -> int = { a, b -> a + b }\ndef q = 3 \\f")

[<Fact>]
let ``A tuple of a function's parameters is its arguments`` () =
    compileClean "def f: int, int -> int = { a, b -> a + b }\ndef a = f((1, 2))\ndef pair = (1, 2)\ndef b = f(pair)\ndef c = 3 \\f(4)"
    |> ignore
