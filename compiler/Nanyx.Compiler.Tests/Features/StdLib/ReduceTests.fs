module Nanyx.Compiler.Tests.Features.StdLib.Reduce

open Xunit
open Nanyx.Compiler.Tests.TestSupport

// `reduce` starts from the first element and needs no context, so it gives `#none` for an empty collection (#2080).

[<Fact>]
let ``Reducing a list of a type with no Default needs no context and may give none`` () =
    let source =
        """type Point = (x: int, y: int)

def total = { points: list(Point) ->
  points \list.reduce { p, q -> Point(x = p.x + q.x, y = p.y + q.y) }
}
"""
    Assert.Equal("list(Point) -> #some(Point) | #none", typeOf source "total")
