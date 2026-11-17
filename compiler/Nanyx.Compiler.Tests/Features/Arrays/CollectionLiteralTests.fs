module Nanyx.Compiler.Tests.Features.CollectionLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Square-bracket literals of any type with a `fromArray` (#1543): `[1, 2]` where an `Array(int)` is expected, and
// `Array[1, 2]`. NYX0257 for a type with none, NYX0258 for a spread of something other than a list or an array.

let private codes (source: string) =
    (Compiler.compile (normalized source)).Diagnostics |> List.map _.Code

[<Fact>]
let ``A literal's type is the expected type, the type named before it, or a list`` () =
    let source =
        """
def a: Array(int) = [1, 2]
def b = Array[1, 2]
def s = set[1, 2]
def m = map["a" => 1]
def l = [1, 2]
def e = []
def n = ["a" => 1]
"""
    let typed = compileClean (normalized source)
    let typeOf name = Unifier.tyToDisplayString Ascii typed.Types.[name]
    Assert.Equal("array(int)", typeOf "a")
    Assert.Equal("array(int)", typeOf "b")
    Assert.Equal("set(int)", typeOf "s")
    Assert.Equal("map(string, int)", typeOf "m")
    Assert.Equal("list(int)", typeOf "l")
    Assert.Equal("list(a)", typeOf "e")
    Assert.Equal("map(string, int)", typeOf "n")

[<Fact>]
let ``A literal written under a typed def's = takes the declared type`` () =
    let source =
        """
type Stock = (name: string, count: int)

def a: Array(int) =
  [1, 2]

def m: Map(string, int) =
  ["a" => 1]

def s: Set(int) =
  def first = 1
  [first, 2]

def stocks: Array(Stock) =
  [| name    | count |
   | "bolts" | 3     |]
"""
    let typed = compileClean (normalized source)
    let typeOf name = Unifier.tyToDisplayString Ascii typed.Types.[name]
    Assert.Equal("array(int)", typeOf "a")
    Assert.Equal("map(string, int)", typeOf "m")
    Assert.Equal("set(int)", typeOf "s")
    Assert.Equal("array(Stock)", typeOf "stocks")

[<Fact>]
let ``An expected type with no fromArray is an error at the literal`` () =
    let source =
        """
type Bag = protected (items: list(int))

def bag: Bag = [1, 2]
"""
    let source = normalized source
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.NoFromArray.Code, diagnostic.Code)
    Assert.StartsWith("'Bag' has no `fromArray`, so a square-bracket literal can't build one", diagnostic.Message)
    Assert.Equal(source.IndexOf "[1, 2]", diagnostic.Span.Value.Start)

[<Fact>]
let ``A name before the brackets with no fromArray is an error at the name`` () =
    let source =
        """
type Bag = protected (items: list(int))

def bag = Bag[1, 2]
"""
    let source = normalized source
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.NoFromArray.Code, diagnostic.Code)
    Assert.Equal(source.IndexOf "Bag[", diagnostic.Span.Value.Start)
    Assert.Equal(source.IndexOf "Bag[" + 3, diagnostic.Span.Value.End)

[<Fact>]
let ``A spread of a set is an error at the set`` () =
    let source =
        """
def s = set[1, 2]
def a = Array[...s, 3]
"""
    Assert.Equal<string list>([ DiagnosticCodes.InvalidCollectionSpread.Code ], codes source)

[<Fact>]
let ``A fromArray that requires a context makes the literal require it`` () =
    let source =
        """
import nanyx/mstack

def outside = MStack[1, 2]
"""
    Assert.Equal<string list>([ DiagnosticCodes.MissingContextMembers.Code ], codes source)
