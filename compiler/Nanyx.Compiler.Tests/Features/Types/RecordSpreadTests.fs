/// Record spread, `(...a, f = v, ...b)`: the type of the record it makes.
module Nanyx.Compiler.Tests.Features.Types.RecordSpread

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A spread record has every field of its parts, a later part's replacing an earlier one's`` () =
    let typed =
        compileClean
            """
def defaults = (host = "localhost", port = 8080)
def env = (port = 9000)
def config = (...defaults, protocol = "https", ...env, port = "none")
"""
    let expected =
        TyRecord(Map.ofList [ "host", TyPrimitive "string"; "port", TyPrimitive "string"; "protocol", TyPrimitive "string" ])
    Assert.Equal(expected, typed.Types["config"])

[<Fact>]
let ``A spread of a nominal record type has its fields`` () =
    let typed =
        compileClean
            """
type Point = (x: int, y: int)
def p = Point(x = 1, y = 2)
def labelled = (...p, label = "origin")
"""
    let expected = TyRecord(Map.ofList [ "label", TyPrimitive "string"; "x", TyPrimitive "int"; "y", TyPrimitive "int" ])
    Assert.Equal(expected, typed.Types["labelled"])

[<Fact>]
let ``A spread of a value whose fields are not known is an error at the value`` () =
    let code, text = singleError "def f = { r -> (...r, a = 1) }"
    Assert.Equal(DiagnosticCodes.InvalidRecordSpread.Code, code)
    Assert.Equal("r", text)
