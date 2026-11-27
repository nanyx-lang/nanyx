/// `char`, one Unicode scalar value, and the string literal that is one where a `char` is expected (#2596).
module Nanyx.Compiler.Tests.Features.CharLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A string literal is a char where a char is expected, and a string anywhere else`` () =
    let types = definedTypes "def initial: char = \"A\"\ndef smile: char = \"\\u{1F600}\"\ndef raw: char = 'x'\ndef text = \"A\"\n"
    Assert.Equal("char", Unifier.tyToString types.["initial"])
    Assert.Equal("char", Unifier.tyToString types.["smile"])
    Assert.Equal("char", Unifier.tyToString types.["raw"])
    Assert.Equal("string", Unifier.tyToString types.["text"])

[<Fact>]
let ``A string literal given for a char parameter, or in a list of chars, is a char`` () =
    let source =
        "def isA: char -> bool = { c -> c == \"A\" }\n\ndef yes = isA(\"A\")\n\ndef letters: list(char) = [\"a\", \"b\"]\n"
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``A literal where a char is expected that is not one scalar value is NYX0284 at the literal`` () =
    let code = DiagnosticCodes.NotOneChar.Code
    Assert.Equal<(string * string) list>([ code, "\"\"" ], reportedAt "def a: char = \"\"")
    Assert.Equal<(string * string) list>([ code, "\"ab\"" ], reportedAt "def b: char = \"ab\"")
    Assert.Equal<(string * string) list>([ code, "\"{1}\"" ], reportedAt "def c: char = \"{1}\"")
    Assert.Equal<(string * string) list>([ code, "\"e\\u{301}\"" ], reportedAt "def d: char = \"e\\u{301}\"")

[<Fact>]
let ``A char literal that is a letter and a combining mark says so`` () =
    let _, message, _ = onlyDiagnostic (withFinalNewline "def d: char = \"e\\u{301}\"")
    Assert.Contains("a combining mark", message)

[<Fact>]
let ``String literals in a match on a char are chars`` () =
    let source =
        """def isVowel: char -> bool = { c ->
  match c
    | "a" | "e" | "i" | "o" | "u" -> true
    | _ -> false
}"""
    Assert.Empty((Compiler.compile (withFinalNewline source)).Diagnostics)
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.NotOneChar.Code, "\"ab\"" ],
        reportedAt "def f: char -> bool = { c ->\n  match c\n    | \"ab\" -> true\n    | _ -> false\n}")

[<Fact>]
let ``A string literal compared with a char is one, on either side`` () =
    let source = "def c: char = \"b\"\n\ndef a = c == \"b\"\n\ndef b = \"a\" < c\n"
    Assert.Empty((Compiler.compile source).Diagnostics)
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NotOneChar.Code, "\"bb\"" ], reportedAt "def c: char = \"b\"\n\ndef a = \"bb\" == c")

[<Fact>]
let ``A mismatch of a char and a string says how to make a literal a char`` () =
    let code, message, _ = onlyDiagnostic (withFinalNewline "def f = { x -> x == \"a\" }\n\ndef g: char -> bool = f")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.EndsWith("a string literal is a char only where a char is expected, so write the type, as in `{ c: char -> ... }`", message)

[<Fact>]
let ``A mismatch of a string and a char says how to make a literal a char too`` () =
    let code, message, _ = onlyDiagnostic (withFinalNewline "def s: string = \"ab\"\n\ndef c: char = s")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'string' vs 'char': a string literal is a char only where a char is expected, so write the type, as in `{ c: char -> ... }`", message)

[<Fact>]
let ``A string's chars are a list of chars, and chars make a string`` () =
    let types = definedTypes "def cs = \"héllo\" \\string.chars\ndef s = string.fromChars(cs)\ndef n = char.toInt(\"A\")\ndef c = char.fromInt(65)\n"
    Assert.Equal("list(char)", Unifier.tyToString types.["cs"])
    Assert.Equal("string", Unifier.tyToString types.["s"])
    Assert.Equal("int", Unifier.tyToString types.["n"])
    Assert.Equal("#some(char) | #invalidCodePoint", Unifier.tyToString types.["c"])
