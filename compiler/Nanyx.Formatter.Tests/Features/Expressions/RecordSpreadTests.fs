module Nanyx.Formatter.Tests.Features.Expressions.RecordSpread

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A record with a spread is written with its spreads`` () =
    Assert.Equal("def config = (...defaults, protocol = \"https\", ...env)\n", format "def config = ( ...defaults,protocol=\"https\", ... env )")

[<Fact>]
let ``A record with a spread written one part per line joins onto one line when it fits`` () =
    let source =
        """
def config = (
  ...defaults
  protocol = "https"
  ...env
)
"""
    Assert.Equal("def config = (...defaults, protocol = \"https\", ...env)\n", format (source.ReplaceLineEndings "\n"))

[<Fact>]
let ``A record with a spread and a comment among its parts keeps one part per line`` () =
    unchanged (
        """def config = (
  ...defaults
  -- From the environment.
  ...env
)
""".ReplaceLineEndings "\n")
