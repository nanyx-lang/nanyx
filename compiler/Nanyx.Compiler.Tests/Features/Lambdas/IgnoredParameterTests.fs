module Nanyx.Compiler.Tests.Features.Lambdas.IgnoredParameters

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda written without parameters, `{ ... }`, checked against a type that takes one, ignores it (#813).

let private applySource = """def apply: (int -> string) -> string = { f -> f(1) }
"""

let private typeOf (source: string) (name: string) = Unifier.tyToString (compileClean source).Types.[name]

[<Fact>]
let ``A lambda without parameters passed for a one-parameter function ignores the parameter`` () =
    let source = applySource + """def x = apply { "ignored" }
def y = apply({ "ignored" })
"""
    Assert.Equal("string", typeOf source "x")
    Assert.Equal("string", typeOf source "y")

[<Fact>]
let ``A lambda without parameters defined with a one-parameter type ignores the parameter`` () =
    let source = """def g: int -> string = { "top" }
def h = {
  def local: int -> string = { "local" }
  local(2)
}
"""
    Assert.Equal("int -> string", typeOf source "g")
    Assert.Equal("() -> string", typeOf source "h")

[<Fact>]
let ``A lambda written with a unit parameter passed for a one-parameter function is a type mismatch`` () =
    let source = applySource + """def x = apply { () -> "x" }
"""
    let code, at = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("""{ () -> "x" }""", at)

[<Fact>]
let ``A lambda written with a unit parameter defined with a one-parameter type is a type mismatch`` () =
    let code, at = singleError """def k: int -> string = { () -> "x" }
"""
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("""{ () -> "x" }""", at)

[<Fact>]
let ``A lambda without parameters and no expected type takes unit`` () =
    Assert.Equal("() -> int", typeOf "def f = { 0 }\n" "f")

[<Fact>]
let ``A lambda without parameters passed for a function of unit still takes unit`` () =
    let source = """def run: (() -> a) -> a = { f -> f() }
def y = run { 1 }
def z = run { () -> 2 }
"""
    Assert.Equal("int", typeOf source "y")
    Assert.Equal("int", typeOf source "z")
