/// A comment between a match arm's `->` and its result stays there, as a lambda's after its `->` does (#3247).
module Nanyx.Formatter.Tests.MatchArmCommentTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A comment on a line of its own after an arm's arrow stays before the result, at its indent`` () =
    unchanged (
        lines
            [ "def g = { (x: int) ->"
              "  match x"
              "    | 0 ->"
              "      -- zero case"
              "      \"zero\""
              "    | _ -> \"other\""
              "}" ]
    )

[<Fact>]
let ``A comment at the end of an arm's arrow line stays at its end, the result on the next line`` () =
    unchanged (
        lines
            [ "def g = { (x: int) ->"
              "  match x"
              "    | 0 -> -- zero case"
              "      \"zero\""
              "    | _ -> \"other\""
              "}" ]
    )

[<Fact>]
let ``A comment after the arrow of an arm whose result is a block stays before its first statement`` () =
    unchanged (
        lines
            [ "def g = { (x: int) ->"
              "  match x"
              "    | 0 ->"
              "      -- zero case"
              "      def y = 1"
              "      y + 1"
              "    | _ -> 2"
              "}" ]
    )
