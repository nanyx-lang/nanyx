module TranspilerWasmTests.Features.Packages.PartialModules

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a package whose module is split across two files runs`` () =
    let validation =
        """
@partial module utils

-- `isNonEmpty` is declared in the other fragment
export def validate: string -> bool = { s -> s \isNonEmpty }
"""
    let format =
        """
@partial module utils

import nanyx/text

def isNonEmpty: string -> bool = { s -> s.length > 0 }

export def format: string -> string = { s -> s \trim }
"""
    let main =
        """
module main

import utils

@entry
def main = {
  println(format("  tidy  "))
  println(validate(""))
}
"""
    let files = [ "validation.nyx", validation; "format.nyx", format; "main.nyx", main ] |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n")
    let outcome, _ = nanyxOnPackage files [ "run"; "{package}" ]
    assertRan "tidy\nfalse\n" outcome
