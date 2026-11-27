/// A call of a value whose type is known and is no function (#2076).
module Nanyx.Compiler.Tests.Features.Calls.NotAFunction

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic of `source`, which must be `NYX0305`: its message and the text it is reported at.
let private notAFunction (source: string) =
    let code, message, at = onlyDiagnostic (withFinalNewline source)
    Assert.Equal(DiagnosticCodes.NotAFunction.Code, code)
    message, at

[<Fact>]
let ``A number called as a function says it is one, at its name`` () =
    Assert.Equal(("'x' is an int, not a function, so it can't be called", "x"), notAFunction "def x = 5\ndef y = x(3)")

[<Fact>]
let ``A record called as a function says its type`` () =
    Assert.Equal(
        ("'p' is of type '(x: int, y: int)', not a function, so it can't be called", "p"),
        notAFunction "def p = (x = 1, y = 2)\ndef q = p(1)")

[<Fact>]
let ``A string called as a function says it is one`` () =
    Assert.Equal(("'s' is a string, not a function, so it can't be called", "s"), notAFunction "def s = \"abc\"\ndef t = s(0)")

[<Fact>]
let ``A map is called to look up a key`` () =
    compileClean "def m = [\"a\" => 1]\ndef n = m(\"a\")" |> ignore
