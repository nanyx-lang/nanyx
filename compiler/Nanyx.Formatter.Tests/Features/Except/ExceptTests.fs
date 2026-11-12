module Nanyx.Formatter.Tests.Features.Except.Excepts

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``An except keeps one arm on its line and puts several on the lines after it`` () =
    unchanged "def v = int.fromString(s) except #invalidInt -> #invalidVersionNumber\n"
    unchanged "def v = xs \\find(p) except #notFound | #empty -> #none\n"
    unchanged (lines [ "def v = load(n) except"; "  | #error(#notFound) -> #some(0)"; "  | #error(#io(message)) -> #some(1)" ])
    unchanged (lines [ "def f = { s ->"; "  def n = int.fromString(s) except #invalidInt -> #some(0)"; "  n"; "}" ])
    Assert.Equal("def v = x except #a -> #b\n", format "def v = x   except   #a->#b")

[<Fact>]
let ``An except that is not the last thing in its construct is bracketed`` () =
    unchanged "def v = (x except #a -> #b) \\describe\n"
    unchanged "def v = f((x except #a -> #b), y)\n"
    unchanged "def v = x except #a -> y except #b -> #c\n"
