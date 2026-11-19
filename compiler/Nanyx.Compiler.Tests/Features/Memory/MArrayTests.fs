module Nanyx.Compiler.Tests.Features.MArrays

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `MArray(a)` in `nanyx/marray`: a fixed-length array changed in place, declared
// `type MArray(a) = [Memory] private marray(a)`, so it carries its `memory` block's scope.

[<Fact>]
let ``An MArray is made, changed and copied out inside its memory block`` () =
    let source =
        """
import nanyx/marray
def xs = memory {
  def ys = MArray.init(3, { i -> i * 10 })
  def changed = ys \set(1, 99)
  MArray.toArray(ys)
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("array(int)", Unifier.tyToString typed.Types.["xs"])

[<Fact>]
let ``An MArray can't leave its memory block`` () =
    let source =
        """
import nanyx/marray
def leaks = memory { MArray.fromList([1]) }
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("MArray.fromList([1])", text)

[<Fact>]
let ``MArrays have no equality`` () =
    let source =
        """
import nanyx/marray
def same = memory {
  def xs = MArray.fromList([1])
  xs == xs
}
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("xs == xs", at)
    Assert.Equal("'MArray(int)' cannot be compared with ==: mutable arrays have no equality", message)

[<Fact>]
let ``MArrays have no hash`` () =
    let source =
        """
import nanyx/marray
def hashed: [Hash(a)] a -> int = { _ -> 0 }
def h = memory { hashed(MArray.fromList([1])) }
"""
    let message, _ = missingContext (normalized source)
    Assert.Contains("mutable arrays have no hash", message)

[<Fact>]
let ``create requires a Default of the element type`` () =
    let source =
        """
import nanyx/marray
def n = memory {
  def fs: MArray(int -> int) = MArray.create(2)
  MArray.length(fs)
}
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("MArray.create", at)
    Assert.Contains("'Default'", message)

[<Fact>]
let ``An MArray holds values of one type, so a def of one is not generalised`` () =
    let source =
        """
import nanyx/marray
def strings = memory {
  def xs = MArray.fromList([])
  def changed = MArray.set(xs, 0, 5)
  def read: list(string) = MArray.toList(xs)
  read
}
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
let ``A memory block's unannotated result can be a recursive function's tag union`` () =
    // `run` in nanyx.regex once needed the block's result bound with its type written to check (#2053).
    let source =
        """
import nanyx/marray

def step: [Memory] MArray(int), int, (#some(array(int)) | #none) -> #some(array(int)) | #none = { marks, pos, found ->
  if pos >= 3
    then found
    else
      def marked = MArray.set(marks, 0, pos)
      step(marks, pos + 1, #some(array.init(2, { _ -> pos })))
}

def run: int -> #some(array(int)) | #none = { start ->
  memory {
    def marks = MArray.init(4, { _ -> -1 })
    step(marks, start, #none)
  }
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("int -> #some(array(int)) | #none", typeIn typed "run")
