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
