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
let ``An attribute's argument is printed as a tag's payload is`` () =
    unchanged (lines [ "@test(#io)"; "def readsConfig = 1" ])
    unchanged (lines [ "@test()"; "def x = 1" ])
    Assert.Equal(lines [ "@retry(times = 3, #slow)"; "def x = 1" ], format (lines [ "@retry( times=3 , #slow )"; "def x = 1" ]))
