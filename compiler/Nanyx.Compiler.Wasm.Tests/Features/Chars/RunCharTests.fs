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

[<Fact>]
let ``Run - a char converts to and from its code point and a string`` () =
    """@entry
def main = {
  def e: char = "é"
  println(char.toInt(e))
  println(char.fromInt(233))
  println(char.fromInt(55296))
  println(char.fromInt(1114112))
  println(e)
  println("{char.toString("A")}{char.toString("€")}{char.toString("\u{1F600}")}")
  println("héllo \u{1F600}" \string.chars)
  println(string.fromChars(["h", "é", "\u{1F600}"]))
  println(string.fromCodePoint(8364))
}"""
    |> runSource
    |> assertRan "233\n#some(é)\n#invalidCodePoint\n#invalidCodePoint\né\nA€😀\n[h, é, l, l, o,  , 😀]\nhé😀\n#some(€)\n"

[<Fact>]
let ``Run - a char's predicates read ASCII digits, letters and whitespace`` () =
    """@entry
def main = {
  println("{char.isDigit("7")} {char.isDigit("x")} {char.isAsciiLetter("q")} {char.isAsciiLetter("é")}")
  println("{char.isWhitespace("\t")} {char.isWhitespace("a")}")
}"""
    |> runSource
    |> assertRan "true false true false\ntrue false\n"

[<Fact>]
let ``Run - dbg writes a char as its string literal`` () =
    """@entry
def main = {
  def e: char = "é"
  println(dbg(e))
}"""
    |> runSource
    |> withoutDbgLocations
    |> assertRan "é\n\"é\"\n"
