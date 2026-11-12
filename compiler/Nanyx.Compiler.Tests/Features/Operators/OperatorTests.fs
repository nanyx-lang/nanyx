/// Nanyx's operators are a closed list (see `binaryOperators`): what the compiler reports for one it does not have, or
/// for a spare one no type gives a meaning, and the types of a composition.
module Nanyx.Compiler.Tests.Features.Operators

open Xunit
open NyxCompiler

/// The one diagnostic `source` has: its code, its message with FParsec's line breaks taken out, and the text from
/// where it is reported to the end of its line.
let private onlyDiagnostic (source: string) =
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        let rest = source.Substring(diagnostic.Span.Value.Start)
        let lineEnd = rest.IndexOf '\n'
        diagnostic.Code, diagnostic.Message.ReplaceLineEndings(" "), (if lineEnd < 0 then rest else rest.Substring(0, lineEnd))
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Theory>]
[<InlineData("def x = true && false", "`&&` is not an operator in Nanyx: write `and`", "&& false")>]
[<InlineData("def x = true || false", "`||` is not an operator in Nanyx: write `or`", "|| false")>]
[<InlineData("def x = !true", "There is no prefix `!` in Nanyx: logical not is the keyword `not`, as in `not x`", "!true")>]
[<InlineData("def x = 1 < 2 < 3", "Comparisons do not chain: write `a < b and b < c`, or put one comparison in parentheses", "< 3")>]
[<InlineData("def x = 1 == 1 == true", "Comparisons do not chain: write `a == b and b == c`, or put one comparison in parentheses", "== true")>]
let ``Syntax Nanyx does not have is reported as what to write instead`` (source: string) (message: string) (at: string) =
    let code, reported, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, code)
    Assert.Contains(message, reported)
    Assert.Equal(at, text)

[<Fact>]
let ``Defining an operator Nanyx does not have is an error at the operator`` () =
    let code, reported, text = onlyDiagnostic "type P = (x: int)\ndef P.`+++`: P, P -> P = { a, b -> a }\n"
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, code)
    Assert.Contains("`+++` is not one of Nanyx's operators, so it cannot be defined", reported)
    Assert.Equal("`+++`: P, P -> P = { a, b -> a }", text)

[<Fact>]
let ``A spare operator with no meaning for its operands is an error at the operator`` () =
    let code, reported, text = onlyDiagnostic "def x = 1 + 2 ^ 3\n"
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("`^` has no built-in meaning, and `int` has no `^` attached. A type gets one by attaching it: def $Money.`^` = ...", reported)
    Assert.Equal("^ 3", text)

[<Fact>]
let ``A composition is a function from what the first takes to what the second returns`` () =
    let result =
        Compiler.compile
            """
def length: string -> int = { s -> s \length }
def isEven = { n -> n % 2 == 0 }
def forward = length >> isEven
def backward = isEven << length
"""
    Assert.Empty(result.Diagnostics)
    for name in [ "forward"; "backward" ] do
        Assert.Equal("string -> bool", Unifier.tyToString (result.Typed.Value.Types |> Map.find name))

[<Fact>]
let ``A composition whose functions do not fit together is a type error`` () =
    let code, _, _ = onlyDiagnostic "def isEven = { n -> n % 2 == 0 }\ndef bad = isEven >> isEven\n"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
// 200 levels is past what a 1 MB stack held in a Debug build. Not 10,000, as #839 asks: checking a chain takes time
// that grows faster than the square of its length (#374), so 10,000 would take hours.
let ``A long chain of operators is checked without running out of stack`` () =
    let source = "def total = 1" + String.replicate 199 " + 1" + "\n"
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    Assert.True(result.TypeChecked)
