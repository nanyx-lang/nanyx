module Nanyx.Compiler.Tests.Features.Definitions.DuplicateFields

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What compiling `source` reports when it names `field` twice in a record: the duplicate-field error at the second.
let private namedTwice (field: string) = DiagnosticCodes.DuplicateField.Code, $"'{field}' is named twice in this record", field

[<Fact>]
let ``A record type that names a field twice is reported there, not at a call`` () =
    let source =
        normalized """def f: (email: string, email: string) -> string = { a, b -> a }
def s = f("x", "y")"""
    Assert.Equal(namedTwice "email", onlyDiagnostic source)
    let span = spanOf (diagnosticOf source)
    Assert.Equal((0, 23), (span.StartLine, span.StartColumn))

[<Fact>]
let ``A type definition's record that names a field twice is reported`` () =
    Assert.Equal(namedTwice "name", onlyDiagnostic "type User = (name: string, name: int)")

[<Fact>]
let ``A call that names an argument twice is reported`` () =
    let source =
        normalized """type User = (name: string, age: int)
def u = User(name = "a", name = "b")"""
    Assert.Equal(namedTwice "name", onlyDiagnostic source)

[<Fact>]
let ``A record value that names a field twice is reported`` () =
    Assert.Equal(namedTwice "x", onlyDiagnostic "def r = (x = 1, x = 2)")

[<Fact>]
let ``A record pattern that names a field twice is reported`` () =
    Assert.Equal(namedTwice "x", onlyDiagnostic "def g = { (x = a, x = b) -> a }")

[<Fact>]
let ``A with that updates a field twice is reported`` () =
    Assert.Equal(namedTwice "x", onlyDiagnostic "def r = (x = 1, y = 2)\ndef w = r with (x = 3, x = 4)")

[<Fact>]
let ``A field after a spread may replace one of the spread's, but not one written before it`` () =
    Assert.Empty((Compiler.compile "def r = (x = 1, y = 2)\ndef t = (...r, x = 3)").Diagnostics)
    Assert.Equal(namedTwice "y", onlyDiagnostic "def r = (x = 1)\ndef s = (...r, y = 1, y = 2)")

[<Fact>]
let ``Positional fields are not named, so they cannot repeat`` () =
    Assert.Empty((Compiler.compile "def p = (1, 1, x = 1)\ndef q: (int, int) = (2, 2)").Diagnostics)

[<Fact>]
let ``Each repeat is reported once`` () =
    let result = Compiler.compile "def r = (x = 1, x = 2, x = 3)"
    Assert.Equal<int list>([ 16; 23 ], result.Diagnostics |> List.map (fun diagnostic -> (spanOf diagnostic).StartColumn))
    Assert.All(result.Diagnostics, fun diagnostic -> Assert.Equal(DiagnosticCodes.DuplicateField.Code, diagnostic.Code))
