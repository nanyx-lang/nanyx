module Nanyx.Compiler.Tests.Features.Types.PrivateFields

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A private record's fields are its representation: outside its home module they are not read, matched, destructured
// or spread (#2341).

let private secure =
    """module secure =
  export type Secret = private (key: string)
  export def Secret.make: () -> Secret = { Secret(key = "s3cret") }
  export def Secret.isMaster: Secret -> bool = { s -> s.key == "master" }
  export def Secret.reveal: Secret -> string = { s ->
    def (key = k) = s
    def t = (...s)
    match s
      | (key = "master") -> "master"
      | _ -> t.key ++ k
  }

def s = secure.Secret.make()
"""

let private takenApart =
    "The private type 'Secret' can only be taken apart in the module that defines it: use a function that module exports instead"

let private errorOf (use': string) = onlyDiagnostic (normalized (secure + use' + "\n"))

[<Fact>]
let ``Reading a private type's field outside its module is an error`` () =
    Assert.Equal(
        (DiagnosticCodes.NominalConstructedOutsideItsModule.Code,
         "The private type 'Secret' has no field 'key' outside the module that defines it: use a function that module exports instead",
         "key"),
        errorOf "println(s.key)")

[<Fact>]
let ``Destructuring a private type's fields outside its module is an error`` () =
    Assert.Equal((DiagnosticCodes.NominalConstructedOutsideItsModule.Code, takenApart, "(key = k)"), errorOf "def (key = k) = s\nprintln(k)")

[<Fact>]
let ``Matching a private type's fields outside its module is an error`` () =
    Assert.Equal(
        (DiagnosticCodes.NominalConstructedOutsideItsModule.Code, takenApart, "(key = k)"),
        errorOf "match s\n  | (key = k) -> println(k)")

[<Fact>]
let ``Spreading a private type's fields outside its module is an error`` () =
    Assert.Equal((DiagnosticCodes.NominalConstructedOutsideItsModule.Code, takenApart, "s"), errorOf "def t = (...s)\nprintln(t)")

[<Fact>]
let ``A private type's fields are read, matched, destructured and spread in its own module`` () =
    let source = normalized (secure + "println(secure.Secret.reveal(s))\nprintln(secure.Secret.isMaster(s))\n")
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A protected type's fields are read, matched, destructured and spread anywhere`` () =
    let source =
        normalized
            """module secure =
  export type Label = protected (text: string)
  export def Label.make: () -> Label = { Label(text = "hi") }

def l = secure.Label.make()
def (text = t) = l
def r = (...l)
println(l.text ++ t ++ r.text)
match l
  | (text = x) -> println(x)
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
