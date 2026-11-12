module Nanyx.Compiler.Tests.Features.PackageBuilds

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private manifest =
    """
[package]
name = "app"
version = "0.1.0"
sources = ["src/**/*.nyx"]
"""

let private shared =
    """
module shared

export def twice: int -> int = { n -> n * 2 }
"""

let private user (name: string) =
    $"""
module {name}

import shared

export def run: int -> int = {{ n -> twice(n) }}
"""

[<Fact>]
let ``A package build checks each module once, however many of its files import it`` () =
    // #698: each file's compile checked again every module it imports. A module checked once gives each importer
    // the same symbols, which a second check would number afresh.
    let files =
        [ "app.nyxp", manifest; "src/shared.nyx", shared; "src/a.nyx", user "a"; "src/b.nyx", user "b"; "src/c.nyx", user "c" ]
    use package = new TempPackage(files |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n"))
    let build =
        match Package.find package.Root with
        | Result.Ok found -> Package.build found
        | Result.Error errors -> failwith (String.concat "\n" errors)
    Assert.Empty build.Diagnostics
    let twiceSymbols (result: CompileResult) =
        result.Typed.Value.Symbols
        |> Map.values
        |> Seq.filter (fun symbol -> symbol.Name = "twice" && symbol.Declaration.File.EndsWith "shared.nyx")
        |> Seq.map _.Id
        |> Set.ofSeq
    let seen = build.Results |> List.map (snd >> twiceSymbols)
    Assert.Equal(4, seen.Length)
    Assert.All(seen, fun ids -> Assert.Equal(1, ids.Count))
    Assert.Equal(1, (Set.unionMany seen).Count)
