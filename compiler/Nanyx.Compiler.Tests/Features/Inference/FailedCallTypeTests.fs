/// A call that fails for a reason outside its callee's written type gives back what that type says, unknown only
/// where a failed argument would have said (#1947).
module Nanyx.Compiler.Tests.Features.Inference.FailedCallTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The codes of the diagnostics compiling `source` gives, and the type it infers for `name`.
let private failedCall (source: string) (name: string) =
    let result = Compiler.compile (normalized source)
    result.Diagnostics |> List.map _.Code, Unifier.tyToString result.Typed.Value.Types.[name]

let private escaping =
    """context Foo = (bar: string -> string)

def fooEscapes2: [Foo] string -> ([Foo] () -> string) = { s ->
  def inner: [Foo] () -> string = { bar(s) }
  inner
}

def noEscape = fooEscapes2("Hello from Foo!")
"""

[<Fact>]
let ``A call missing a context gives back its callee's result`` () =
    Assert.Equal(([ DiagnosticCodes.MissingContextMembers.Code ], "[Foo] () -> string"), failedCall escaping "noEscape")

[<Fact>]
let ``A wrong use of a call missing a context is an error of its own`` () =
    let result = Compiler.compile (normalized (escaping + "def used = noEscape + 1\n"))
    Assert.Equal<string list>([ DiagnosticCodes.MissingContextMembers.Code; DiagnosticCodes.TypeMismatch.Code ], result.Diagnostics |> List.map _.Code)
    Assert.Equal("'[Foo] () -> string' is not a number, so it cannot be used with +", result.Diagnostics.[1].Message)

[<Fact>]
let ``A call given an argument of the wrong type gives back its callee's result`` () =
    let source =
        """def f: int -> string = { n -> "x" }
def v = f("no")
"""
    Assert.Equal(([ DiagnosticCodes.TypeMismatch.Code ], "string"), failedCall source "v")

[<Fact>]
let ``A call given too many arguments gives back its callee's result`` () =
    let source =
        """def f: int -> string = { n -> "x" }
def v = f(1, 2)
"""
    Assert.Equal(([ DiagnosticCodes.ArgumentCount.Code ], "string"), failedCall source "v")

[<Fact>]
let ``A call that passes by position what its callee takes by name gives back its callee's result`` () =
    let source =
        """def area: (width: int, height: int) -> string = { r -> "x" }
def v = area(1, 2)
"""
    Assert.Equal(([ DiagnosticCodes.PositionalForNamedArgument.Code ], "string"), failedCall source "v")

[<Fact>]
let ``A call to a definition a module block does not export gives back its result`` () =
    let source =
        """module inner =
  def hidden: int -> string = { n -> "x" }

def v = inner.hidden(1)
"""
    Assert.Equal(([ DiagnosticCodes.NotExported.Code ], "string"), failedCall source "v")

[<Fact>]
let ``A call's result is unknown where a failed argument would have said what it is`` () =
    Assert.Equal(([ DiagnosticCodes.TypeMismatch.Code ], "list(???)"), failedCall "def v = list.map([1, 2], 5)\n" "v")
