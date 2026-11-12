module Nanyx.Compiler.Tests.Features.PackageImports

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private manifest =
    """
[package]
name = "acme.web"
version = "0.1.0"
sources = ["src/**/*.nyx"]
"""

let private greetings (name: string) =
    $"""
module {name}

export def greet: string -> string = {{ name -> "Hello, " ++ name }}
"""

let private main (import: string) =
    $"""
module main

import {import}

@entry
def main = {{
  println(greet("Ada"))
}}
"""

/// An `@entry` that imports nothing of the package's.
let private plainMain =
    """
module main

@entry
def main = {
  println("hi")
}
"""

/// The package's diagnostics, each as its code, the file it is in and its message.
let private buildOf (files: (string * string) list) =
    use package = new TempPackage(("acme.web.nyxp", manifest) :: files |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n"))
    match Package.find package.Root with
    | Result.Ok found ->
        (Package.build found).Diagnostics
        |> List.map (fun diagnostic ->
            let file = diagnostic.Span |> Option.map (fun span -> Path.GetRelativePath(package.Root, span.File).Replace('\\', '/'))
            diagnostic.Code, file, diagnostic.Message)
    | Result.Error errors -> failwith (String.concat "\n" errors)

[<Theory>]
[<InlineData("greetings")>]
[<InlineData("acme.web/greetings")>]
let ``A package imports its own module by its name, wherever its file is`` (import: string) =
    Assert.Empty(buildOf [ "src/deep/er/greetings.nyx", greetings "greetings"; "src/main.nyx", main import ])

[<Fact>]
let ``A module is found by the name it declares, not its file's name`` () =
    Assert.Empty(buildOf [ "src/hello.nyx", greetings "greetings"; "src/main.nyx", main "greetings" ])

[<Fact>]
let ``Importing a module by a name its file does not declare names both names`` () =
    let code, file, message = Assert.Single(buildOf [ "src/greetings.nyx", greetings "salutations"; "src/main.nyx", main "greetings" ])
    Assert.Equal(DiagnosticCodes.ImportNotFound.Code, code)
    Assert.Equal(Some "src/main.nyx", file)
    Assert.Equal(
        "Import not found: greetings: the package 'acme.web' has no module 'greetings'; 'src/greetings.nyx' declares the module name 'salutations', not 'greetings'",
        message)

[<Fact>]
let ``An unknown module of the package suggests close names`` () =
    let code, _, message = Assert.Single(buildOf [ "src/lib.nyx", greetings "greetings"; "src/main.nyx", main "greeting" ])
    Assert.Equal(DiagnosticCodes.ImportNotFound.Code, code)
    Assert.Equal("Import not found: greeting: the package 'acme.web' has no module 'greeting': did you mean 'greetings'?", message)

[<Fact>]
let ``A module of another package cannot be imported`` () =
    let code, _, message = Assert.Single(buildOf [ "src/greetings.nyx", greetings "greetings"; "src/main.nyx", main "acme.core/greetings" ])
    Assert.Equal(DiagnosticCodes.ImportNotFound.Code, code)
    Assert.StartsWith("Import not found: acme.core/greetings: there is no package 'acme.core'", message)

[<Fact>]
let ``A source file of a package without a module name is an error naming the file`` () =
    let code, file, message = Assert.Single(buildOf [ "src/main.nyx", plainMain; "src/util.nyx", """export def one = 1""" ])
    Assert.Equal(DiagnosticCodes.ScriptInPackage.Code, code)
    Assert.Equal(Some "src/util.nyx", file)
    Assert.Equal("'src/util.nyx' is in the package 'acme.web', so it must declare a module name: start it with module util", message)

[<Fact>]
let ``A source file of a package with top-level statements is an error naming the file`` () =
    let diagnostics = buildOf [ "src/main.nyx", plainMain; "src/extra.nyx", """
module extra

println("x")
""" ]
    let code, file, message = Assert.Single(diagnostics |> List.filter (fun (code, _, _) -> code = DiagnosticCodes.ScriptInPackage.Code))
    Assert.Equal(Some "src/extra.nyx", file)
    Assert.Equal("'src/extra.nyx' is in the package 'acme.web', so it cannot have top-level statements: put them in the package's @entry def", message)

[<Fact>]
let ``Two files that declare the same module are an error in each`` () =
    let diagnostics = buildOf [ "src/a.nyx", greetings "greetings"; "src/b.nyx", greetings "greetings"; "src/main.nyx", main "greetings" ]
    let duplicates = diagnostics |> List.filter (fun (code, _, _) -> code = DiagnosticCodes.DuplicateModule.Code)
    Assert.Equal<string option list>([ Some "src/a.nyx"; Some "src/b.nyx" ], duplicates |> List.map (fun (_, file, _) -> file) |> List.sort)
    let _, _, inA = duplicates |> List.find (fun (_, file, _) -> file = Some "src/a.nyx")
    Assert.Equal(
        "'src/a.nyx' declares the module 'greetings', which 'src/b.nyx' declares too: to split a module across files, start each with @partial module greetings, which 'src/a.nyx', 'src/b.nyx' do not",
        inA)

[<Fact>]
let ``A source file compiled on its own finds the package's modules`` () =
    use package =
        new TempPackage(
            [ "acme.web.nyxp", manifest
              "src/lib/greetings.nyx", (greetings "greetings").ReplaceLineEndings "\n"
              "src/main.nyx", (main "acme.web/greetings").ReplaceLineEndings "\n" ])
    let mainPath = package.Path "src/main.nyx"
    Assert.Empty((Script.compileSource mainPath (File.ReadAllText mainPath)).Diagnostics)

[<Fact>]
let ``A file under a manifest but not among its sources is compiled as a script`` () =
    use package =
        new TempPackage(
            [ "acme.web.nyxp", manifest
              "src/main.nyx", plainMain.ReplaceLineEndings "\n"
              "scripts/util.nyx", """export def one = 1"""
              "scripts/run.nyx",
              """
import "./util" as util

def two = util.one + 1
""".ReplaceLineEndings "\n" ])
    let runPath = package.Path "scripts/run.nyx"
    Assert.Empty((Script.compileSource runPath (File.ReadAllText runPath)).Diagnostics)
