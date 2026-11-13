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
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "string", []), result.Types.["shout"])

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
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "bool", []), result.Types.["isEven"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "bool", []), result.Types.["isOdd"])

// The prelude is one module across its files: a def that reads `**` and is used from an earlier file was checked
// before `__pow`, whose `Num(a)` was then not yet required, and so was passed no instance (#1113).
[<Fact>]
let ``A def that uses an operator is inferred after the prelude function the operator calls`` () =
    let source =
        """def scale = { m, e -> m * 10.0 ** e }
def twice = { f -> f >> f }
def __pow = { base, exponent -> base }
def __composeForward = { f, g -> { x -> g(f(x)) } }"""
    let items =
        match Parser.Program.Api.parseModule source with
        | Ok items -> items
        | Error message -> failwith message
    let order = DefinitionOrder.inferredAfter items
    Assert.Equal<int list>([ 2 ], order.[0])
    Assert.Equal<int list>([ 3 ], order.[1])

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
