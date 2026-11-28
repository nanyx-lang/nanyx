module Nanyx.Compiler.Tests.Features.CollectionLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Square-bracket literals of any type with a `fromArray` (#1543): `[1, 2]` where an `Array(int)` is expected, and
// `Array[1, 2]`. NYX0257 for a type with none, NYX0258 for a spread of something that can't be iterated (#1869).

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
    let typeOf = asciiTypeIn typed
    Assert.Equal("array(int)", typeOf "a")
    Assert.Equal("array(int)", typeOf "b")
    Assert.Equal("set(int)", typeOf "s")
    Assert.Equal("map(string, int)", typeOf "m")
    Assert.Equal("list(int)", typeOf "l")
    Assert.Equal("list(undefined)", typeOf "e")
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
    let typeOf = asciiTypeIn typed
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
let ``A spread of a record is an error at the record`` () =
    let source =
        """
def p = (x = 1, y = 2)
def a = Array[...p, 3]
"""
    let source = normalized source
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.InvalidCollectionSpread.Code, diagnostic.Code)
    Assert.Equal(source.IndexOf "...p" + 3, diagnostic.Span.Value.Start)

[<Fact>]
let ``A list literal that is only a spread of a record is an error at the record`` () =
    let source =
        """
def r = (a = 1)
def xs = [...r]
"""
    let source = normalized source
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.InvalidCollectionSpread.Code, diagnostic.Code)
    Assert.Equal(source.IndexOf "...r" + 3, diagnostic.Span.Value.Start)

[<Fact>]
let ``A spread of a type with no toSeq names the spread and the missing definition`` () =
    let source =
        """
type Point = protected (x: int, y: int)

def a = [0, ...Point(x = 1, y = 2)]
"""
    let diagnostic = Assert.Single (Compiler.compile (normalized source)).Diagnostics
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Contains("Spreading a value of type 'Point' into a collection literal", diagnostic.Message)
    Assert.Contains("'Point.toSeq' is not defined", diagnostic.Message)

[<Fact>]
let ``A list literal that spreads a set is a list of its elements`` () =
    let types = (Compiler.compile (normalized "def s = set[1, 2]\ndef xs = [...s, 3]\n")).Typed.Value.Types
    Assert.Equal("list(int)", Unifier.tyToString types.["xs"])

[<Fact>]
let ``A fromArray that requires a context makes the literal require it`` () =
    let source =
        """
import nanyx/mstack

def outside = MStack[1, 2]
"""
    Assert.Equal<string list>([ DiagnosticCodes.MissingContextMembers.Code ], codes source)

[<Fact>]
let ``A literal builds the type a lambda's result, a record field and a spread's def expect`` () =
    // Not only a def's written type and a call's parameter (#1868).
    let source =
        """
type Box = (xs: Array(int))
def mk: () -> Array(int) = { -> [1, 2] }
def mkBlock: int -> Array(int) = { n ->
  def m = n
  [m, 2]
}
def apply: (int -> Array(int)) -> Array(int) = { f -> f(1) }
def applied = apply({ n -> [n] })
def box: Box = (xs = [1, 2])
def ys = [1, 2]
def spread: Array(int) = [...ys]
"""
    let typeOf = asciiTypeIn (compileClean (normalized source))
    Assert.Equal("() -> array(int)", typeOf "mk")
    Assert.Equal("int -> array(int)", typeOf "mkBlock")
    Assert.Equal("array(int)", typeOf "applied")
    Assert.Equal("Box", typeOf "box")
    Assert.Equal("array(int)", typeOf "spread")

[<Fact>]
let ``A literal ending a lambda whose result is a type variable stays a list`` () =
    let typeOf = asciiTypeIn (compileClean "def mk = { -> [1, 2] }\n")
    Assert.Equal("() -> list(int)", typeOf "mk")
