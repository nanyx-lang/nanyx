/// The variables the compiler made up in the types a diagnostic names, named for a reader across the message as hover
/// names them (#2077, #3052).
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
let ``An open record's fields are α, β, ..., and its rest, written nowhere else, a bare spread`` () =
    Assert.Equal("'int' vs '(name: α, …)'", mismatch "def f = { x -> x.name }\ndef y = f(5)")

[<Fact>]
let ``Made-up variables are α, β, ... in the order the message writes them`` () =
    Assert.Equal("'int' vs '(α, β)'", mismatch "def z = match 1\n  | (x, y) -> x")

[<Fact>]
let ``A variable in both of a mismatch's types has one name`` () =
    Assert.Equal("'α' vs '[κ] α -> β', which contains it, so the type would be infinite", mismatch "def selfApply = { x -> x(x) }")

[<Fact>]
let ``A recursive def that would give itself an option of its own result names the variable on both sides alike`` () =
    let source = "def loop = { xs ->\n  match xs\n    | [] -> #some(xs)\n    | [x, ...rest] -> if x then loop(rest) else xs\n}"
    Assert.Equal("'α' vs '#some(α)', which contains it, so the type would be infinite", mismatch source)

/// The message of the one diagnostic of `source`.
let private messageOf (source: string) =
    let _, message, _ = onlyDiagnostic (withFinalNewline source)
    message

[<Fact>]
let ``A message about one type, not a mismatch, names its variables as hover does`` () =
    Assert.Contains("but this is '#some(α)', which is option-like", messageOf "def f = { x, y -> x ?? #some(y) }")
    Assert.EndsWith("where 'a' would have to be '(foo: int, …)'", messageOf "def f: a -> int = { x -> x.foo }")
    Assert.EndsWith("where 'a' would have to be '[α] int, int -> int'", messageOf "def f: a -> int = { x -> x(1, 2) }")
