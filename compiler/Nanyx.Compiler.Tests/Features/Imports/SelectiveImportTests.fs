module Nanyx.Compiler.Tests.Features.SelectiveImports

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private util =
    [ "util.nyx",
      """module util

export def double: int -> int = { x -> x * 2 }
export def triple: int -> int = { x -> x * 3 }
export type Point = (x: int, y: int)
export def origin: Point = Point(x = 0, y = 0)""" ]

[<Fact>]
let ``A selection brings in the names it selects`` () =
    let result = compileImportingAll util "module main\nimport util as (double, triple)\ndef value = double(triple(1))"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types["value"])

[<Fact>]
let ``A selection does not bring in the names it leaves out`` () =
    let source = "module main\nimport util as (double)\ndef value = triple(1)"
    let code, message, at = onlyDiagnosticIn source (compileImportingAll util source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("Unknown function 'triple'", message)
    Assert.Equal("triple", at)

[<Fact>]
let ``A selection with an alias brings in the rest under the alias`` () =
    let result = compileImportingAll util "module main\nimport util as u & (double)\ndef value = double(u.triple(1))"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A name the module does not export is an error at the name, with the closest export`` () =
    let source = "module main\nimport util as (double, tripel)\ndef value = double(1)"
    let code, message, at = onlyDiagnosticIn source (compileImportingAll util source)
    Assert.Equal(DiagnosticCodes.NotExported.Code, code)
    Assert.Equal("'util' does not export 'tripel': did you mean 'triple'?", message)
    Assert.Equal("tripel", at)

[<Fact>]
let ``A type the selection leaves out cannot be written`` () =
    let source = "module main\nimport util as (origin)\ndef p: Point = origin"
    let code, _, at = onlyDiagnosticIn source (compileImportingAll util source)
    Assert.Equal(DiagnosticCodes.UnknownType.Code, code)
    Assert.Equal("Point", at)

[<Fact>]
let ``A selected type can be written`` () =
    let result = compileImportingAll util "module main\nimport util as (Point)\ndef p: Point = Point(x = 1, y = 2)"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Selecting a name resolves a name two imports export`` () =
    let modules =
        [ "csvParser.nyx", "module csvParser\nexport def parse = { s -> 1 }\nexport def csvOnly = 1"
          "jsonParser.nyx", "module jsonParser\nexport def parse = { s -> 2 }" ]
    let result = compileImportingAll modules (normalized """module main
import csvParser as (csvOnly)
import jsonParser
def data = parse("x") + csvOnly""")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")