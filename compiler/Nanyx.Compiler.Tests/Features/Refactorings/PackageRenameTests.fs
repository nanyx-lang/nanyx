module Nanyx.Compiler.Tests.Features.Refactorings.PackageRename

open System.IO
open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Compiler.Tests.TestSupport

// Rename across the files of a package, each compiled as `nanyx build` checks it.

let private manifest = """(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))"""

let private shapes =
    withFinalNewline """module shapes

export type Point = (x: int, y: int)

export def Point.show: Point -> string = { p -> "point" }

export def origin: Point = (x = 0, y = 0)"""

let private main =
    withFinalNewline """module main

import app/shapes

def here: Point = origin
def text = here \show
def shown = Point.show(origin)
def nudge = { (zero: Point) -> origin.x + zero.x }"""

let private report =
    withFinalNewline """module report

import app/shapes as s

def far: s.Point = s.origin
def near: s.Point = (x = s.origin.x + 1, y = 0)"""


/// Renaming the name at the `occurrence`th `text` (plus `within`) in the file `file` of a package of `shapes`, `main`
/// and `report` to `newName`: each file's text after it, by name, or why it is refused.
let private renamed (file: string) (text: string) (occurrence: int) (within: int) (newName: string) =
    use package = new TempPackage([ "app.nyxp", manifest; "src/shapes.nyx", shapes; "src/main.nyx", main; "src/report.nyx", report ])
    let path = Path.GetFullPath(package.Path $"src/{file}")
    let compile (readFile: string -> string) =
        (Package.compilePackageReading readFile path).Value
        |> List.map (fun (file, result) -> { Rename.Path = Path.GetFullPath file; Rename.Source = readFile file; Rename.Result = result })
    let files = compile File.ReadAllText
    for file in files do
        Assert.True(file.Result.Diagnostics.IsEmpty, sprintf "%A" file.Result.Diagnostics)
    let recompile (texts: (string * string) list) =
        let renamed = Map.ofList texts
        let compiled = compile (fun file -> renamed |> Map.tryFind (Path.GetFullPath file) |> Option.defaultWith (fun () -> File.ReadAllText file))
        texts |> List.map (fun (path, _) -> (compiled |> List.find (fun file -> file.Path = path)).Result)
    let scope = Rename.Package(files |> List.map (fun file -> file.Path))
    let source = File.ReadAllText path
    Rename.editsIn scope files path (offsetOf source text occurrence within) newName recompile
    |> Result.map (fun edits ->
        files
        |> List.map (fun file ->
            Path.GetFileName file.Path,
            edits |> List.tryFind (fst >> (=) file.Path) |> Option.map snd |> Option.defaultValue [] |> applyEdits file.Source)
        |> Map.ofList)

let private renamedTo file text occurrence within newName =
    match renamed file text occurrence within newName with
    | Ok after -> after
    | Error reason -> failwithf "expected the rename, got: %s" reason

let private refusedBecause file text occurrence within newName =
    match renamed file text occurrence within newName with
    | Error reason -> reason
    | Ok after -> failwithf "expected a refusal, got\n%A" after

[<Fact>]
let ``An exported def is renamed in every file of the package that uses it`` () =
    let after = renamedTo "main.nyx" "origin" 0 0 "start"
    Assert.Equal(shapes.Replace("origin", "start"), after.["shapes.nyx"])
    Assert.Equal(main.Replace("origin", "start"), after.["main.nyx"])
    Assert.Equal(report.Replace("s.origin", "s.start"), after.["report.nyx"])

[<Fact>]
let ``Renaming a type renames the type's part of its attached definitions' names`` () =
    let after = renamedTo "shapes.nyx" "Point" 0 0 "Spot"
    Assert.Equal(shapes.Replace("Point", "Spot"), after.["shapes.nyx"])
    Assert.Equal(main.Replace("Point", "Spot"), after.["main.nyx"])
    Assert.Equal(report.Replace("Point", "Spot"), after.["report.nyx"])

[<Fact>]
let ``An attached definition is renamed across files and keeps its type's part`` () =
    let after = renamedTo "main.nyx" "show" 0 0 "describe"
    Assert.Equal(shapes.Replace(".show", ".describe"), after.["shapes.nyx"])
    Assert.Equal(main.Replace("\\show", "\\describe").Replace(".show", ".describe"), after.["main.nyx"])

[<Fact>]
let ``An import's alias is renamed where the names it brings in are written`` () =
    let after = renamedTo "report.nyx" "as s" 0 3 "geo"
    Assert.Equal(report.Replace("as s", "as geo").Replace("s.", "geo."), after.["report.nyx"])
    Assert.Equal(main, after.["main.nyx"])

[<Fact>]
let ``An import with no alias is refused`` () =
    Assert.StartsWith("It is imported without an alias", refusedBecause "main.nyx" "app/shapes" 0 4 "geo")

[<Fact>]
let ``A rename that would change what a name means in another file is refused, naming the file`` () =
    Assert.Equal(
        "`zero.x` on line 8 of main.nyx would refer to something else",
        refusedBecause "shapes.nyx" "def origin" 0 4 "zero")
