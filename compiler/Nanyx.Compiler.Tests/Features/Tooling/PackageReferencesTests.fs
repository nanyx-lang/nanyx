module Nanyx.Compiler.Tests.Features.Tooling.PackageReferences

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Find references across the files of a package, each compiled as `nanyx build` checks it.

let private manifest = """(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))"""

let private shapes =
    withFinalNewline """module shapes

export type Point = (x: int, y: int)

export context Logger = (log: string -> ())

export def Point.show: Point -> string = { p -> "point" }

export def origin: Point = (x = 0, y = 0)"""

let private main =
    withFinalNewline """module main

import app/shapes

def here: Point = origin
def text = here \show
def say: [Logger] string -> () = { s -> log(s) }"""

let private report =
    withFinalNewline """module report

import app/shapes

def far: Point = origin
def told: [Logger] () -> () = { log(far \show) }"""

/// Each name in the package's files that refers to what the name at the `occurrence`th `text` (plus `within`) in the
/// file `file` declares or refers to: the file's name, the name's text and where it starts, in that order.
let private referencesFrom (file: string) (text: string) (occurrence: int) (within: int) =
    use package = new TempPackage([ "app.nyxp", manifest; "src/shapes.nyx", shapes; "src/main.nyx", main; "src/report.nyx", report ])
    let results = (Package.compilePackageReading File.ReadAllText (package.Path "src/main.nyx")).Value
    for _, result in results do
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let path = Path.GetFullPath(package.Path $"src/{file}")
    let source = results |> List.find (fst >> Path.GetFullPath >> (=) path) |> snd
    let sourceText = File.ReadAllText path
    let symbol = (Definition.symbolAt path source (offsetOf sourceText text occurrence within)).Value
    Definition.referencesAcross (List.map snd results) symbol
    |> List.map (fun span ->
        let text = File.ReadAllText span.File
        Path.GetFileName span.File, text.Substring(span.Start, span.End - span.Start), span.Start)
    |> List.sort

[<Fact>]
let ``References to an exported def are found in every file of the package that uses it`` () =
    let expected = [ "main.nyx", "origin", offsetOf main "origin" 0 0; "report.nyx", "origin", offsetOf report "origin" 0 0 ]
    Assert.Equal<(string * string * int) list>(expected, referencesFrom "shapes.nyx" "def origin" 0 4)
    Assert.Equal<(string * string * int) list>(expected, referencesFrom "main.nyx" "origin" 0 0)

[<Fact>]
let ``References to an exported type, context and attached definition are found across the package`` () =
    // The type's uses in its own module are among them, though not the type's part of an attached definition's name.
    Assert.Equal<(string * string * int) list>(
        [ "main.nyx", "Point", offsetOf main "Point" 0 0
          "report.nyx", "Point", offsetOf report "Point" 0 0
          "shapes.nyx", "Point", offsetOf shapes "Point" 2 0
          "shapes.nyx", "Point", offsetOf shapes "Point" 3 0 ],
        referencesFrom "report.nyx" "Point" 0 0)
    Assert.Equal<(string * string * int) list>(
        [ "main.nyx", "Logger", offsetOf main "Logger" 0 0; "report.nyx", "Logger", offsetOf report "Logger" 0 0 ],
        referencesFrom "shapes.nyx" "Logger" 0 0)
    Assert.Equal<(string * string * int) list>(
        [ "main.nyx", "show", offsetOf main "show" 0 0; "report.nyx", "show", offsetOf report "show" 0 0 ],
        referencesFrom "main.nyx" "show" 0 0)

[<Fact>]
let ``A local's references stay in its own file`` () =
    Assert.Equal<(string * string * int) list>(
        [ "main.nyx", "s", offsetOf main "log(s)" 0 4 ],
        referencesFrom "main.nyx" "{ s" 0 2)

[<Fact>]
let ``A file in no package has no package to search`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "loose.nyx")
        File.WriteAllText(path, "def x = 1\n")
        Assert.True((Package.compilePackageReading File.ReadAllText path).IsNone))
