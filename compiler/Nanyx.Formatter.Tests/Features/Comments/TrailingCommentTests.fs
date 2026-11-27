/// Comments at the end of a line, after code (#1633).
module Nanyx.Formatter.Tests.TrailingCommentTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A one-line def's comment stays on its first line when its body goes on lines of its own`` () =
    formatsTo
        """context Tx = (commit: () -> ())

def viaTx: [Tx] () -> () = { -- a Tx serves
  commit()
}"""
        """context Tx = (commit: () -> ())

def viaTx: [Tx] () -> () = { commit() } -- a Tx serves"""

[<Fact>]
let ``A comment after a lambda's { or -> stays there`` () =
    unchanged
        """def viaTx: [Tx] () -> () = { -- a Tx serves
  commit()
}
"""
    unchanged
        """def m = { x -> -- why
  x + 1
}
"""

[<Fact>]
let ``A comment after a def that stays on one line, or after the } of one written over lines, stays where it is`` () =
    unchanged
        """def h = 1 -- short

def k = { x -> x + 1 } -- stays
"""
    unchanged
        """def g = {
  1
} -- after g
"""
