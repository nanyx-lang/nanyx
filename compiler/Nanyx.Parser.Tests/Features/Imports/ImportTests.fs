module ParserTests.Features.Imports

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse import feature files`` () =
    parseFeatureNyxFiles "Imports"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Error err -> failwith $"Parse failed for {filePath}: {err}"
        | Ok r -> printfn "%A" r
    )

let private importsOf (source: string) =
    match parseModule (source.ReplaceLineEndings "\n") with
    | Ok [ Kind (Import items) ] -> items |> List.map (fun item -> item.Alias, item.Selection |> Option.map (List.map fst))
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``An import selects names in brackets after as`` () =
    Assert.Equal<(string option * string list option) list>([ None, Some [ "double"; "triple" ] ], importsOf "import util as (double, triple)")

[<Fact>]
let ``An import selects names and takes an alias with &`` () =
    Assert.Equal<(string option * string list option) list>([ Some "u", Some [ "double" ] ], importsOf "import util as u & (double)")

[<Fact>]
let ``A selection's names may be types and attached definitions, one per line`` () =
    let source = """
import shapes as (
  Shape
  Email
  string.shout
)"""
    Assert.Equal<(string option * string list option) list>([ None, Some [ "Shape"; "Email"; "string.shout" ] ], importsOf (source.Trim()))

[<Fact>]
let ``Selections parse among bracketed imports`` () =
    let source = """
import (
  nanyx/math as (sqrt, pi)
  util as u
)"""
    Assert.Equal<(string option * string list option) list>([ None, Some [ "sqrt"; "pi" ]; Some "u", None ], importsOf (source.Trim()))

[<Fact>]
let ``A name that starts with as after an import is not an alias`` () =
    match parseModule "import util\nassert(true)" with
    | Ok [ Kind (Import [ item ]); _ ] -> Assert.Equal(None, item.Alias)
    | other -> failwith $"Unexpected parse: %A{other}"
[<Fact>]
let ``A re-export names a module and what it exports again`` () =
    match parseModule "export util (double, triple)" with
    | Ok [ Kind (ReExport item) ] ->
        Assert.Equal(ModuleImport "util", item.Source)
        Assert.Equal<string list option>(Some [ "double"; "triple" ], item.Selection |> Option.map (List.map fst))
    | other -> failwith $"Unexpected parse: %A{other}"

[<Theory>]
[<InlineData("export shapes")>]
[<InlineData("export shapes  -- all of it\n")>]
[<InlineData("export shapes\ndef one = 1")>]
let ``A module alone on its line is re-exported whole`` (source: string) =
    match parseModule source with
    | Ok (Kind (ReExport item) :: _) ->
        Assert.Equal(ModuleImport "shapes", item.Source)
        Assert.Equal(None, item.Selection)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``An exported definition is not a re-export`` () =
    match parseModule "export def double = { x: int -> x * 2 }\nexport type Point = (x: int, y: int)" with
    | Ok [ Kind (Def (ValueDef (true, "double", _, _))); Kind (Def (TypeDef ("Point", _, _, _))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``export module declares a module another package can import`` () =
    match parseModule "export module acme.web.http\n\nexport def get = 1" with
    | Result.Ok [ Kind (ModuleDecl(true, "acme.web.http")); Kind (Def _) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A module declaration without export is internal to its package`` () =
    match parseModule "module strings\n\n@partial export module more" with
    | Result.Ok [ Kind (ModuleDecl(false, "strings")); Kind (Attribute("partial", None)); Kind (ModuleDecl(true, "more")) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``export before a def is still a def, and before a module's name in brackets a re-export`` () =
    match parseModule "export def x = 1\nexport nanyx/math (abs)" with
    | Result.Ok [ Kind (Def (ValueDef(true, "x", None, _))); Kind (ReExport _) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"
