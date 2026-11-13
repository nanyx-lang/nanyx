module TranspilerWasmTests.Features.Packages.ReExports

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a module's importer calls what it re-exports`` () =
    let util =
        """
module util

export def double: int -> int = { x -> x * 2 }
export def triple: int -> int = { x -> x * 3 }
"""
    let c =
        """
module c

export util (double)

export def quadruple: int -> int = { x -> double(double(x)) }
"""
    let main =
        """
module main

import c

@entry
def main = { println("{double(2)} {quadruple(1)}") }
"""
    let files = [ "util.nyx", util; "c.nyx", c; "main.nyx", main ] |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n")
    let outcome, _ = nanyxOnPackage files [ "run"; "{package}" ]
    assertRan "4 4\n" outcome