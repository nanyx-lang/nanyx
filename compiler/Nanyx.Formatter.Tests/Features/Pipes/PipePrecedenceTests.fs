module Nanyx.Formatter.Tests.Features.Pipes.PipePrecedence

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

// A pipe binds tighter than every binary operator (#986).

[<Fact>]
let ``A pipe used as an operand is not bracketed`` () =
    unchanged "def v = [p.r, p.g, p.b] \\min ?? 0.0\n"
    unchanged (normalized """def v = s \trim == "" """)
    unchanged "def v = a + b \\f\n"
    Assert.Equal("def v = x \\f + 1\n", format "def v = (x \\f) + 1")
    Assert.Equal("def v = xs \\length == 0\n", format "def v = (xs \\length) == 0")

[<Fact>]
let ``An operator expression piped is bracketed`` () =
    unchanged "def v = (a + b) \\f\n"
    unchanged "def v = (0..n - 1) \\toList\n"
    unchanged "def v = n + 1.. \\take(3)\n"
    unchanged "def v = -x \\abs\n"
    unchanged "def v = -(x \\abs)\n"
    unchanged "def v = (x \\f).y\n"
