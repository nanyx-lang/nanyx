module Nanyx.Compiler.Tests.Features.Types.TypeHoles

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `_` in a type: the part of the annotation that inference fills in.

let private typesOf (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value.Types

let private typeOf (source: string) (name: string) = Unifier.tyToString (typesOf source).[name]

[<Fact>]
let ``A hole in a type argument is inferred from the value`` () =
    Assert.Equal("list(int)", typeOf "def xs: list(_) = [1, 2]" "xs")
    Assert.Equal("(string, int)", typeOf """def pair: (string, _) = ("a", 1)""" "pair")

[<Fact>]
let ``A hole in a function type is inferred from the lambda`` () =
    let source =
        normalized """def add: (int, _) -> _ = { a, b -> a + b }
def greet: _ -> string = { name -> "hi " ++ name }"""
    let types = typesOf source
    Assert.Equal("int, int -> int", Unifier.tyToString types.["add"])
    Assert.Equal("string -> string", Unifier.tyToString types.["greet"])

[<Fact>]
let ``A hole for a whole input takes all of a lambda's parameters`` () =
    Assert.Equal("int, bool -> int", typeOf "def f: _ -> _ = { n, b -> if b then n + 1 else 0 }" "f")

[<Fact>]
let ``A hole for a whole type is the same as no annotation`` () =
    let source =
        normalized """def f: _ = { x -> x + 1 }
def n: _ = f(2)"""
    let types = typesOf source
    Assert.Equal("int -> int", Unifier.tyToString types.["f"])
    Assert.Equal("int", Unifier.tyToString types.["n"])

[<Fact>]
let ``Holes are generalised as an unannotated def is, not as named variables`` () =
    // The hole in `inc` is bound to `int` by its body, where `a -> a` would be too general.
    let source =
        normalized """def id: _ -> _ = { x -> x }
def a = id(1)
def b = id("s")
def inc: _ -> _ = { x -> x + 1 }"""
    let types = typesOf source
    Assert.Equal("int", Unifier.tyToString types.["a"])
    Assert.Equal("string", Unifier.tyToString types.["b"])
    Assert.Equal("int -> int", Unifier.tyToString types.["inc"])

[<Fact>]
let ``Each hole is a type of its own`` () =
    Assert.Equal("(int, string)", typeOf """def pair: (_, _) = (1, "a")""" "pair")

[<Fact>]
let ``A hole can be used before its def`` () =
    let types = typesOf "def n = double(4)\ndef double: _ -> _ = { x -> x * 2 }"
    Assert.Equal("int", Unifier.tyToString types.["n"])

[<Fact>]
let ``Holes work in local defs and lambda parameters`` () =
    let source =
        normalized """def total = {
  def xs: list(_) = [1, 2, 3]
  def add = { a: _, b -> a + b }
  match xs
    | [x, ...] -> add(x, 1)
    | [] -> 0
}"""
    Assert.Equal("() -> int", typeOf source "total")

[<Fact>]
let ``The rest of an annotation with a hole is still checked`` () =
    let result = Compiler.compile "def xs: list(_) = \"not a list\""
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, (List.head result.Diagnostics).Code)
    let result = Compiler.compile "def f: (int, _) -> string = { a, b -> a + b }"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, (List.head result.Diagnostics).Code)

[<Fact>]
let ``A hole takes only one type`` () =
    let result = Compiler.compile "def xs: list(_) = [1, 2]\ndef ys: list(string) = xs"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, (List.head result.Diagnostics).Code)

[<Fact>]
let ``A hole cannot be used in a type definition`` () =
    let result = Compiler.compile "type Names = list(_)"
    let diagnostic = List.head result.Diagnostics
    Assert.Equal(DiagnosticCodes.MisplacedTypeHole.Code, diagnostic.Code)
    Assert.Equal("'_' cannot be used in the definition of the type 'Names', only in the type of a value", diagnostic.Message)
    Assert.Equal(offsetOf "type Names = list(_)" "_" 0 0, diagnostic.Span.Value.Start)
    let local = Compiler.compile "def f = {\n  type Pair = (int, _)\n  1\n}"
    Assert.Equal(DiagnosticCodes.MisplacedTypeHole.Code, (List.head local.Diagnostics).Code)
