module Nanyx.Compiler.Tests.Features.Definitions.DefinitionOrder

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A pipe on the result of a function defined later finds the function attached to its type`` () =
    let result =
        compileClean """import nanyx/text

def found = later(5) \contains("x")

def later = { x -> "text {x}" }"""
    Assert.Equal(TyPrimitive "bool", result.Types.["found"])

[<Fact>]
let ``A function's pipe on the result of a function defined later finds the function attached to its type`` () =
    let result =
        compileClean """import nanyx/text

def shout = { n -> later(n) \trim }

def later = { x -> " text {x + 1} " }"""
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "string"), result.Types.["shout"])

[<Fact>]
let ``A function used before its definition is generalised before the use`` () =
    let result =
        compileClean """def a = id(1)
def b = id("s")

def id = { x -> x }"""
    Assert.Equal(TyPrimitive "int", result.Types.["a"])
    Assert.Equal(TyPrimitive "string", result.Types.["b"])

[<Fact>]
let ``Unannotated functions that call each other still check, in the order they are written`` () =
    let result =
        compileClean """import nanyx/text

def check = { n -> isEven(n) \toString \contains("t") }

def isEven = { n -> if n == 0 then true else isOdd(n - 1) }
def isOdd = { n -> if n == 0 then false else isEven(n - 1) }"""
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "bool"), result.Types.["isEven"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "bool"), result.Types.["isOdd"])

[<Fact>]
let ``The typed module keeps its items in the order they are written`` () =
    let result =
        compileClean """def first = second(1)
def second = { x -> x }"""
    let names =
        result.Items
        |> List.choose (function
            | TypedDef (TypedValueDef(_, name, _, _)) -> Some name
            | _ -> None)
    Assert.Equal<string list>([ "first"; "second" ], names)
