module Nanyx.Compiler.Tests.Features.Types.UndefinedUse

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `???` has the type `undefined`, as `crash(...)` has (#2532), and a value of that type fits what is expected of it and
// is used without another error, since nothing that uses it is reached (#2530).

/// The codes of the diagnostics compiling `source` gives, and the text each is at.
let private codesAt (source: string) =
    let source = normalized source
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

let private notImplemented = [ DiagnosticCodes.NotImplemented.Code, "???" ]

[<Fact>]
let ``A list of values that never come is the list expected of it`` () =
    Assert.Empty(codesAt "def xs: list(int) = [crash(\"no\")]\ndef ys: list(string) = [crash(\"a\"), crash(\"b\")]\n")

[<Fact>]
let ``??? as a function's whole body leaves the annotation as general as it is written`` () =
    Assert.Equal<(string * string) list>(notImplemented, codesAt "def f: (string, a: Type(a)) -> a = { s, a -> ??? }\n")

[<Theory>]
[<InlineData("def g: int -> int = { n -> n }\ndef n = g(???)\n")>]
[<InlineData("def n: int = ???(1)\n")>]
[<InlineData("def n = { x -> match x | 0 -> ??? | _ -> 1 }\n")>]
[<InlineData("def n = { b -> if b then ??? else 1 }\n")>]
[<InlineData("def xs: list(int) = [???]\n")>]
[<InlineData("type P = (name: string)\ndef g: P -> int = { p -> 1 }\ndef x = ???\ndef a = x + 1\ndef b = x.name\ndef c = x \\g\n")>]
[<InlineData("def main = {\n  def x = ???\n  println(x + 1)\n  println(x.name)\n}\n")>]
let ``??? is the one error wherever it is used`` (source: string) =
    Assert.Equal<(string * string) list>(notImplemented, codesAt source)

[<Fact>]
let ``??? is of type undefined`` () =
    let result = Compiler.compile "def myFunction = { () -> ??? }\n"
    Assert.Equal("() -> undefined", Unifier.tyToString result.Typed.Value.Types.["myFunction"])

[<Fact>]
let ``A value that never comes is used without another error`` () =
    let source =
        """type P = (name: string)
def g: P -> int = { p -> 1 }
def main = {
  def x = crash("no")
  println(x + 1)
  println(x.name)
  println(x \g)
  println(x(1))
  match x
    | 1 -> 2
    | _ -> 3
}
"""
    Assert.Empty(codesAt source)
