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
    Assert.Equal("'triple' is not in scope", diagnostic.Message)

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

/// `shapes`, whose exports `geometry` re-exports whole, and `solids`, which re-exports `geometry` whole in turn.
let private wholeModules =
    [ "shapes.nyx",
      """
module shapes

export type Shape = (width: int, height: int)
export def area: Shape -> int = { s -> s.width * s.height }
export def perimeter: Shape -> int = { s -> 2 * (s.width + s.height) }
"""
      "geometry.nyx",
      """
module geometry

export shapes

export def square: int -> Shape = { n -> Shape(width = n, height = n) }
"""
      "solids.nyx",
      """
module solids

export geometry
""" ]
    |> List.map (fun (file, text) -> file, text.ReplaceLineEndings "\n")

[<Fact>]
let ``A whole-module re-export gives everything the module exports`` () =
    clean (compileImportingAll wholeModules "module main\nimport geometry\ndef value = area(square(2)) + perimeter(Shape(width = 1, height = 2))")

[<Fact>]
let ``A whole-module re-export imports the module into the one that writes it`` () =
    let source = """
module geometry

export shapes

export def square: int -> Shape = { n -> Shape(width = n, height = n) }
export def twice: Shape -> int = { s -> area(s) * 2 }
"""
    clean (compileImportingAll (List.take 1 wholeModules) (source.ReplaceLineEndings "\n"))

[<Fact>]
let ``Whole-module re-exports chain`` () =
    clean (compileImportingAll wholeModules "module main\nimport solids as s\ndef value = s.area(s.square(2))")

[<Fact>]
let ``A selection can name what a whole-module re-export gives`` () =
    clean (compileImportingAll wholeModules "module main\nimport solids as (area, square)\ndef value = area(square(2))")

[<Fact>]
let ``A selection of what a whole-module re-export does not give is an error`` () =
    let diagnostic = Assert.Single((compileImportingAll wholeModules "module main\nimport solids as (volume)\ndef value = 1").Diagnostics)
    Assert.Equal(DiagnosticCodes.NotExported.Code, diagnostic.Code)

[<Fact>]
let ``A name a whole-module re-export gives refers to the definition in the module it comes from`` () =
    let result = compileImportingAll wholeModules "module main\nimport solids\ndef value = area(square(2))"
    clean result
    let typed = result.Typed.Value
    let declared =
        typed.References
        |> Map.values
        |> Seq.choose (fun id -> typed.Symbols |> Map.tryFind id)
        |> Seq.find (fun symbol -> symbol.Name = "area")
    Assert.EndsWith("shapes.nyx", declared.Declaration.File)

[<Fact>]
let ``Whole-module re-exports in a cycle are an import cycle`` () =
    let modules = [ "a.nyx", "module a\n\nexport b\n\nexport def one = 1"; "b.nyx", "module b\n\nexport a\n\nexport def two = 2" ]
    let result = compileImportingAll modules "module main\nimport a\ndef value = one"
    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Code = DiagnosticCodes.ImportCycle.Code)

[<Fact>]
let ``A whole-module re-export of a name the module defines itself is a clash, with a fix that selects the rest`` () =
    let source = "module main\n\nexport shapes\n\ndef area: int = 1"
    let diagnostic = Assert.Single((compileImportingAll (List.take 1 wholeModules) source).Diagnostics)
    Assert.Equal(DiagnosticCodes.ReExportClash.Code, diagnostic.Code)
    Assert.Equal("'area' is re-exported from 'shapes', but this module defines 'area' too", diagnostic.Message)
    Assert.Equal("shapes", textAt source diagnostic.Span.Value)
    let fix = Assert.Single diagnostic.Fixes
    Assert.Equal("module main\n\nexport shapes (Shape, perimeter)\n\ndef area: int = 1", applyEdits source fix.Edits)

[<Fact>]
let ``Two whole-module re-exports that give the same name from different modules clash at the second`` () =
    let modules =
        List.take 1 wholeModules
        @ [ "circles.nyx", "module circles\n\nexport def area: int -> int = { r -> 3 * r * r }\nexport def radius = 1" ]
    let source = "module main\n\nexport shapes\nexport circles"
    let diagnostic = Assert.Single((compileImportingAll modules source).Diagnostics)
    Assert.Equal(DiagnosticCodes.ReExportClash.Code, diagnostic.Code)
    Assert.Equal("'area' is re-exported from 'circles', but also from 'shapes', where it is another definition", diagnostic.Message)
    Assert.Equal("circles", textAt source diagnostic.Span.Value)
    let fix = Assert.Single diagnostic.Fixes
    Assert.Equal("module main\n\nexport shapes\nexport circles (radius)", applyEdits source fix.Edits)

[<Fact>]
let ``Two whole-module re-exports that give the same definition do not clash`` () =
    clean (compileImportingAll wholeModules "module main\n\nexport shapes\nexport geometry\n\nexport def value = area(square(1))")

[<Fact>]
let ``A package's module re-exports another of its modules whole`` () =
    let main = """
module main

import geometry

@entry
def main = {
  println("{area(square(3))}")
}
"""
    let manifest = $"""(package = (name = "reexport", version = "0.1.0", sources = ["src/**/*.nyx"]), {consoleHostEntry})"""
    use package =
        new TempPackage(
            ("reexport.nyxp", manifest)
            :: ("src/main.nyx", main.ReplaceLineEndings "\n")
            :: (wholeModules |> List.take 2 |> List.map (fun (file, text) -> "src/" + file, text))
        )
    match Package.findIn (SeededPackages.user ()) package.Root with
    | Result.Ok found -> Assert.Empty((Package.build found).Diagnostics)
    | Result.Error errors -> failwith (String.concat "\n" errors)

[<Fact>]
let ``Re-exporting a name the module does not export is an error at the name`` () =
    let source = "module main\nexport util (quadruple)"
    let diagnostic = Assert.Single((compileImportingAll modules source).Diagnostics)
    Assert.Equal(DiagnosticCodes.NotExported.Code, diagnostic.Code)
    Assert.Equal("'util' does not export 'quadruple'", diagnostic.Message)
    Assert.Equal("quadruple", textAt source diagnostic.Span.Value)
/// `geo`, which re-exports its own `shapes` block whole, and `sel`, which re-exports one name of its `shapes`.
let private blockModules =
    [ "geo.nyx",
      """
module geo

export shapes

module shapes =
  export type Shape = (width: int, height: int)
  export def area: Shape -> int = { s -> s.width * s.height }
  export def square: int -> Shape = { n -> Shape(width = n, height = n) }
  def unit: int = 1

export def twice: int -> int = { n -> 2 * area(square(n)) }
"""
      "sel.nyx",
      """
module sel

export shapes (area)

module shapes =
  export def area: int -> int = { n -> n * n }
  export def side: int -> int = { n -> n }
""" ]
    |> List.map (fun (file, text) -> file, text.ReplaceLineEndings "\n")

[<Fact>]
let ``An importer of a module gets what it re-exports of its own block, unqualified`` () =
    clean (compileImportingAll blockModules "module main\nimport geo\ndef s: Shape = Shape(width = 1, height = 2)\ndef value = area(s) + area(square(2)) + twice(1)")

[<Fact>]
let ``What a module re-exports of its own block is under the alias it is imported with, and can be selected`` () =
    clean (compileImportingAll blockModules "module main\nimport geo as g\ndef value = g.area(g.square(2))")
    clean (compileImportingAll blockModules "module main\nimport geo as (area, square)\ndef value = area(square(2))")

[<Fact>]
let ``A re-export of a block that selects names gives only those`` () =
    clean (compileImportingAll blockModules "module main\nimport sel\ndef value = area(2)")
    let diagnostic = Assert.Single((compileImportingAll blockModules "module main\nimport sel\ndef value = side(2)").Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)

[<Fact>]
let ``A name a module re-exports of its own block refers to the definition in the block`` () =
    let source = "module main\nimport geo\ndef value = area(square(2))"
    let result = compileImportingAll blockModules source
    clean result
    let typed = result.Typed.Value
    let declared =
        typed.References
        |> Map.values
        |> Seq.choose (fun id -> typed.Symbols |> Map.tryFind id)
        |> Seq.find (fun symbol -> symbol.Name = "geo.shapes.area")
    Assert.EndsWith("geo.nyx", declared.Declaration.File)
    let geo = snd blockModules.[0]
    Assert.Equal("area", geo.Substring(declared.Declaration.Start, declared.Declaration.End - declared.Declaration.Start))
    Assert.True(declared.Declaration.Start > geo.IndexOf "module shapes =")

[<Fact>]
let ``A re-export of a block that does not export a name it selects is an error at the name`` () =
    let source = "module main\n\nexport shapes (volume)\n\nmodule shapes =\n  export def area: int = 1\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.NotExported.Code, diagnostic.Code)
    Assert.Equal("'shapes' does not export 'volume'", diagnostic.Message)
    Assert.Equal("volume", textAt source diagnostic.Span.Value)

[<Fact>]
let ``A whole re-export of a block that gives a name the module defines itself is a clash`` () =
    let source = "module main\n\nexport shapes\n\nmodule shapes =\n  export def area: int = 1\n  export def side: int = 2\n\nexport def area: int = 3\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.ReExportClash.Code, diagnostic.Code)
    Assert.Equal("'area' is re-exported from 'shapes', but this module defines 'area' too", diagnostic.Message)
    Assert.Equal("shapes", textAt source diagnostic.Span.Value)
    let fix = Assert.Single diagnostic.Fixes
    Assert.StartsWith("module main\n\nexport shapes (side)\n", applyEdits source fix.Edits)
