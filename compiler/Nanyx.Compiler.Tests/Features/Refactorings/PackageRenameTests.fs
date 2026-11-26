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

let private logging =
    withFinalNewline """module logging

export context Log = (log: string -> ())

export def hello: [Log] () -> () = { () -> log("hi") }"""

let private quiet =
    withFinalNewline """module quiet

import logging

def run = use Log(log = { m -> () }) in hello()"""

/// Renaming in a package of `files`: the full path of each file, and the text of each after the edits, by name, with
/// the files renamed, by name; or why it is refused.
let private renamedIn (files: (string * string) list) (file: string) (text: string) (occurrence: int) (within: int) (newName: string) =
    use package = new TempPackage(("app.nyxp", manifest) :: (files |> List.map (fun (name, text) -> $"src/{name}", text)))
    let path = Path.GetFullPath(package.Path $"src/{file}")
    let compile (readFile: string -> string) =
        (Package.compilePackageReading (SeededPackages.user ()) readFile path).Value
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
    |> Result.map (fun renaming ->
        let texts =
            files
            |> List.map (fun file ->
                Path.GetFileName file.Path,
                renaming.Edits |> List.tryFind (fst >> (=) file.Path) |> Option.map snd |> Option.defaultValue [] |> applyEdits file.Source)
            |> Map.ofList
        texts, renaming.Files |> List.map (fun (old, renamed) -> Path.GetFileName old, Path.GetFileName renamed))

/// Renaming the name at the `occurrence`th `text` (plus `within`) in the file `file` of a package of `shapes`, `main`
/// and `report` to `newName`: each file's text after it, by name, or why it is refused.
let private renamed (file: string) (text: string) (occurrence: int) (within: int) (newName: string) =
    renamedIn [ "shapes.nyx", shapes; "main.nyx", main; "report.nyx", report ] file text occurrence within newName |> Result.map fst

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
let ``An import with no alias is refused at its package's name`` () =
    Assert.StartsWith("It is imported without an alias", refusedBecause "main.nyx" "app/shapes" 0 1 "geo")

[<Fact>]
let ``A rename that would change what a name means in another file is refused, naming the file`` () =
    Assert.Equal(
        "`zero.x` on line 8 of main.nyx would refer to something else",
        refusedBecause "shapes.nyx" "def origin" 0 4 "zero")

[<Fact>]
let ``A context's member is renamed in the files that write an instance of its context`` () =
    match renamedIn [ "logging.nyx", logging; "quiet.nyx", quiet ] "quiet.nyx" "log =" 0 1 "write" with
    | Ok (after, _) ->
        Assert.Equal(logging.Replace("(log:", "(write:").Replace("log(", "write("), after.["logging.nyx"])
        Assert.Equal(quiet.Replace("log =", "write ="), after.["quiet.nyx"])
    | Error reason -> failwithf "expected the rename, got: %s" reason

[<Fact>]
let ``A record's field is renamed in every file of the package that writes it`` () =
    let after = renamedTo "shapes.nyx" "x: int" 0 0 "across"
    Assert.Equal(shapes.Replace("x: int", "across: int").Replace("x = 0", "across = 0"), after.["shapes.nyx"])
    Assert.Equal(main.Replace(".x", ".across"), after.["main.nyx"])
    Assert.Equal(report.Replace("(x = s.origin.x", "(across = s.origin.across"), after.["report.nyx"])

[<Fact>]
let ``A module is renamed in its module line and every import of it, and its file with it`` () =
    match renamedIn [ "shapes.nyx", shapes; "main.nyx", main; "report.nyx", report ] "shapes.nyx" "shapes" 0 2 "geometry" with
    | Ok (after, files) ->
        Assert.Equal(shapes.Replace("module shapes", "module geometry"), after.["shapes.nyx"])
        Assert.Equal(main.Replace("app/shapes", "app/geometry"), after.["main.nyx"])
        Assert.Equal(report.Replace("app/shapes", "app/geometry"), after.["report.nyx"])
        Assert.Equal<(string * string) list>([ "shapes.nyx", "geometry.nyx" ], files)
    | Error reason -> failwithf "expected the rename, got: %s" reason

[<Fact>]
let ``A module is renamed from an import of it`` () =
    let after = renamedTo "report.nyx" "app/shapes" 0 6 "geometry"
    Assert.Equal(shapes.Replace("module shapes", "module geometry"), after.["shapes.nyx"])
    Assert.Equal(main.Replace("app/shapes", "app/geometry"), after.["main.nyx"])

[<Fact>]
let ``A module renamed to the name of another module of the package is refused`` () =
    Assert.StartsWith("The renamed package would not compile", refusedBecause "shapes.nyx" "shapes" 0 0 "main")

let private money =
    withFinalNewline """module money

module coins =
  export type Coin = protected (pence: int)

  export def Coin.of: int -> Coin = { pence -> Coin(pence = pence) }

  export def penny: Coin = Coin.of(1)

import coins

def one: Coin = Coin.of(1)
def two = money.coins.penny"""

let private till =
    withFinalNewline """module till

import app/money

def float = money.coins.Coin.of(500)"""

[<Fact>]
let ``A module block is renamed in its module line, each import of it and each name written with it`` () =
    match renamedIn [ "money.nyx", money; "till.nyx", till ] "money.nyx" "coins" 0 0 "cents" with
    | Ok (after, files) ->
        Assert.Equal(money.Replace("coins", "cents"), after.["money.nyx"])
        Assert.Equal(till.Replace("coins", "cents"), after.["till.nyx"])
        Assert.Empty(files)
    | Error reason -> failwithf "expected the rename, got: %s" reason

/// Renaming the block `coins` at the `occurrence`th `text` (plus `within`) in `money.nyx`, beside `till.nyx`, to
/// `newName`: each file's text after it, by name, or why it is refused.
let private blockRenamed (text: string) (occurrence: int) (within: int) (newName: string) (files: (string * string) list) =
    renamedIn files "money.nyx" text occurrence within newName |> Result.map fst

[<Fact>]
let ``A module block is renamed from an import of it`` () =
    match blockRenamed "import coins" 0 8 "cents" [ "money.nyx", money; "till.nyx", till ] with
    | Ok after -> Assert.Equal(money.Replace("coins", "cents"), after.["money.nyx"])
    | Error reason -> failwithf "expected the rename, got: %s" reason

[<Fact>]
let ``A module block renamed to the name of a block beside it is refused`` () =
    let withNotes = money.Replace("import coins", "module notes =\n  export def penny = 1\n\nimport coins")
    match blockRenamed "coins" 0 0 "notes" [ "money.nyx", withNotes; "till.nyx", till ] with
    | Error reason -> Assert.StartsWith("The renamed package would not compile", reason)
    | Ok after -> failwithf "expected a refusal, got\n%A" after

[<Fact>]
let ``A module block's new name must be a name`` () =
    match blockRenamed "coins" 0 0 "two.parts" [ "money.nyx", money; "till.nyx", till ] with
    | Error reason -> Assert.Equal("`two.parts` is not a name", reason)
    | Ok after -> failwithf "expected a refusal, got\n%A" after
