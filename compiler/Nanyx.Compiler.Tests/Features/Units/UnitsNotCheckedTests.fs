/// Units of measure, which the parser reads and the checker does not check yet: each declaration and unit literal is
/// `NYX0285` where it is written, and nothing else.
module Nanyx.Compiler.Tests.Features.UnitsNotChecked

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The code and the text each diagnostic of compiling `source` is reported at.
let private reported (source: string) =
    let source = withFinalNewline source
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

let private code = DiagnosticCodes.UnitsNotChecked.Code

[<Fact>]
let ``Each dimension and unit declaration is NYX0285 at the declaration, and @baseUnit is no unknown attribute`` () =
    Assert.Equal<(string * string) list>(
        [ code, "dimension Time"; code, "unit ns: Time"; code, "unit ms: Time = 1_000_000ns"; code, "dimension Bytes(int)" ],
        reported "dimension Time\n@baseUnit unit ns: Time\nunit ms: Time = 1_000_000ns\ndimension Bytes(int)")

[<Fact>]
let ``A unit literal is NYX0285 at the literal`` () =
    Assert.Equal<(string * string) list>([ code, "500ms" ], reported "def x = 500ms")
    Assert.Equal<(string * string) list>([ code, "-0.5s" ], reported "def y = -0.5s")

[<Fact>]
let ``A unit literal in a guard is NYX0285 at the literal`` () =
    Assert.Equal<(string * string) list>(
        [ code, "0ms" ],
        reported "def f: int -> bool = { t ->\n  match t\n    | == 0ms -> true\n    | _ -> false\n}")

[<Fact>]
let ``dimension and unit are names a program can still use`` () =
    Assert.Empty((Compiler.compile "def unit = 1\ndef dimension = 2\ndef sum = unit + dimension\n").Diagnostics)
