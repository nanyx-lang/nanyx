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
        TyRecord(Map.ofList [ "host", TyPrimitive "string"; "port", TyPrimitive "string"; "protocol", TyPrimitive "string" ], FieldOrder.none)
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
    let expected = TyRecord(Map.ofList [ "label", TyPrimitive "string"; "x", TyPrimitive "int"; "y", TyPrimitive "int" ], FieldOrder.none)
    Assert.Equal(expected, typed.Types["labelled"])

[<Fact>]
let ``A spread of a value whose fields are not known is an error at the value`` () =
    let code, text = singleError "def f = { r -> (...r, a = 1) }"
    Assert.Equal(DiagnosticCodes.InvalidRecordSpread.Code, code)
    Assert.Equal("r", text)

[<Fact>]
let ``A spread of an open record that sets its known fields is of the record's own type`` () =
    // Only the fields it is known to have: the result is the open record's type, whatever else it has (#1866).
    let typed =
        compileClean
            """
def moveRight: (x: int, ...r) -> (x: int, ...r) = { p -> (...p, x = p.x + 1) }
def moved = moveRight(x = 1, y = 2, label = "a")
"""
    Assert.Equal("(x: int, y: int, label: string)", asciiTypeIn typed "moved")

[<Fact>]
let ``A spread of an open record that adds a field, or is joined with another record, is an error`` () =
    let added = Compiler.compile "def f: (x: int, ...r) -> int = { p ->\n  def q = (...p, z = 1)\n  0\n}\n"
    let diagnostic = Assert.Single added.Diagnostics
    Assert.Equal(DiagnosticCodes.InvalidRecordSpread.Code, diagnostic.Code)
    Assert.Equal(
        "A spread of a record that may have more fields can only set the fields it is known to have, and 'z' isn't one of them: write the record's type with all its fields, closed, as in `(x: int, y: int)`, to make a record of other fields",
        diagnostic.Message)
    let code, text = singleError "def f: (x: int, ...r) -> int = { p ->\n  def q = (a = 1, ...p)\n  0\n}"
    Assert.Equal((DiagnosticCodes.InvalidRecordSpread.Code, "p"), (code, text))

[<Fact>]
let ``A field a spread of an open record sets keeps its type`` () =
    let code, text = singleError "def f: (x: int, ...r) -> (x: int, ...r) = { p -> (...p, x = \"two\") }"
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "\"two\""), (code, text))
