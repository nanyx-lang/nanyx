module Nanyx.Formatter.Tests.Features.Layout.BlankLines

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Comments stay where they were written`` () =
    unchanged (lines [ "-- the answer"; "def a = 42 -- and no other"; ""; "-- the end" ])
    unchanged (lines [ "def f = { x ->"; "  -- add one"; "  def y = x + 1"; "  y * 2 -- doubled"; "}" ])

[<Fact>]
let ``Single blank lines between statements are kept, and runs of them become one`` () =
    Assert.Equal(lines [ "def a = 1"; ""; "def b = 2"; "def c = 3" ], format (lines [ "def a = 1"; ""; ""; ""; "def b = 2"; "def c = 3" ]))
    Assert.Equal(lines [ "def f = {"; "  def a = 1"; ""; "  a"; "}" ], format (lines [ "def f = {"; ""; "  def a = 1"; ""; ""; "  a"; ""; "}" ]))

[<Fact>]
let ``A construct that spans lines gets a blank line before and after it at the top level`` () =
    Assert.Equal(
        lines [ "def zero = 0"; "def one = 1"; ""; "def add = { a, b ->"; "  a + b"; "}"; ""; "def two = 2" ],
        format (lines [ "def zero = 0"; "def one = 1"; "def add = { a, b ->"; "  a + b"; "}"; "def two = 2" ]))

[<Fact>]
let ``Two constructs that span lines get one blank line between them, not two`` () =
    Assert.Equal(
        lines [ "def f = {"; "  1"; "}"; ""; "def g = {"; "  2"; "}" ],
        format (lines [ "def f = {"; "  1"; "}"; "def g = {"; "  2"; "}" ]))

[<Fact>]
let ``Statements in a lambda that span lines are separated, but not from its braces`` () =
    Assert.Equal(
        lines [ "def main = {"; "  def f = { x ->"; "    x"; "  }"; ""; "  def a = 1"; "  def b = 2"; ""; "  match a"; "    | 1 -> b"; "    | _ -> 0"; "}" ],
        format (lines [ "def main = {"; "  def f = { x ->"; "    x"; "  }"; "  def a = 1"; "  def b = 2"; "  match a"; "    | 1 -> b"; "    | _ -> 0"; "}" ]))

[<Fact>]
let ``The blank line goes before a construct's leading comment`` () =
    Assert.Equal(
        lines [ "def a = 1"; ""; "-- Adds two numbers."; "def add = { a, b ->"; "  a + b"; "}" ],
        format (lines [ "def a = 1"; "-- Adds two numbers."; "def add = { a, b ->"; "  a + b"; "}" ]))

[<Fact>]
let ``An indented block separates multi-line constructs and keeps the author's blank lines`` () =
    Assert.Equal(
        lines [ "def x ="; "  def f = { y ->"; "    y"; "  }"; ""; "  f(1)" ],
        format (lines [ "def x ="; "  def f = { y ->"; "    y"; "  }"; "  f(1)" ]))
    Assert.Equal(
        lines [ "module m ="; "  def f = { y ->"; "    y"; "  }"; ""; "  def g = 2" ],
        format (lines [ "module m ="; "  def f = { y ->"; "    y"; "  }"; "  def g = 2" ]))
    unchanged (lines [ "def x ="; "  def a = 1"; ""; "  -- the result"; "  a" ])
    unchanged (lines [ "module m ="; "  def a = 1"; ""; "  def b = 2" ])
    Assert.Equal(
        lines [ "def x ="; "  def a = 1"; ""; "  a" ],
        format (lines [ "def x ="; "  def a = 1"; ""; ""; "  a" ]))

[<Fact>]
let ``Separating constructs is idempotent`` () =
    let once = format (lines [ "def a = 1"; "-- note"; "def f = {"; "  1"; "}"; "def b = 2" ])
    Assert.Equal(once, format once)
