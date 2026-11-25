module TranspilerWasmTests.Features.Chars.Run

open Xunit
open TranspilerWasmTestHelpers

// A `char` is its code point at run time, so its literals match and compare by it (#2596).

[<Fact>]
let ``Run - char literals match and compare by code point`` () =
    """def isVowel: char -> bool = { c ->
  match c
    | "a" | "e" | "i" | "o" | "u" -> true
    | _ -> false
}

def smile: char = "\u{1F600}"

@entry
def main = {
  println("{isVowel("e")} {isVowel("z")}")
  def initial: char = "A"
  println("{initial == "A"} {"B" == initial} {initial < "B"} {smile > "z"}")
}"""
    |> runSource
    |> assertRan "true false\ntrue false true true\n"
