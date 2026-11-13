module Nanyx.Compiler.Tests.Features.ShadowedDefContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A local def and a top-level def of the same name each require their own contexts, inside the block and after it
// (#1096, #1115).

[<Fact>]
let ``A top-level def called after a block requires its own contexts, not those of a local def of its name`` () =
    let typed = compileClean """
context Log = (log: string -> ())

def mk: () -> int = { 1 }

def f: [Log] () -> int = {
  def mk: [Log] () -> int = {
    log("x")
    2
  }
  mk()
}

def g: () -> int = { mk() }
"""
    Assert.Equal("() -> int", Unifier.tyToString typed.Types.["g"])

[<Fact>]
let ``A local def's parameterised context is found by its own signature`` () =
    let typed = compileClean """
context Show(a) = (show: a -> string)

def label: () -> string = { "top" }

def f: [Show(int)] () -> string = {
  def label: [Show(b)] b -> string = { x -> show(x) }
  label(1)
}

def g: () -> string = { label() }
"""
    Assert.Equal("() -> string", Unifier.tyToString typed.Types.["g"])
