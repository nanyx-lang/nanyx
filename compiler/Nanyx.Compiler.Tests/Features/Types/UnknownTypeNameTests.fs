module Nanyx.Compiler.Tests.Features.Types.UnknownTypeNames

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics compiling `source` gives: each one's code and the text it is reported at.
let private compiled (source: string) =
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

[<Fact>]
let ``A type name in a def's annotation that names no type is reported`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Strnig" ], compiled "def x: Strnig = \"a\"\n")
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnknownType.Code, "Strnig"; DiagnosticCodes.UnknownType.Code, "Nmuber" ],
        compiled "def f: (Strnig, list(Nmuber)) -> Strnig = { s, _ -> s }\n")

[<Fact>]
let ``A type name in a lambda parameter's annotation that names no type is reported`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Foo" ], compiled "def f = { (x: Foo) -> x }\n")

[<Fact>]
let ``A type name in a type definition that names no type is reported`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Foo" ], compiled "type T = (a: Foo)\n")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Missing" ], compiled "def f = {\n  type T = #some(Missing)\n  1\n}\n")

[<Fact>]
let ``Built-in types, type variables, type parameters and types defined anywhere in the module are known`` () =
    let source =
        normalized """def n: int = 1
def xs: list(string) = [ "a" ]
def ys: seq(bool) -> seq(bool) = { s -> s }
def id: a -> a = { x -> x }
def t: Tree = #leaf
type Tree = #leaf | #node(Tree, Tree)
type Box(t) = (value: t)
type Pair(a, b) = (first: a, second: b)
def g = { (p: Pair(int, Tree)), (b: Box(string)) -> (p.first, b.value) }
"""
    Assert.Empty(compiled source)
