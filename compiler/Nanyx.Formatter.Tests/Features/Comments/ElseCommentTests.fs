/// Comments before and after an `if`'s `else` (#3245).
module Nanyx.Formatter.Tests.ElseCommentTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A comment on a line of its own before an else stays before the else`` () =
    unchanged
        """def describe = { (n: int) ->
  if n > 0 then
    "positive"
  -- before else
  else
    "not positive"
}
"""

[<Fact>]
let ``A comment before an else stays there when the then branch ends in a comment`` () =
    unchanged
        """def describe = { (n: int) ->
  if n > 0 then
    "positive" -- pos
  -- before else
  else
    "not positive"
}
"""

[<Fact>]
let ``A comment before an else stays there after a then branch on the if's line`` () =
    unchanged
        """def describe = { (n: int) ->
  if n > 0 then "positive"
  -- before else
  else
    println("no")
    "not positive"
}
"""

[<Fact>]
let ``A comment after an else stays at the end of its line`` () =
    unchanged
        """def describe = { (n: int) ->
  if n > 0 then
    "positive"
  else -- after else
    "not positive"
}
"""
