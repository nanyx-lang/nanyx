module Nanyx.Compiler.Tests.Features.Arithmetic

open Xunit
open NyxCompiler

/// The one diagnostic `source` has: its code, its message and the text it is reported at.
let private onlyDiagnostic (source: string) =
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        let span = diagnostic.Span.Value
        diagnostic.Code, diagnostic.Message, source.Substring(span.Start, span.End - span.Start)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Arithmetic on a type that is not a number is an error naming the operator`` () =
    let cases =
        [ "def s = \"a\" % \"b\"\n", "'string' cannot be used with %: it is not a number", "\"a\" % \"b\""
          "def s = \"a\" * 2\n", "'string' cannot be used with *: it is not a number", "\"a\" * 2"
          "def b = 1 - true\n", "'bool' cannot be used with -: it is not a number", "1 - true"
          "def s = \"a\" + \"b\"\n", "'string' cannot be used with +: it is not a number. Use ++ to join them", "\"a\" + \"b\"" ]
    for source, message, text in cases do
        Assert.Equal((DiagnosticCodes.TypeMismatch.Code, message, text), onlyDiagnostic source)

[<Fact>]
let ``The remainder of two ints is an int and of two floats a float`` () =
    for source, expected in [ "def r = 7 % 2\n", "int"; "def r = 7.5 % 2.0\n", "float"; "def r = { x -> x % 2 }\n", "int -> int" ] do
        let result = Compiler.compile source
        Assert.Empty(result.Diagnostics)
        let ty = result.Typed.Value.Types |> Map.find "r"
        Assert.Equal(expected, Unifier.tyToString ty)

[<Fact>]
let ``A function that writes its type must require Add of a type variable it adds, subtracts or negates`` () =
    let cases =
        [ "def add: a, a -> a = { x, y -> x + y }\n", "'add' adds values of type 'a' with +, so its type must require Add(a): add [Add(a)] to the type of 'add'", "x + y"
          "def sub: a, a -> a = { x, y -> x - y }\n", "'sub' subtracts values of type 'a' with -, so its type must require Add(a): add [Add(a)] to the type of 'sub'", "x - y"
          "def neg: a -> a = { x -> -x }\n", "'neg' negates values of type 'a' with -, so its type must require Add(a): add [Add(a)] to the type of 'neg'", "-x" ]
    for source, message, text in cases do
        Assert.Equal((DiagnosticCodes.MissingContextMembers.Code, message, text), onlyDiagnostic source)

[<Fact>]
let ``Adding and negating values of a type variable calls the members of the Add the function requires`` () =
    let source =
        "def double: [Add(a)] a -> a = { x -> x + x }\n" +
        "def orZero: [Add(a)] a -> a = { x -> x + 0 }\n" +
        "def flip: [Add(a)] a -> a = { x -> 0 - x + -x }\n" +
        "def total = [1.5, 2.5] \\List.sum\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal("float", Unifier.tyToString result.Typed.Value.Types.["total"])

[<Fact>]
let ``Negation without a written type is of an int or a float`` () =
    for source, expected in [ "def r = { x -> -x }\n", "int -> int"; "def r = -2.5\n", "float"; "def r = { x: float -> -x }\n", "float -> float" ] do
        let result = Compiler.compile source
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        Assert.Equal(expected, Unifier.tyToString result.Typed.Value.Types.["r"])
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'string' cannot be used with -: it is not a number", "-\"s\""), onlyDiagnostic "def r = -\"s\"\n")

[<Fact>]
let ``A function that writes its type must require Num of a type variable it multiplies, divides or takes the remainder of`` () =
    let cases =
        [ "def mul: a, a -> a = { x, y -> x * y }\n", "'mul' multiplies values of type 'a' with *, so its type must require Num(a): add [Num(a)] to the type of 'mul'", "x * y"
          "def div: [Add(a)] a, a -> a = { x, y -> x / y }\n", "'div' divides values of type 'a' with /, so its type must require Num(a): add [Num(a)] to the type of 'div'", "x / y"
          "def rem: a, a -> a = { x, y -> x % y }\n", "'rem' takes the remainder of values of type 'a' with %, so its type must require Num(a): add [Num(a)] to the type of 'rem'", "x % y" ]
    for source, message, text in cases do
        Assert.Equal((DiagnosticCodes.MissingContextMembers.Code, message, text), onlyDiagnostic source)

[<Fact>]
let ``A literal beside a value of a type variable is fromInt of it, which needs Num, and cannot be a decimal`` () =
    Assert.Equal(
        (DiagnosticCodes.MissingContextMembers.Code, "'1' is an 'a' here, which needs Num(a): add [Num(a)] to the type of 'inc'", "1"),
        onlyDiagnostic "def inc: [Add(a)] a -> a = { x -> x + 1 }\n")
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'0.5' cannot be an 'a': 'a' may be an integer type. Take the value as a parameter", "0.5"),
        onlyDiagnostic "def half: [Num(a)] a -> a = { x -> x * 0.5 }\n")

[<Fact>]
let ``A function that requires Num multiplies, takes literals and calls one that requires Add`` () =
    let source =
        "def square: [Num(a)] a -> a = { x -> x * x }\n" +
        "def twice: [Num(a)] a -> a = { x -> 2 * x + 0 }\n" +
        "def mean: [Num(a)] list(a) -> a = { xs -> List.sum(xs) / fromInt(List.length(xs)) }\n" +
        "def total = mean([1.5, 2.5]) + square(2.0) + twice(1.5)\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal("float", Unifier.tyToString result.Typed.Value.Types.["total"])
