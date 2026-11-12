module Nanyx.Compiler.Tests.Features.NumberConversions

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The types of `names` in `source`, which has no diagnostics, as the compiler prints them.
let private typesOf (names: string list) (source: string) =
    let typed = compileClean (source.ReplaceLineEndings "\n")
    names |> List.map (fun name -> Unifier.tyToString typed.Types.[name])

[<Fact>]
let ``A conversion that keeps every value gives the value, and any other an option of it`` () =
    let source =
        """
def widened = I32.toInt
def unsigned = U8.toI16
def narrowed = int.toU8
def signChanged = I8.toU64
def float = U64.toFloat
def single = int.toF32
"""
    Assert.Equal<string list>(
        [ "I32 -> int"
          "U8 -> I16"
          "int -> #some(U8) | #outOfRange"
          "I8 -> #some(U64) | #outOfRange"
          "U64 -> float"
          "int -> F32" ],
        typesOf [ "widened"; "unsigned"; "narrowed"; "signChanged"; "float"; "single" ] source)

[<Fact>]
let ``A floating-point number converts to an int as a mode says, or to the other floating-point type`` () =
    let source =
        """
def whole = { (x: float) -> float.toInt(x, #round) }
def maybe = { (x: F32) -> F32.tryToInt(x, #floor) }
def single = float.toF32
def double = F32.toFloat
"""
    Assert.Equal<string list>(
        [ "float -> int"; "F32 -> #some(int) | #outOfRange"; "float -> F32"; "F32 -> float" ],
        typesOf [ "whole"; "maybe"; "single"; "double" ] source)
