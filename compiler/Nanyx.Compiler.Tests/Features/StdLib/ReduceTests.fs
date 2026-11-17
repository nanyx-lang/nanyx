module Nanyx.Compiler.Tests.Features.StdLib.Reduce

open Xunit
open Nanyx.Compiler.Tests.TestSupport

// `reduce` starts from the element type's `Default`, so a type with none can't be reduced (#1792).

[<Fact>]
let ``Reducing a list of a type with no Default is the missing-context error`` () =
    let source =
        """type Point = (x: int, y: int)

def total: list(Point) -> Point = { points ->
  points \list.reduce { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }
}
"""
    let message, at = missingContext (normalized source)
    Assert.Equal("list.reduce", at)
    Assert.Contains("'Point.default' is not defined", message)
