module Nanyx.Compiler.Tests.Features.MRefs

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `MRef(a)` in `nanyx/mref`: a cell as a value, declared `type MRef(a) = [Memory] private box(a)`, so it carries its
// `memory` block's scope.

[<Fact>]
let ``An MRef is made, read and changed inside its memory block`` () =
    let source =
        """
import nanyx/mref
def total = memory {
  def r = MRef.new(1)
  def holder = (ref = r, name = "x")
  MRef.set(holder.ref, 5)
  MRef.update(r, { n -> n + 1 })
  MRef.get(r)
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("int", Unifier.tyToString typed.Types.["total"])

[<Fact>]
let ``An MRef holds values of one type, so a def of one is not generalised`` () =
    let source =
        """
import nanyx/mref
def strings = memory {
  def r = MRef.new([])
  MRef.set(r, [5])
  def read: list(string) = MRef.get(r)
  read
}
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("r, [5]", text)

[<Fact>]
let ``A function that makes an MRef is still generalised`` () =
    let source =
        """
import nanyx/mref
def lengths = memory {
  def make = { () -> MRef.new([]) }
  def numbers = make()
  def words = make()
  MRef.set(numbers, [1])
  MRef.set(words, ["a", "b"])
  list.length(MRef.get(numbers)) + list.length(MRef.get(words))
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("int", Unifier.tyToString typed.Types.["lengths"])

[<Fact>]
let ``An MRef can't leave its memory block`` () =
    let source =
        """
import nanyx/mref
def leaks = memory { MRef.new(1) }
"""
    let code, text = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("MRef.new(1)", text)

[<Fact>]
let ``Nor can a list holding one`` () =
    let source =
        """
import nanyx/mref
def held = memory { [MRef.new(1)] }
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)

[<Fact>]
let ``MRefs have no equality`` () =
    let source =
        """
import nanyx/mref
def same = memory {
  def r = MRef.new(1)
  r == r
}
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("r == r", at)
    Assert.Equal("'MRef(int)' cannot be compared with ==: mutable references have no equality", message)

[<Fact>]
let ``MRefs have no hash`` () =
    let source =
        """
import nanyx/mref
def hashed: [Hash(a)] a -> int = { _ -> 0 }
def h = memory { hashed(MRef.new(1)) }
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("hashed", at)
    Assert.Contains("mutable references have no hash", message)

[<Fact>]
let ``MRefs have no toString`` () =
    let source =
        """
import nanyx/mref
def shown = memory {
  def r = MRef.new(1)
  "{r}"
}
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)

[<Fact>]
let ``Only nanyx/mref makes an MRef`` () =
    let source =
        """
import nanyx/mref
def made = memory { MRef(__nanyx_box_new(1)) }
"""
    let result = Compiler.compile (normalized source)
    Assert.Contains(DiagnosticCodes.NominalConstructedOutsideItsModule.Code,result.Diagnostics |> List.map _.Code)
