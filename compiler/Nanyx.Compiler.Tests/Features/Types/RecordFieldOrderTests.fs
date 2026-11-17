module Nanyx.Compiler.Tests.Features.Types.RecordFieldOrder

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A record type keeps the order its fields are written in, to be shown in it: a signature's named parameters, a
// record and a tag's payload, in hover, `nanyx check`, error messages and the standard library's pages.

/// The type of each top-level def of `source`, as `nanyx check` writes it.
let private typesOf (source: string) =
    let result = Compiler.compile (normalized source)
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    result.Typed.Value.Types |> Map.map (fun _ ty -> Unifier.tyToDisplayString TypeNotation.Source ty)

[<Fact>]
let ``Named parameters are written in the order they are declared`` () =
    let types =
        typesOf
            """def slice: (string, start: int, length: int) -> string = { s, start, length -> s }
def made = (zeta = 1, alpha = 2)
"""
    Assert.Equal("(string, start: int, length: int) -> string", types.["slice"])
    Assert.Equal("(zeta: int, alpha: int)", types.["made"])

[<Fact>]
let ``An imported definition's named parameters are written as its module declares them`` () =
    Assert.Equal(
        Some "def string.substring: (string, start: int, length: int) -> string",
        hoverType "def part = string.substring(\"hello\", start = 1, length = 2)\n" "substring")

[<Fact>]
let ``The annotation's order wins over the value's`` () =
    Assert.Equal("(y: int, x: int)", (typesOf "def point: (y: int, x: int) = (x = 1, y = 2)\n").["point"])

[<Fact>]
let ``A record and a tag's payload are written in order in an error message`` () =
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "Type mismatch: '(zeta: int, alpha: int)' vs 'int'", "(zeta = 1, alpha = 2)"),
        onlyDiagnostic "def n: int = (zeta = 1, alpha = 2)\n")
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "Type mismatch: '#box(width: int, height: int)' vs 'int'", "#box(width = 1, height = 2)"),
        onlyDiagnostic "def t: int = #box(width = 1, height = 2)\n")

[<Fact>]
let ``Two records written in different orders are one type`` () =
    Assert.Equal(TyRecord(Map.ofList [ "x", TyPrimitive "int" ], FieldOrder [ "x" ]), TyRecord(Map.ofList [ "x", TyPrimitive "int" ], FieldOrder.none))
    Assert.Empty((Compiler.compile "type P = (x: int, y: int)\ndef p: P = (y = 2, x = 1)\n").Diagnostics)
