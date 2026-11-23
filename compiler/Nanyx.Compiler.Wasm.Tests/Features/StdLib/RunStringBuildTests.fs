module TranspilerWasmTests.Features.StdLib.RunStringBuild

open Xunit
open TranspilerWasmTestHelpers

// `string.replace`, `repeat`, `padStart`, `padEnd` and `reverse` work out the length of their result first and write it
// into one new string (`__nanyx_string_new` and `__nanyx_string_write`, #2313), and give back the string they were
// given when it is their result. `runSource` checks the memory, so these also show that the helpers free nothing they
// borrow.

[<Fact>]
let ``Run - string.replace replaces every occurrence, at either end, next to each other and of several bytes`` () =
    let source =
        normalized """import nanyx/text

def show: string, string, string -> string = { s, pattern, replacement ->
  "[{string.replace(s, pattern = pattern, replacement = replacement)}]"
}

@entry
def main = {
  println(show("a-b--c", "-", "+"))
  println(show("-a-", "-", "+"))
  println(show("abc", "x", "+"))
  println(show("", "x", "+"))
  println(show("abc", "", "+"))
  println(show("abc", "abc", ""))
  println(show("aaa", "aa", "b"))
  println(show("a, b, c", ", ", ""))
  println(show("a-b", "-", " to "))
  println(show("café été", "é", "€"))
  println(show("x😀y😀", "😀", "é"))
}
"""
    runSource source
    |> assertRan "[a+b++c]\n[+a+]\n[abc]\n[]\n[abc]\n[]\n[ba]\n[abc]\n[a to b]\n[caf€ €t€]\n[xéyé]\n"

[<Fact>]
let ``Run - string.repeat repeats any number of times, and none for 0 or less`` () =
    let source =
        normalized """import nanyx/text

@entry
def main = {
  println("[{string.repeat("ab", 0)}] [{string.repeat("ab", -2)}] [{string.repeat("", 5)}]")
  println("[{string.repeat("ab", 1)}] [{string.repeat("ab", 2)}] [{string.repeat("abc", 7)}]")
  println("[{string.repeat("é€", 3)}]")
  println(string.length(string.repeat("x😀", 100001)))
}
"""
    runSource source
    |> assertRan "[] [] []\n[ab] [abab] [abcabcabcabcabcabcabc]\n[é€é€é€]\n500005\n"

[<Fact>]
let ``Run - string.padStart and padEnd cut their fill at the width, a byte count, and leave a string as wide as it unchanged`` () =
    let source =
        normalized """import nanyx/text

@entry
def main = {
  println("[{string.padStart("7", width = 3, fill = "0")}] [{string.padEnd("7", width = 3, fill = "0")}]")
  println("[{string.padStart("ab", width = 7, fill = "xyz")}] [{string.padEnd("ab", width = 7, fill = "xyz")}]")
  println("[{string.padStart("long", width = 4, fill = "0")}] [{string.padEnd("long", width = 2, fill = "0")}]")
  println("[{string.padStart("ab", width = 5, fill = "")}] [{string.padEnd("", width = 3, fill = "-")}]")
  println("[{string.padStart("é", width = 8, fill = "€")}] [{string.padEnd("é", width = 6, fill = "é")}]")
  println(string.length(string.padStart("é", width = 6, fill = "€")))
  println(string.length(string.padStart("x", width = 100000, fill = "ab")))
}
"""
    runSource source
    |> assertRan "[007] [700]\n[xyzxyab] [abxyzxy]\n[long] [long]\n[ab] [---]\n[€€é] [ééé]\n6\n100000\n"

[<Fact>]
let ``Run - string.reverse turns characters of one to four bytes around, each whole`` () =
    let source =
        normalized """import nanyx/text

@entry
def main = {
  println("[{string.reverse("")}] [{string.reverse("x")}] [{string.reverse("ab")}]")
  println("[{string.reverse("a€b😀c")}] [{string.reverse("é€😀")}] [{string.reverse("😀")}]")
  println(string.reverse(string.repeat("ab", 50000)) == string.repeat("ba", 50000))
}
"""
    runSource source |> assertRan "[] [x] [ba]\n[c😀b€a] [😀€é] [😀]\ntrue\n"

let private building =
    normalized """import nanyx/text

def made: int -> int = { n -> string.length("a-b-c-d-{n}") }

def replaced: int -> int = { n -> string.length(string.replace("a-b-c-d-{n}", pattern = "-", replacement = "+")) }

def unmatched: int -> int = { n -> string.length(string.replace("a-b-c-d-{n}", pattern = "/", replacement = "+")) }

def paddedStart: int -> int = { n -> string.length(string.padStart("a-b-c-d-{n}", width = 40, fill = "ab")) }

def paddedEnd: int -> int = { n -> string.length(string.padEnd("a-b-c-d-{n}", width = 40, fill = "ab")) }

def wide: int -> int = { n -> string.length(string.padStart("a-b-c-d-{n}", width = 2, fill = " ")) }

def repeated: int -> int = { n -> string.length(string.repeat("a-b-c-d-{n}", n)) }

def reversed: int -> int = { n -> string.length(string.reverse("a-é-€-😀-{n}")) }

def accented: int -> int = { n -> string.length("a-é-€-😀-{n}") }
"""

[<Fact>]
let ``Memory - replace, repeat, padStart, padEnd and reverse each make one allocation, and none for a string unchanged`` () =
    use program = instantiateWasm building []
    let liveBefore = program.LiveBlocks
    for n in [ 7; 10000 ] do
        let made, length = allocationsOf program "made" n
        let accented, accentedLength = allocationsOf program "accented" n
        let count name = fst (allocationsOf program name n)
        Assert.Equal(made + 1, count "replaced")
        Assert.Equal(made, count "unmatched")
        Assert.Equal(made + 1, count "paddedStart")
        Assert.Equal(made + 1, count "paddedEnd")
        Assert.Equal(made, count "wide")
        Assert.Equal(made + 1, count "repeated")
        Assert.Equal(accented + 1, count "reversed")
        Assert.Equal(length * n, snd (allocationsOf program "repeated" n))
        Assert.Equal(accentedLength, snd (allocationsOf program "reversed" n))
    Assert.Equal(liveBefore, program.LiveBlocks)
