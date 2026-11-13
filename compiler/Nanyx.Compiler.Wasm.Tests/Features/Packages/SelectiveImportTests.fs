module TranspilerWasmTests.Features.Packages.SelectiveImports

open Xunit
open TranspilerWasmTestHelpers

let private util =
    """
module util

export def double: int -> int = { x -> x * 2 }
export def triple: int -> int = { x -> x * 3 }
export def sixfold: int -> int = { x -> double(triple(x)) }

export type Shape = #circle(int) | #square(int)
export def Shape.area: Shape -> int = { shape ->
  match shape
    | #circle(r) -> 3 * r * r
    | #square(w) -> w * w
}
"""

let private run (main: string) =
    let files = [ "util.nyx", util; "main.nyx", main ] |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n")
    fst (nanyxOnPackage files [ "run"; "{package}" ])

[<Fact>]
let ``Run - an import that selects names calls them`` () =
    run """
module main

import util as (double, triple)

@entry
def main = { println("{double(2)} {triple(2)}") }
"""
    |> assertRan "4 6\n"

[<Fact>]
let ``Run - an import that selects names and takes an alias calls both`` () =
    run """
module main

import util as u & (double)

@entry
def main = { println("{double(2)} {u.triple(2)}") }
"""
    |> assertRan "4 6\n"

[<Fact>]
let ``Run - an export the selection leaves out keeps its meaning beside the importer's own of that name`` () =
    run """
module main

import util as (sixfold)

def triple = { x: int -> x }

@entry
def main = { println("{sixfold(1)} {triple(5)}") }
"""
    |> assertRan "6 5\n"

[<Fact>]
let ``Run - a selected type brings the definitions attached to it`` () =
    run """
module main

import util as (Shape)

def shape: Shape = #square(3)

@entry
def main = { println("{shape \area}") }
"""
    |> assertRan "9\n"