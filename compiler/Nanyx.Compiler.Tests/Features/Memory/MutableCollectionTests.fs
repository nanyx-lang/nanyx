module Nanyx.Compiler.Tests.Features.MutableCollections

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `MStack(a)` and `MList(a)`, written over `MRef`: each carries its `memory` block's scope through the `MRef` it holds.

[<Fact>]
let ``A stack and a list are used inside their memory block`` () =
    let source =
        """
import nanyx/mstack
import nanyx/mlist
def total = memory {
  def stack = MStack.empty()
  stack \push(1)
  def items = MList.empty()
  items \add("a")
  MStack.length(stack) + MList.length(items)
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("int", Unifier.tyToString typed.Types.["total"])

[<Fact>]
let ``A stack can't leave its memory block`` () =
    let source =
        """
import nanyx/mstack
def leaks = memory { MStack.fromList([1]) }
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)

[<Fact>]
let ``Nor can a list`` () =
    let source =
        """
import nanyx/mlist
def leaks = memory { (items = MList.fromList([1]), name = "x") }
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)

[<Fact>]
let ``A stack holds values of one type`` () =
    let source =
        """
import nanyx/mstack
def mixed = memory {
  def stack = MStack.empty()
  stack \push(1)
  stack \push("two")
  MStack.length(stack)
}
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
