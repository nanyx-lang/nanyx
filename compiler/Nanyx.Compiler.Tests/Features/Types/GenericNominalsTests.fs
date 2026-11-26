module Nanyx.Compiler.Tests.Features.Types.GenericNominals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The type of each of `names` in `typed`, as `nanyx check` lists it.
let private shown (typed: TypedModule) (names: string list) =
    names |> List.map (fun name -> name, Unifier.tyToDisplayString Ascii typed.Types.[name])

/// The codes of the diagnostics `source` compiles to.
let private codes (source: string) =
    (Compiler.compile (normalized source)).Diagnostics |> List.map _.Code

[<Fact>]
let ``A written generic protected type is the type its constructor makes`` () =
    let source =
        """
type Box(a) = protected (v: a)

def b = Box(v = 5)
def get: Box(int) -> int = { x -> x.v }
def n = get(b)
def wrap = { x -> Box(v = x) }
def open: Box(a) -> a = { Box(r) -> r.v }
def pair = (Box(v = 1), Box(v = "s"))
"""
    let typed = compileClean (normalized source)
    Assert.Equal<(string * string) list>(
        [ "b", "Box(int)"
          "get", "Box(int) -> int"
          "n", "int"
          "wrap", "a -> Box(a)"
          "open", "Box(a) -> a"
          "pair", "(Box(int), Box(string))" ],
        shown typed [ "b"; "get"; "n"; "wrap"; "open"; "pair" ])

[<Fact>]
let ``A generic record type's constructor is used at a different type each time`` () =
    let source =
        """
type Bag(a) = (items: list(a))

def Bag.size: Bag(a) -> int = { b -> list.length(b.items) }

def x = Bag.size(Bag(items = ["a"]))
def y = Bag.size(Bag(items = [4, 5]))
def strings = Bag(items = ["a"])
def ints = Bag(items = [4, 5])
"""
    let typed = compileClean (normalized source)
    Assert.Equal<(string * string) list>(
        [ "x", "int"; "y", "int"; "strings", "Bag(string)"; "ints", "Bag(int)" ],
        shown typed [ "x"; "y"; "strings"; "ints" ])

[<Fact>]
let ``A generic protected type can be the type of a record's field and a function's result`` () =
    let source =
        """
type Box(a) = protected (v: a)
type Shelf = (left: Box(int), right: Box(string))

def make: int -> Box(int) = { x -> Box(v = x) }
def shelf: Shelf = (left = make(1), right = Box(v = "s"))
def total = shelf.left.v + 1
"""
    let typed = compileClean (normalized source)
    Assert.Equal<(string * string) list>([ "make", "int -> Box(int)"; "total", "int" ], shown typed [ "make"; "total" ])

[<Fact>]
let ``A generic protected type at one argument is not the type at another`` () =
    let source =
        """
type Box(a) = protected (v: a)

def get: Box(int) -> int = { x -> x.v }
def n = get(Box(v = "s"))
"""
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], codes source)

[<Fact>]
let ``A generic type's argument it does not use still tells two of its types apart`` () =
    let source =
        """
type Tagged(a) = protected int

def zero: Tagged(string) = Tagged(0)
def other: Tagged(int) = zero
"""
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], codes source)

[<Fact>]
let ``A generic private type is used outside its module through the functions it exports`` () =
    let boxes =
        """
module mod

export type Box(a) = private (v: a)

export def wrap: a -> Box(a) = { x -> Box(v = x) }
export def unwrap: Box(a) -> a = { b -> b.v }
"""
    let main =
        """
module main

import mod as m

def boxed: m.Box(int) = m.wrap(3)
def n: int = m.unwrap(boxed) + 1
def wrong: m.Box(string) = m.wrap(3)
"""
    let result = compileImporting (normalized boxes) (normalized main)
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], result.Diagnostics |> List.map _.Code)
