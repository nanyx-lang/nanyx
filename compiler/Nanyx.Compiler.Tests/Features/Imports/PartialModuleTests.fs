module Nanyx.Compiler.Tests.Features.PartialModules

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The modules tour page's two fragments of `utils`.
let private validation =
    """
@partial module utils

-- `isNonEmpty` is declared in the other fragment
export def validate: string -> bool = { s -> s \isNonEmpty }
"""

let private format =
    """
@partial module utils

import nanyx/text

def isNonEmpty: string -> bool = { s -> s.length > 0 }

export def format: string -> string = { s -> s \trim }
"""

let private main =
    """
module main

import utils

@entry
def main = {
  println(format(" hi "))
  println(validate("x"))
}
"""

/// The diagnostics of the package of `files` (paths under `src`, with their text), each with the file it is in.
let private buildOf (files: (string * string) list) =
    let manifest = "[package]\nname = \"demo\"\nversion = \"0.1.0\"\nsources = [\"src/**/*.nyx\"]\n"
    use package = new TempPackage(("demo.nyxp", manifest) :: (files |> List.map (fun (path, text) -> "src/" + path, text.ReplaceLineEndings "\n")))
    let shown (span: Parser.Program.SourceSpan) = Path.GetRelativePath(package.Root, span.File).Replace('\\', '/')
    match Package.find package.Root with
    | Result.Ok found ->
        (Package.build found).Diagnostics
        |> List.map (fun diagnostic ->
            diagnostic.Code, diagnostic.Span |> Option.map shown, diagnostic.Message, diagnostic.Related |> List.map (fst >> shown))
    | Result.Error errors -> failwith (String.concat "\n" errors)

[<Fact>]
let ``The tour's two fragments of utils make one module`` () =
    Assert.Empty(buildOf [ "validation.nyx", validation; "format.nyx", format; "main.nyx", main ])

[<Fact>]
let ``Each fragment can refer to the other's declarations`` () =
    let first =
        """
@partial module numbers

export def double: int -> int = { n -> n * two }
def one = 1
"""
    let second =
        """
@partial module numbers

def two = one + one
export def quadruple: int -> int = { n -> double(double(n)) }
"""
    let user =
        """
module main

import numbers

@entry
def main = {
  println(quadruple(3))
}
"""
    Assert.Empty(buildOf [ "a.nyx", first; "b.nyx", second; "main.nyx", user ])

[<Fact>]
let ``The imports of one fragment apply to the whole module`` () =
    let other =
        """
@partial module utils

export def tidy: string -> string = { s -> s \trim }
"""
    Assert.Empty(buildOf [ "validation.nyx", validation; "format.nyx", format; "shout.nyx", other; "main.nyx", main ])

[<Fact>]
let ``A module in two files without @partial on each is an error in each, naming both`` () =
    let unmarked = format.Replace("@partial module", "module")
    let diagnostics = buildOf [ "validation.nyx", validation; "format.nyx", unmarked; "main.nyx", main ]
    Assert.All(diagnostics, fun (code, _, _, _) -> Assert.Equal(DiagnosticCodes.DuplicateModule.Code, code))
    let inFormat = diagnostics |> List.find (fun (_, file, _, _) -> file = Some "src/format.nyx")
    let _, _, message, _ = inFormat
    Assert.Equal(
        "'src/format.nyx' declares the module 'utils', which 'src/validation.nyx' declares too: to split a module across files, start each with @partial module utils, which 'src/format.nyx' does not",
        message)
    Assert.Contains(diagnostics, fun (_, file, _, _) -> file = Some "src/validation.nyx")

[<Fact>]
let ``A declaration in two fragments is a duplicate, related to the other file`` () =
    let again =
        """
@partial module utils

def isNonEmpty: string -> bool = { s -> true }
"""
    let duplicates =
        buildOf [ "validation.nyx", validation; "format.nyx", format; "again.nyx", again; "main.nyx", main ]
        |> List.filter (fun (code, _, _, _) -> code = DiagnosticCodes.DuplicateDefinition.Code)
    let _, file, message, related = Assert.Single(duplicates)
    Assert.Contains("'isNonEmpty' is already defined in this module", message)
    Assert.NotEqual(file, related |> List.tryHead)
    Assert.Equal<string list>([ "src/again.nyx"; "src/format.nyx" ], (Option.toList file @ related) |> List.sort)
