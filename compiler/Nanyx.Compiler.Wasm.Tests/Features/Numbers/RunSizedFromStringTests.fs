module TranspilerWasmTests.Features.Numbers.RunSizedFromString

open Xunit
open TranspilerWasmTestHelpers

/// A program that prints what `<typeName>.fromString` reads from each of `inputs`, the number or the type's tag.
let private readsProgram (typeName: string) (inputs: string list) =
    let lines =
        inputs
        |> List.map (fun input -> $"""  println(show({typeName}.fromString("{input}")))""")
        |> String.concat "\n"

    let source =
        """
import nanyx/console

def show = {
  | #some(n) -> "{n}"
  | #invalidTYPE -> "#invalidTYPE"
}

@entry
def main = {
LINES
}
"""
    source.ReplaceLineEndings("\n").Replace("TYPE", typeName).Replace("LINES", lines)

let private expected (lines: string list) = (lines |> String.concat "\n") + "\n"

[<Theory>]
[<InlineData("I8", "-128", "127", "-129", "128")>]
[<InlineData("I16", "-32768", "32767", "-32769", "32768")>]
[<InlineData("I32", "-2147483648", "2147483647", "-2147483649", "2147483648")>]
[<InlineData("U8", "0", "255", "-1", "256")>]
[<InlineData("U16", "0", "65535", "-1", "65536")>]
[<InlineData("U32", "0", "4294967295", "-1", "4294967296")>]
[<InlineData("U64", "0", "18446744073709551615", "-1", "18446744073709551616")>]
let ``Run - a sized integer reads its range back from toString and nothing past it``
    (typeName: string, smallest: string, largest: string, belowSmallest: string, aboveLargest: string)
    =
    let tag = $"#invalid{typeName}"

    readsProgram typeName [ smallest; largest; belowSmallest; aboveLargest; ""; "-"; "abc"; "+7"; "1 " ]
    |> runSource
    |> assertRan (expected [ smallest; largest; tag; tag; tag; tag; tag; "7"; tag ])

[<Theory>]
[<InlineData("U8")>]
[<InlineData("U16")>]
[<InlineData("U32")>]
[<InlineData("U64")>]
let ``Run - an unsigned integer does not read a leading minus`` (typeName: string) =
    let tag = $"#invalid{typeName}"

    readsProgram typeName [ "-1"; "-0"; "0" ]
    |> runSource
    |> assertRan (expected [ tag; tag; "0" ])

[<Fact>]
let ``Run - F32 reads what float reads, to the nearest F32`` () =
    readsProgram "F32" [ "1.5"; "-0.25"; "1e2"; "1e39"; ""; "-"; "abc" ]
    |> runSource
    |> assertRan (expected [ "1.5"; "-0.25"; "100.0"; "Infinity"; "#invalidF32"; "#invalidF32"; "#invalidF32" ])

[<Fact>]
let ``Run - the sized types read through FromString`` () =
    let source =
        """
import nanyx/console

def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e) = { texts ->
  list.map(texts, { text -> fromString(text) })
}

def showI32 = {
  | #some(n) -> "{n}"
  | #invalidI32 -> "#invalidI32"
}

def showU8 = {
  | #some(n) -> "{n}"
  | #invalidU8 -> "#invalidU8"
}

@entry
def main = {
  def i32s: list(#some(I32) | #invalidI32) = readAll(["-5", "x"])
  def u8s: list(#some(U8) | #invalidU8) = readAll(["200", "300"])
  println(list.map(i32s, showI32) \list.fold("", { acc, s -> acc ++ s ++ " " }))
  println(list.map(u8s, showU8) \list.fold("", { acc, s -> acc ++ s ++ " " }))
}
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "-5 #invalidI32 \n200 #invalidU8 \n"
