module TranspilerWasmTests.Features.StdLib.RunStringUtf8

open Xunit
open TranspilerWasmTestHelpers

// `string.toUtf8` and `string.fromUtf8` turn a string into its bytes and back (#2279).

[<Fact>]
let ``Run - string.toUtf8 gives one-, two-, three- and four-byte characters' bytes, and fromUtf8 reads them back`` () =
    let source =
        """import nanyx/text

@entry
def main = {
  for ["abc", "aé", "€", "😀", "aé€😀"] | s ->
    def bytes = s \toUtf8
    println("{bytes} {string.fromUtf8(bytes)}")
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan
        ("""[97, 98, 99] #some(abc)
[97, 195, 169] #some(aé)
[226, 130, 172] #some(€)
[240, 159, 152, 128] #some(😀)
[97, 195, 169, 226, 130, 172, 240, 159, 152, 128] #some(aé€😀)
""".ReplaceLineEndings "\n")

[<Fact>]
let ``Run - string.toUtf8 of an empty string is empty, and fromUtf8 of no bytes is the empty string`` () =
    let source =
        """import nanyx/text

@entry
def main = {
  println(array.length("" \toUtf8))
  match string.fromUtf8(array.fromList([]))
    | #some(s) -> println(string.length(s))
    | #invalidUtf8 -> println("invalid")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "0\n0\n"

[<Fact>]
let ``Run - string.fromUtf8 accepts the edges of each sequence length`` () =
    let source =
        """import nanyx/text

def decode: list(int) -> string = { xs ->
  match string.fromUtf8(array.fromList(xs \map { x -> U8.fromInt(x) }))
    | #some(s) -> "{s \codePoints}"
    | #invalidUtf8 -> "invalid"
}

@entry
def main = {
  println(decode([127]))
  println(decode([194, 128]))
  println(decode([223, 191]))
  println(decode([224, 160, 128]))
  println(decode([237, 159, 191]))
  println(decode([238, 128, 128]))
  println(decode([239, 191, 191]))
  println(decode([240, 144, 128, 128]))
  println(decode([244, 143, 191, 191]))
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "[127]\n[128]\n[2047]\n[2048]\n[55295]\n[57344]\n[65535]\n[65536]\n[1114111]\n"

[<Fact>]
let ``Run - string.fromUtf8 gives #invalidUtf8 for bytes that aren't well-formed UTF-8`` () =
    let source =
        """import nanyx/text

def decode: list(int) -> string = { xs ->
  match string.fromUtf8(array.fromList(xs \map { x -> U8.fromInt(x) }))
    | #some(s) -> s
    | #invalidUtf8 -> "invalid"
}

@entry
def main = {
  -- a stray continuation byte
  println(decode([128]))
  println(decode([97, 191, 98]))
  -- a sequence cut short at the end, and one interrupted
  println(decode([195]))
  println(decode([226, 130]))
  println(decode([240, 159, 152]))
  println(decode([195, 97]))
  -- overlong encodings
  println(decode([192, 128]))
  println(decode([193, 191]))
  println(decode([224, 159, 191]))
  println(decode([240, 143, 191, 191]))
  -- surrogates
  println(decode([237, 160, 128]))
  println(decode([237, 191, 191]))
  -- past 0x10FFFF
  println(decode([244, 144, 128, 128]))
  println(decode([245, 128, 128, 128]))
  println(decode([255]))
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan (String.replicate 15 "invalid\n")

[<Fact>]
let ``Run - string.toUtf8 and fromUtf8 work on a 100,000-byte string`` () =
    let source =
        """import nanyx/text

@entry
def main = {
  def s = string.repeat("aé€😀", 10000)
  def bytes = s \toUtf8
  println(array.length(bytes))
  match string.fromUtf8(bytes)
    | #some(back) -> println("{string.length(back)} {back == s}")
    | #invalidUtf8 -> println("invalid")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "100000\n100000 true\n"
