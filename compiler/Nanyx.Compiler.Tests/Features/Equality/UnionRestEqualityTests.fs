module Nanyx.Compiler.Tests.Features.UnionRestEquality

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A function that compares a value with a tag takes any union that has the tag: `o == #none` is on an `#none | r`.
// Comparing it compares the tags its rest `r` turns out to hold, so each call needs `Eq` of what `r` is there. The
// function is passed nothing for it: the comparison is the compiler's own at each type it is specialised at.

[<Fact>]
let ``A call that gives a rest a tag with a function in it is an error at the call`` () =
    let message, at =
        missingContext """def isNone = { o -> o == #none }
def f = { n -> n + 1 }
def b = isNone(#some(f))
"""
    Assert.Equal(
        "'isNone' compares values of type '#some(int -> int) | ...', which cannot be compared: the payload of '#some' is a function, and functions have no equality",
        message
    )
    Assert.Equal("isNone", at)

[<Fact>]
let ``A function that writes its type, a local one, and one that passes its union on are checked at each call`` () =
    let cases =
        [ "isNoneTyped", "def isNoneTyped: #none | r -> bool = { o -> o == #none }"
          "viaCaller", "def isNone = { o -> o == #none }\ndef viaCaller = { o -> isNone(o) }"
          "viaTyped", "def isNone = { o -> o == #none }\ndef viaTyped: #none | r -> bool = { o -> isNone(o) }" ]
    for name, defs in cases do
        let message, at = missingContext $"{defs}\ndef f = {{ n -> n + 1 }}\ndef b = {name}(#some(f))\n"
        Assert.Equal(
            $"'{name}' compares values of type '#some(int -> int) | ...', which cannot be compared: the payload of '#some' is a function, and functions have no equality",
            message
        )
        Assert.Equal(name, at)
    let message, at =
        missingContext """def f = { n -> n + 1 }
def b = {
  def isNone = { o -> o == #none }
  isNone(#some(f))
}
"""
    Assert.Equal(
        "'isNone' compares values of type '#some(int -> int) | ...', which cannot be compared: the payload of '#some' is a function, and functions have no equality",
        message
    )
    Assert.Equal("isNone", at)

[<Fact>]
let ``A call to an imported function that compares with a tag is checked too`` () =
    let main =
        """module main
import mod
def f = { n -> n + 1 }
def b = isNone(#some(f))
"""
    let result = compileImporting "module mod\nexport def isNone = { o -> o == #none }\n" main
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal(
        "'isNone' compares values of type '#some(int -> int) | ...', which cannot be compared: the payload of '#some' is a function, and functions have no equality",
        diagnostic.Message
    )

[<Fact>]
let ``Comparing with a tag requires no context, and a call to it is passed nothing`` () =
    let typed =
        compileClean """def isNone = { o -> o == #none }
def viaCaller = { o -> isNone(o) }
def b = isNone(#some(3)) or isNone(#none) or viaCaller(#some([1])) or viaCaller(#none)
"""
    Assert.Empty(typed.InferredContextTypes)
    Assert.Empty(ownContextInstances typed)
