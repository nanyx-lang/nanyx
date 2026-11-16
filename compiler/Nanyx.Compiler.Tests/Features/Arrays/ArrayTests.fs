module Nanyx.Compiler.Tests.Features.Arrays

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Array is the array type, and its functions are attached to it`` () =
    let source =
        """
import nanyx/array
def xs: Array(string) = array.fromList(["a"])
def n = xs \length
def ys = array.fromList([1, 2]) \map({ x -> x * 2 })
"""
    let typed = compileClean (normalized source)
    Assert.Equal("array(string)", Unifier.tyToString typed.Types.["xs"])
    Assert.Equal("int", Unifier.tyToString typed.Types.["n"])
    Assert.Equal("array(int)", Unifier.tyToString typed.Types.["ys"])

[<Fact>]
let ``Arrays have no order`` () =
    let source =
        """
import nanyx/array
def xs = array.fromList([1, 2])
def b = xs < xs
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("xs < xs", at)
    Assert.Equal("'array(int)' cannot be ordered with <: arrays have no order", message)

[<Fact>]
let ``Arrays have no hash`` () =
    let source =
        """
import nanyx/array
def s = set.fromList([array.fromList([1])])
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("set.fromList", at)
    Assert.Contains("arrays have no hash", message)

[<Fact>]
let ``create requires a Default of the element type`` () =
    let source =
        """
import nanyx/array
def fs: Array(int -> int) = array.create(2)
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("array.create", at)
    Assert.Contains("'Default'", message)
