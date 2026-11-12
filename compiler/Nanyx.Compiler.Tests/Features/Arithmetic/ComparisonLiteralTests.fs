module Nanyx.Compiler.Tests.Features.ComparisonLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``An integer literal beside a comparison of a value of a type variable is zero or fromInt of it`` () =
    // #678: the literal was an `int`, so each function was more general than its inferred type.
    let typed =
        compileClean (
            """
def isNegative: [Num(a) & Ord(a)] a -> bool = { n -> n < 0 }
def atMost: [Num(a) & Ord(a)] a -> bool = { n -> n <= 3 }
def above: [Num(a) & Ord(a)] a -> bool = { n -> 3 > n }
def atLeast: [Num(a) & Ord(a)] a -> bool = { n -> n >= 1 }
def isZero: [Num(a) & Eq(a)] a -> bool = { n -> 0 == n }
def notTwo: [Num(a) & Eq(a)] a -> bool = { n -> n != 2 }
def checks = [isNegative(-2.5), atMost(3), above(1.5), atLeast(1), isZero(0.0), notTwo(2)]
"""
                .ReplaceLineEndings "\n"
        )
    Assert.Equal("list(bool)", Unifier.tyToString typed.Types.["checks"])

[<Fact>]
let ``An integer literal beside a comparison needs Num, and a decimal one is an error`` () =
    Assert.Equal(
        ("'1' is an 'a' here, which needs Num(a): add [Num(a)] to the type of 'isOne'", "1"),
        missingContext "def isOne: [Add(a) & Eq(a)] a -> bool = { n -> n == 1 }\n")
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "0.5"),
        singleError "def isHalf: [Num(a) & Eq(a)] a -> bool = { n -> n == 0.5 }\n")

[<Fact>]
let ``An integer literal beside a comparison outside generic code is an int`` () =
    let typed =
        compileClean (
            """
def small = { n -> n < 2 }
def same = { n -> 0 == n }
def sorted = { x: float -> x >= 1.5 }
"""
                .ReplaceLineEndings "\n"
        )
    Assert.Equal("int -> bool", Unifier.tyToString typed.Types.["small"])
    Assert.Equal("int -> bool", Unifier.tyToString typed.Types.["same"])
    Assert.Equal("float -> bool", Unifier.tyToString typed.Types.["sorted"])
