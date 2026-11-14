module TranspilerWasmTests.Features.StdLib.RunStringCodePoint

open Xunit
open TranspilerWasmTestHelpers

// `string.codePoints` and `string.fromCodePoint` turn characters into numbers and back (#1260).

[<Fact>]
let ``Run - string.codePoints decodes one-, two-, three- and four-byte characters`` () =
    let source =
        """import nanyx/console
import nanyx/text

@entry
def main = {
  println("aé€😀" \codePoints)
  println("" \codePoints)
  println(list.length("héllo" \codePoints) == list.length("héllo" \chars))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "[97, 233, 8364, 128512]\n[]\ntrue\n"

[<Fact>]
let ``Run - string.fromCodePoint encodes each code point, and codePoints reads it back`` () =
    let source =
        """import nanyx/console
import nanyx/text

def roundTrip: int -> string = { c ->
  match string.fromCodePoint(c)
    | #some(s) -> "{s} {s \codePoints}"
    | #invalidCodePoint -> "invalid"
}

@entry
def main = {
  for [97, 233, 8364, 128512] | c -> println(roundTrip(c))
  println([0, 127, 128, 2047, 2048, 65535, 65536, 1114111] \map { c ->
    match string.fromCodePoint(c)
      | #some(s) -> s \codePoints
      | #invalidCodePoint -> []
  })
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "a [97]\né [233]\n€ [8364]\n😀 [128512]\n[[0], [127], [128], [2047], [2048], [65535], [65536], [1114111]]\n"

[<Fact>]
let ``Run - string.fromCodePoint gives #invalidCodePoint for negatives, surrogates and past 0x10FFFF`` () =
    let source =
        """import nanyx/console
import nanyx/text

@entry
def main = {
  println([-1, 55296, 57343, 1114112] \map { c -> string.fromCodePoint(c) })
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "[#invalidCodePoint, #invalidCodePoint, #invalidCodePoint, #invalidCodePoint]\n"

[<Fact>]
let ``Run - string.codePoints works on a string of 20,000 characters`` () =
    let source =
        """import nanyx/console
import nanyx/text

@entry
def main = {
  def points = string.repeat("é", 20000) \codePoints
  println("{list.length(points)} {list.sum(points)}")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "20000 4660000\n"
