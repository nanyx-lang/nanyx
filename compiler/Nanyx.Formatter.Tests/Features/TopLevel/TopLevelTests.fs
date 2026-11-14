module Nanyx.Formatter.Tests.Features.TopLevel.TopLevels

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A dotted module declaration is printed as written`` () =
    unchanged "module collections.list\n\nexport def one = 1\n"

[<Fact>]
let ``An attribute stays on the line before its item`` () =
    Assert.Equal(
        lines [ "def a = 1"; ""; "@entry"; "def main = {"; "  a"; "}" ],
        format (lines [ "def a = 1"; "@entry"; "def main = {"; "  a"; "}" ]))

[<Fact>]
let ``@partial stays on the line of the module declaration it marks`` () =
    let source = lines [ "@partial module utils"; ""; "export def one = 1" ]
    Assert.Equal(source, format source)
    Assert.Equal(source, format (lines [ "@partial"; "module utils"; ""; "export def one = 1" ]))

[<Fact>]
let ``A context member's attribute is printed on the line above the member`` () =
    let expected =
        """context Async = (
  @customKeywordType(#bind)
  await: [c] (int, ([c] int -> int)) -> int
  zero: int
)
"""
    Assert.Equal(expected.ReplaceLineEndings "\n", format "context Async = (@customKeywordType( #bind ) await: [c] (int, ([c] int -> int)) -> int, zero: int)\n")
    unchanged (expected.ReplaceLineEndings "\n")

[<Fact>]
let ``An attribute's argument is printed as a tag's payload is`` () =
    unchanged (lines [ "@test(#io)"; "def readsConfig = 1" ])
    unchanged (lines [ "@test()"; "def x = 1" ])
    Assert.Equal(lines [ "@retry(times = 3, #slow)"; "def x = 1" ], format (lines [ "@retry( times=3 , #slow )"; "def x = 1" ]))
