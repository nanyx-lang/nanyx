module Nanyx.Compiler.Tests.Features.ReExports

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private modules =
    [ "util.nyx",
      """module util

export def double: int -> int = { x -> x * 2 }
export def triple: int -> int = { x -> x * 3 }
export type Point = (x: int, y: int)"""
      "c.nyx",
      """module c

export util (double, Point)

export def quadruple: int -> int = { x -> double(double(x)) }"""
      "d.nyx", "module d\n\nexport c (double)" ]

let private clean (result: CompileResult) =
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``An importer of a module gets what it re-exports, with its own exports`` () =
    clean (compileImportingAll modules "module main\nimport c\ndef p: Point = Point(x = 1, y = 2)\ndef value = double(quadruple(1))")

[<Fact>]
let ``What a module re-exports is under the alias it is imported with`` () =
    clean (compileImportingAll modules "module main\nimport c as cc\ndef value = cc.double(cc.quadruple(1))")

[<Fact>]
let ``An importer does not get what the module imports but does not re-export`` () =
    let source = "module main\nimport c\ndef value = triple(1)"
    let diagnostic = Assert.Single((compileImportingAll modules source).Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)
    Assert.Equal("Unknown function 'triple'", diagnostic.Message)

[<Fact>]
let ``A selection can name what a module re-exports`` () =
    clean (compileImportingAll modules "module main\nimport c as (double)\ndef value = double(1)")

[<Fact>]
let ``A re-export of a re-export reaches the definition`` () =
    clean (compileImportingAll modules "module main\nimport d\ndef value = double(1)")

[<Fact>]
let ``A re-exported name refers to the definition in the module it comes from`` () =
    let result = compileImportingAll modules "module main\nimport c\ndef value = double(1)"
    clean result
    let typed = result.Typed.Value
    let declared =
        typed.References
        |> Map.values
        |> Seq.choose (fun id -> typed.Symbols |> Map.tryFind id)
        |> Seq.find (fun symbol -> symbol.Name = "double")
    Assert.EndsWith("util.nyx", declared.Declaration.File)

[<Fact>]
let ``Re-exporting a name the module does not export is an error at the name`` () =
    let source = "module main\nexport util (quadruple)"
    let diagnostic = Assert.Single((compileImportingAll modules source).Diagnostics)
    Assert.Equal(DiagnosticCodes.NotExported.Code, diagnostic.Code)
    Assert.Equal("'util' does not export 'quadruple'", diagnostic.Message)
    Assert.Equal("quadruple", textAt source diagnostic.Span.Value)