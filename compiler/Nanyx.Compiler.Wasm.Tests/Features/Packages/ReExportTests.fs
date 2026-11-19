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
[<Fact>]
let ``Run - a module's importer calls what it re-exports of its own blocks`` () =
    let util =
        """
module util

export def double: int -> int = { x -> x * 2 }
"""
    let geo =
        """
module geo

export shapes
export solids (volume)

module shapes =
  export type Shape = (width: int, height: int)
  export def area: Shape -> int = { s -> s.width * s.height }
  export def square: int -> Shape = { n -> Shape(width = n, height = n) }

module solids =
  export util (double)
  export def volume: int -> int = { n -> n * n * n }
  export def unused: int = 0
"""
    let main =
        """
module main

import geo
import geo as g

@entry
def main = {
  let s: Shape = Shape(width = 2, height = 5)
  println("{area(s)} {g.area(square(3))} {volume(2)}")
}
"""
    let files = [ "util.nyx", util; "geo.nyx", geo; "main.nyx", main ] |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n")
    let outcome, _ = nanyxOnPackage files [ "run"; "{package}" ]
    assertRan "10 9 8\n" outcome

[<Fact>]
let ``Run - a module block re-exports a block nested in it and another file's module`` () =
    let util =
        """
module util

export def double: int -> int = { x -> x * 2 }
"""
    let lib =
        """
module lib

export outer

module outer =
  export inner
  export util (double)

  module inner =
    export def inc: int -> int = { x -> x + 1 }

  export def twice: int -> int = { x -> double(inc(x)) }
"""
    let main =
        """
module main

import lib

@entry
def main = { println("{inc(1)} {double(3)} {twice(4)}") }
"""
    let files = [ "util.nyx", util; "lib.nyx", lib; "main.nyx", main ] |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n")
    let outcome, _ = nanyxOnPackage files [ "run"; "{package}" ]
    assertRan "2 6 10\n" outcome
