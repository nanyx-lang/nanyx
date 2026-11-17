module Nanyx.Compiler.Tests.Features.TypedContext

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `Typed(a)`: the compiler gives an instance for every type, whose `typeOf` is `type a` (see `TypeValues`).

[<Fact>]
let ``typeOf is a value of type Type(a)`` () =
    let typed = compileClean (normalized "def describe: [Typed(a)] a -> Type(a) = { value -> typeOf }\n\ndef t = describe(5)\n")
    Assert.Equal("Type(int)", Unifier.tyToString typed.Types.["t"])

[<Fact>]
let ``A use of Typed can't make it stand for another type`` () =
    let source =
        """def describe: [Typed(a)] a -> Type(a) = { value -> typeOf }

def t = {
  use Typed(int)(typeOf = type string)
  describe(5)
}
"""
    let code, _ = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
