module TranspilerWasmTests.Features.StdLib.RunOrCrash

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - orCrash gives the value in a #some`` () =
    let source =
        """@entry
def main = {
  def n = "41" \int.fromString \orCrash("not a number")
  println("{n + 1}")
}
"""

    runSource source |> assertRan "42\n"

[<Fact>]
let ``Run - orCrash on #none writes its message to stderr and exits with 1`` () =
    let source =
        """def find: int -> #some(int) | #none = { n -> if n > 0 then #some(n) else #none }

@entry
def main = {
  def a = find(1) \orCrash("missing")
  println("{a}")
  def b = find(0) \orCrash("missing")
  println("{b}")
  println("never")
}
"""

    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("1\n", outcome.StandardOutput)
    Assert.Equal("crash: missing\n", outcome.StandardError)

[<Fact>]
let ``Run - orCrash on a result's #error crashes with its message`` () =
    let source =
        """def parse: string -> #some(int) | #error(string) = { s ->
  match int.fromString(s)
    | #some(n) -> #some(n)
    | _ -> #error(s)
}

@entry
def main = {
  def token = "x"
  def a = parse("7") \orCrash("bad")
  println("{a}")
  def b = parse(token) \orCrash("Invalid number: '{token}'")
  println("{b}")
}
"""

    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("7\n", outcome.StandardOutput)
    Assert.Equal("crash: Invalid number: 'x'\n", outcome.StandardError)
