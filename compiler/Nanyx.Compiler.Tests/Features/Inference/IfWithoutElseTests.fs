module Nanyx.Compiler.Tests.Features.IfWithoutElse

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// #2690: an `if` with no `else` gives `()` when its condition is false. One whose then branch gives something else says
// that it has no `else`, at the `if`, rather than a bare mismatch with `()`.

let private noElse (ty: string) =
    $"This `if` has no `else`, so it gives '()' when its condition is false: add an `else` of type '{ty}'"

[<Fact>]
let ``An if with no else where a string is expected says it has no else`` () =
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, noElse "string", "if true then \"a\""),
        onlyDiagnostic (normalized "def s: string = if true then \"a\"\n"))

[<Fact>]
let ``An if with no else whose then branch is a string says it has no else`` () =
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, noElse "string", "if n > 10 then \"big\""),
        onlyDiagnostic (normalized "def size = { n: int -> if n > 10 then \"big\" }\n"))

[<Fact>]
let ``An if with no else whose then branch is a number says it has no else`` () =
    let source = normalized "def f = { c: bool ->\n  if c then 5\n  \"x\"\n}\n"
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, noElse "int", "if c then 5"), onlyDiagnostic source)

[<Fact>]
let ``An if with no else whose then branch is () or never gives a value checks`` () =
    let typed = compileClean (normalized "def f = { c: bool, n: int ->\n  if c then println(\"x\")\n  if c then crash(\"no\")\n  n\n}\n")
    Assert.Equal("bool, int -> int", Unifier.tyToString typed.Types["f"])

[<Fact>]
let ``An if with a written else keeps its branches' mismatch`` () =
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "The `else` branch gives 'string', but the `then` branch gives 'int'", "\"b\""),
        onlyDiagnostic (normalized "def h: int = if true then 1 else \"b\"\n"))
