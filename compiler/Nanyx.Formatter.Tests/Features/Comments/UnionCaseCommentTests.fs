/// Comments among the cases of a union written over lines (#3246).
module Nanyx.Formatter.Tests.UnionCaseCommentTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A comment after a union's = or after a case stays at the end of its line`` () =
    formatsTo
        """type Shape = -- the shapes
  | #circle(float) -- radius
  | #square(float)"""
        """type Shape =   -- the shapes
  | #circle(float)   -- radius
  | #square(float)"""

[<Fact>]
let ``A comment on a line of its own among a union's cases stays before the case it comes before`` () =
    unchanged
        """type Shape =
  -- before the first
  | #circle(float)
  -- a square by its side
  | #square(float)
  | #point -- last
"""

[<Fact>]
let ``A private union keeps the comments among its cases`` () =
    unchanged
        """type Shape = private -- hidden
  | #circle(float)
  -- a square by its side
  | #square(float)
"""
