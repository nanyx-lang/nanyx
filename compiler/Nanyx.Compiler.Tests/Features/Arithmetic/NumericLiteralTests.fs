module Nanyx.Compiler.Tests.Features.NumericLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The type of `name` in `source`, which has no diagnostics, as the compiler prints it.
let private typeOf (name: string) (source: string) =
    Unifier.tyToString ((compileClean (source.ReplaceLineEndings "\n")).Types.[name])

[<Fact>]
let ``A literal takes the numeric type the code around it expects`` () =
    let source =
        """
def small: U8 = 200
def ratio: F32 = 0.5
def huge: U64 = 18_446_744_073_709_551_615
def inc = { (x: I32) -> x + 1 }
def scale: U16 -> U16 = { n -> n * 2 }
def scaled = scale(7)
def bytes: list(U8) = [1, 2, 255]
def classify = { (b: I8) ->
  match b
  | -128 -> "smallest"
  | _ -> "other"
}
"""
    Assert.Equal("U8", typeOf "small" source)
    Assert.Equal("F32", typeOf "ratio" source)
    Assert.Equal("U64", typeOf "huge" source)
    Assert.Equal("I32 -> I32", typeOf "inc" source)
    Assert.Equal("U16", typeOf "scaled" source)
    Assert.Equal("list(U8)", typeOf "bytes" source)
    Assert.Equal("I8 -> string", typeOf "classify" source)

[<Fact>]
let ``A literal nothing settles is an int, or a float with a decimal point`` () =
    let source =
        """
def big = 3_000_000_000
def half = 0.5
def nan = 0.0 / 0.0
def local = {
  def n = 5
  n
}
def odd = { x -> x % 2 }
"""
    Assert.Equal("int", typeOf "big" source)
    Assert.Equal("float", typeOf "half" source)
    Assert.Equal("float", typeOf "nan" source)
    Assert.Equal("() -> int", typeOf "local" source)
    Assert.Equal("int -> int", typeOf "odd" source)

[<Theory>]
[<InlineData("def a: U8 = 300", "'300' does not fit in U8 (0 to 255)", "300")>]
[<InlineData("def b: I8 = -129", "'-129' does not fit in I8 (-128 to 127)", "-129")>]
[<InlineData("def c = 9_223_372_036_854_775_808", "'9223372036854775808' does not fit in int (-9223372036854775808 to 9223372036854775807)", "9_223_372_036_854_775_808")>]
[<InlineData("def d: U64 = -1", "'-1' does not fit in U64 (0 to 18446744073709551615)", "-1")>]
[<InlineData("def e = { (x: U16) -> x + 70000 }", "'70000' does not fit in U16 (0 to 65535)", "70000")>]
let ``A literal that does not fit its type is an error`` (source: string, message: string, text: string) =
    Assert.Equal((DiagnosticCodes.LiteralOutOfRange.Code, message, text), onlyDiagnostic source)

[<Fact>]
let ``A pattern literal that does not fit its type is an error`` () =
    let source =
        """
def classify = { (b: U8) ->
  match b
  | 256 -> "past"
  | _ -> "other"
}
"""
    Assert.Equal((DiagnosticCodes.LiteralOutOfRange.Code, "'256' does not fit in U8 (0 to 255)", "256"), onlyDiagnostic source)

[<Fact>]
let ``A decimal literal past the largest F32 is an error`` () =
    let code, message, text = onlyDiagnostic "def g: F32 = 1.0e39"
    Assert.Equal(DiagnosticCodes.LiteralOutOfRange.Code, code)
    Assert.StartsWith("'1E+39' does not fit in F32", message)
    Assert.Equal("1.0e39", text)

[<Theory>]
[<InlineData("def d: float = 1", "'1' is an integer literal, so it cannot be a 'float': write '1.0'", "1")>]
[<InlineData("def d = { (x: F32) -> x * 2 }", "'2' is an integer literal, so it cannot be a 'F32': write '2.0'", "2")>]
[<InlineData("def e: string = 2", "Type mismatch: int vs string.", "2")>]
[<InlineData("def f: I32 = 2.5", "Type mismatch: float vs I32.", "2.5")>]
let ``A literal is not of a type of another kind`` (source: string, message: string, text: string) =
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, message, text), onlyDiagnostic source)

[<Fact>]
let ``The fix for an integer literal where a float is expected writes it as one`` () =
    let source = "def d: float = 0x1F\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    let fixedSource = applyFix source (List.exactlyOne diagnostic.Fixes)
    Assert.Equal("def d: float = 31.0\n", fixedSource)
    Assert.Empty((Compiler.compile fixedSource).Diagnostics)
