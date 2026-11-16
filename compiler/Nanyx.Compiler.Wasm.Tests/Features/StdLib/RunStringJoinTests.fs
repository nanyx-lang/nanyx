module TranspilerWasmTests.Features.StdLib.RunStringJoin

open Xunit
open TranspilerWasmTestHelpers

// `string.join` copies each piece into its result once (`__nanyx_string_join`), and `concat`, `replace`, `uppercase`,
// `lowercase` and `reverse` are built on it (#1291). `runSource` checks the memory, so these also show that the join
// frees nothing it borrows.

[<Fact>]
let ``Run - string join puts the separator between each pair, empty pieces included`` () =
    let source =
        normalized """import nanyx/text

@entry
def main = {
  println("[{string.join([], ",")}]")
  println("[{string.join(["a"], ",")}]")
  println("[{string.join(["", "a", "", "b", ""], ",")}]")
  println("[{string.join(["one", "two", "three"], ", ")}]")
  println("[{string.concat(["x", "", "yz"])}]")
  println("[{string.replace("a-b--c", pattern = "-", replacement = "+")}]")
}
"""
    runSource source |> assertRan "[]\n[a]\n[,a,,b,]\n[one, two, three]\n[xyz]\n[a+b++c]\n"

[<Fact>]
let ``Run - uppercase, lowercase and reverse change a string's bytes or characters in order`` () =
    let source =
        normalized """import nanyx/text

@entry
def main = {
  println("[{string.uppercase("Hello, wörld 123")}]")
  println("[{string.lowercase("Hello, WÖRLD 123")}]")
  println("[{string.uppercase("")}] [{string.reverse("")}]")
  println("[{string.reverse("héllo")}]")
}
"""
    runSource source |> assertRan "[HELLO, WöRLD 123]\n[hello, wÖrld 123]\n[] []\n[olléh]\n"

[<Fact>]
let ``Run - string join of 100,000 pieces gives every byte of them`` () =
    // Joining with `++` copied the result so far for each piece, and ran out of memory at 20,000 of these.
    let source =
        normalized """import nanyx/text

def lines: int, list(string) -> list(string) = { n, acc ->
  if n == 0 then acc else lines(n - 1, ["item {n % 10}", ...acc])
}

@entry
def main = {
  def joined = string.join(lines(100000, []), "\n")
  println(string.length(joined))
  println(string.length(string.uppercase(joined)))
  println(string.take(string.uppercase(joined), 13))
}
"""
    runSource source |> assertRan "699999\n699999\nITEM 1\nITEM 2\n"
