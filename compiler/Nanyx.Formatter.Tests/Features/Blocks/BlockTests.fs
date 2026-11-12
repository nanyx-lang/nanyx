module Nanyx.Formatter.Tests.Features.Blocks.Blocks

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Workflow keywords are printed as written`` () =
    unchanged (lines [ "def f = { args -> proc {"; "    def a = try g(args)"; "    def b = try! h(a)"; "    return b"; "  }"; "}" ])

[<Fact>]
let ``A do block keeps its do, and its statements are indented under it`` () =
    unchanged (lines [ "def main = {"; "  def x = 1"; ""; "  do"; "    def x = 2"; "    println(x)"; ""; "  println(x)"; "}" ])
    unchanged (lines [ "def y ="; "  do"; "    def z = 10"; "    z + 1" ])
    Assert.Equal(lines [ "def main = {"; "  do"; "    f(1)"; ""; "  g()"; "}" ], format (lines [ "def main = {"; "  do"; "        f( 1 )"; "  g()"; "}" ]))

[<Fact>]
let ``A block after use ... in goes on the lines after in, indented, and one expression stays after in (#673)`` () =
    // A statement over several lines is set apart from the next by a blank line, as any is.
    unchanged (lines [ "def main = {"; "  def total = use counter in"; "    def a = counted(3)"; "    a + 1"; ""; "  total"; "}" ])
    unchanged (lines [ "def main = {"; "  use counter in"; "    def a = counted(3)"; "    a + 1"; "}" ])
    Assert.Equal(lines [ "def main = {"; "  use counter in counted(3)"; "}" ], format (lines [ "def main = {"; "  use counter in"; "    counted(3)"; "}" ]))
