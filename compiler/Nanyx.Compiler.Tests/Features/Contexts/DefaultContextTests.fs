module Nanyx.Compiler.Tests.Features.DefaultContext

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private valueOrDefault =
    """
def valueOrDefault: [Default(a)] #some(a) | #none -> a = {
  | #some(v) -> v
  | #none -> default()
}
"""

[<Fact>]
let ``A call that requires Default of a type with no default is an error that names it`` () =
    let source =
        $"""{valueOrDefault}
type Point = (x: int, y: int)
def p: Point = valueOrDefault(#none)
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("valueOrDefault", at)
    Assert.Contains("'Point.default' is not defined", message)

[<Fact>]
let ``A call that requires Default of a function is an error`` () =
    let source =
        $"""{valueOrDefault}
def f: int -> int = valueOrDefault(#none)
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("valueOrDefault", at)
    Assert.Contains("'Default'", message)
