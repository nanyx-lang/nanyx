module Nanyx.Compiler.Tests.Features.ImportedGenericTypes

open Xunit
open Nanyx.Compiler.Tests.TestSupport

/// A module `mod` that exports the generic record type `Box`, the generic nominal `Obj` with a function that makes
/// one, and a function of each whose `Eq` of the type's argument is inferred.
let private boxes =
    normalized
        """module mod

export type Box(a) = (v: a)

export type Obj(a) = protected (members: list((string, a)))

export def Obj.make = { members: list((string, a)) -> Obj(members = members) }

export def sameBox = { x: Box(a), y: Box(a) -> x == y }

export def sameObj = { x: Obj(a), y: Obj(a) -> x.members == y.members }
"""

/// The types of `names` in `main`, compiled beside `boxes`, which must give no diagnostics.
let private typesBesideBoxes (main: string) (names: string list) =
    let result = compileImporting boxes (normalized main)
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    names |> List.map (typeIn result.Typed.Value)

[<Fact>]
let ``A generic record type's constructor makes a value of each type it is given`` () =
    let source =
        """type Box(a) = (v: a)

def b = Box(v = 1)
def c = Box(v = "x")
"""
    Assert.Equal<string list>([ "Box(int)"; "Box(string)" ], typesOf (normalized source) [ "b"; "c" ])

[<Fact>]
let ``An imported generic record type's constructor makes a value of the type it is given`` () =
    // The type's parameter is numbered by the module it came from, as the first variable here is (#2695).
    let main =
        """module main

import mod as (Box)

def b = Box(v = 1)
def c = Box(v = "x")
"""
    Assert.Equal<string list>([ "Box(int)"; "Box(string)" ], typesBesideBoxes main [ "b"; "c" ])

[<Fact>]
let ``An imported function whose Eq of a type's argument is inferred is called with values of no written type`` () =
    let main =
        """module main

import mod as (Box, Obj, sameBox, sameObj)

def p = Obj.make([("a", "1")])
def boxes = sameBox(Box(v = 1), Box(v = 1))
def objs = sameObj(p, p)
"""
    Assert.Equal<string list>([ "Obj(string)"; "bool"; "bool" ], typesBesideBoxes main [ "p"; "boxes"; "objs" ])
