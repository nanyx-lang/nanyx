/// An error about a record's field is reported at the field's name, or at its value where that is what doesn't fit, in a
/// record literal, a record pattern, a record type and a field access (#2718).
module Nanyx.Compiler.Tests.Features.FieldNameErrors

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private point = "type P = (width: int, height: int)\n"

/// `source` matching a `P` with `pattern`.
let private matching (pattern: string) =
    point + $"def f = {{ (p: P) ->\n  match p\n    | {pattern} -> 1\n}}"

let private mismatch = DiagnosticCodes.TypeMismatch.Code
let private duplicate = DiagnosticCodes.DuplicateField.Code
let private unknownMember = DiagnosticCodes.UnknownMember.Code

[<Fact>]
let ``A record literal's unknown, misspelt or repeated field is reported at its name`` () =
    Assert.Equal<(string * string) list>([ mismatch, "z" ], reportedAt (point + "def p: P = (width = 1, z = 2)"))
    Assert.Equal<(string * string) list>([ mismatch, "hieght" ], reportedAt (point + "def p: P = (width = 1, hieght = 2)"))
    Assert.Equal<(string * string) list>([ duplicate, "width" ], reportedAt "def p = (width = 1, width = 2)")

[<Fact>]
let ``A record literal's field value of the wrong type is reported at the value`` () =
    Assert.Equal<(string * string) list>([ mismatch, "\"s\"" ], reportedAt (point + "def p: P = (width = 1, height = \"s\")"))

[<Fact>]
let ``A record pattern's unknown, misspelt or repeated field is reported at its name`` () =
    Assert.Equal<(string * string) list>([ mismatch, "z" ], reportedAt (matching "(width = a, z = b)"))
    Assert.Equal<(string * string) list>([ mismatch, "hieght" ], reportedAt (matching "(width = a, hieght = b)"))
    Assert.Equal<(string * string) list>([ duplicate, "width" ], reportedAt (matching "(width = a, width = b)"))

[<Fact>]
let ``A record type's repeated field is reported at its name`` () =
    Assert.Equal<(string * string) list>([ duplicate, "width" ], reportedAt "type P = (width: int, width: string)")

[<Fact>]
let ``A field access's unknown or misspelt field is reported at its name`` () =
    let value = point + "def p: P = (width = 1, height = 2)\n"
    Assert.Equal<(string * string) list>([ unknownMember, "z" ], reportedAt (value + "def q = p.z"))
    Assert.Equal<(string * string) list>([ unknownMember, "hieght" ], reportedAt (value + "def q = p.hieght"))
