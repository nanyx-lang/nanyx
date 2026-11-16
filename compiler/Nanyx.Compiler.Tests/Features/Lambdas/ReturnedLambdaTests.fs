module Nanyx.Compiler.Tests.Features.Lambdas.ReturnedLambda

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda that gives back a lambda, checked against `a -> b -> c`, checks the one it gives back against `b -> c`, so
// that lambda's parameters have their types while its body is inferred (#1749).

let private typeOf (source: string) (name: string) =
    Unifier.tyToString (compileClean (source.ReplaceLineEndings "\n")).Types.[name]

[<Fact>]
let ``A returned lambda's parameter takes its type from the def's annotation`` () =
    let source =
        """
import nanyx/text

def f: string -> string -> int = { a -> { b -> b \length } }
"""
    Assert.Equal("string -> string -> int", typeOf source "f")

[<Fact>]
let ``A returned lambda at the end of a block takes its type too, at any depth`` () =
    let source =
        """
import nanyx/text

def f: string -> string -> string -> int = { a ->
  def unused = a
  { b ->
    { c ->
      def n = c \length
      n + b \length
    }
  }
}
"""
    Assert.Equal("string -> string -> string -> int", typeOf source "f")
