module Nanyx.Compiler.Tests.Features.Definitions.InitialisationCycles

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// Each diagnostic of `source`'s, as its code, the text it is reported at and its message.
let private errorsOf (source: string) =
    let source = source.ReplaceLineEndings "\n"
    Compiler.compile source
    |> fun result -> result.Diagnostics
    |> List.map (fun diagnostic -> diagnostic.Code, textAt source diagnostic.Span.Value, diagnostic.Message)

let private cycle = DiagnosticCodes.InitialisationCycle.Code

[<Fact>]
let ``Two values that need each other are each an error naming the other`` () =
    let errors =
        errorsOf """
module invalid

def a = b + 1
def b = a + 1
"""
    Assert.Equal<(string * string * string) list>(
        [ cycle, "a", "'a' needs its own value to be built, through 'b'"
          cycle, "b", "'b' needs its own value to be built, through 'a'" ],
        errors)

[<Fact>]
let ``A script's values that need each other are an error before they are used`` () =
    let errors =
        errorsOf """
dbg(a)
def a = b + 1
def b = a + 1
"""
    Assert.Equal<string list>([ cycle; cycle ], errors |> List.map (fun (code, _, _) -> code))

[<Fact>]
let ``A value that needs itself is an error`` () =
    let errors = errorsOf "def n: int = n + 1\n"
    Assert.Equal<(string * string * string) list>([ cycle, "n", "'n' needs its own value to be built" ], errors)

[<Fact>]
let ``A value that calls a function that reads the value is a cycle`` () =
    let errors =
        errorsOf """
def a: int = f(1)
def f = { x: int -> x + a }
"""
    Assert.Equal<(string * string * string) list>([ cycle, "a", "'a' needs its own value to be built, through 'f'" ], errors)

[<Fact>]
let ``Functions that call themselves or each other are not a cycle`` () =
    compileClean """
def isEven = { n: int -> if n == 0 then true else isOdd(n - 1) }
def isOdd = { n: int -> if n == 0 then false else isEven(n - 1) }
def countdown = { n: int -> if n == 0 then 0 else countdown(n - 1) }
def answer = isEven(4)
"""
    |> ignore

[<Fact>]
let ``A local name that shadows a top-level value is not a use of it`` () =
    compileClean """
def a =
  def b = 2
  b + 1

def b = a + 1
"""
    |> ignore
