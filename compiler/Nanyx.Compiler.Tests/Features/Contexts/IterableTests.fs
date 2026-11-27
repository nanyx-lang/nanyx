module Nanyx.Compiler.Tests.Features.Iterable

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `Iterable(c)`, the prelude's context of what can be iterated, with the associated type `elem`: a type gets it by
// attaching `toSeq`, whose type fixes `elem` (see docs/src/content/deep-dives/lazy-sequences.md).

let private bag =
    """type Bag(a) = protected (items: list(a))

def Bag.toSeq: Bag(a) -> seq(a) = { b -> list.toSeq(b.items) }

def elements: [Iterable(c)] c -> seq(elem) = { xs -> toSeq(xs) }
"""

[<Fact>]
let ``toSeq fixes elem, for a user's type and for the prelude's`` () =
    let types =
        definedTypes (
            bag
            + """
def fromBag = elements(Bag(items = ["a"]))
def fromList = elements([1, 2])
def fromArray = elements(Array[true])
def fromMap = elements(["a" => 1])
"""
        )
    Assert.Equal("seq(string)", Unifier.tyToString types.["fromBag"])
    Assert.Equal("seq(int)", Unifier.tyToString types.["fromList"])
    Assert.Equal("seq(bool)", Unifier.tyToString types.["fromArray"])
    Assert.Equal("seq((string, int))", Unifier.tyToString types.["fromMap"])

[<Fact>]
let ``a constraint that fixes elem rejects an iterable of something else`` () =
    let source =
        """def shout: [Iterable(c, elem = string)] c -> int = { words -> seq.fold(toSeq(words), 0, { n, w -> n + string.length(w) }) }

def n = shout([1, 2])
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Contains("'Iterable(list(int), elem = string)'", diagnostic.Message)

[<Fact>]
let ``another context may name the associated type an Iterable fixes`` () =
    definedTypes """def describe: [Iterable(c), ToString(elem)] c -> list(string) = { xs -> seq.toList(seq.map(toSeq(xs), { x -> "{x}" })) }

def described = describe([1, 2])
"""
    |> ignore

[<Fact>]
let ``an unannotated def that loops over its parameter requires Iterable of it`` () =
    let source =
        """def walk = { items -> for items | item -> println("{item}") }

def iter = { xs, f -> for xs | x -> f(x) }
"""
    compileClean (normalized source) |> ignore
    // `walk`'s type doesn't name `elem`: it is quantified as the def's own, so the def requires `ToString` of it.
    Assert.Equal(Some "def walk: [Iterable(α, elem = β), ToString(β), Console] α -> ()", hoverType source "walk =")
    Assert.Equal(Some "def iter: [Iterable(α, elem = β), κ] α, ([κ] β -> γ) -> ()", hoverType source "iter =")

[<Fact>]
let ``an unannotated def that loops over its parameter is called with a list and with a user's type`` () =
    let types =
        definedTypes (
            bag
            + """
def count = { xs ->
  for xs | x -> println("{x}")
  0
}

def ofList = { () -> count([1, 2]) }
def ofBag = { () -> count(Bag(items = ["a"])) }
"""
        )
    Assert.Equal("() -> int", Unifier.tyToString types.["ofList"])
    Assert.Equal("() -> int", Unifier.tyToString types.["ofBag"])

[<Fact>]
let ``a for loop over a type with no toSeq names the loop and the missing definition`` () =
    let source =
        """type Point = (x: int, y: int)

def f = for Point(x = 1, y = 2) | p -> p
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Contains("A `for` loop over a value of type 'Point'", diagnostic.Message)
    Assert.Contains("'Point.toSeq' is not defined", diagnostic.Message)
