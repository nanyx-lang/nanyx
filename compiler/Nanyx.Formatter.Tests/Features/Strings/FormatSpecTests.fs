/// The formatter keeps a hole's format spec, `"{price:.2f}"` (#3173), as it was written, though the parser makes the
/// hole a call to `__format`.
module Nanyx.Formatter.Tests.FormatSpecTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A hole's spec is kept byte for byte, spaces and colons in it too`` () =
    unchanged (normalized """def s = "{price:.2f} {n:,} {n:::^5} {n:=+6} {n:} {name: <8} {t:%Y-%m-%d %H:%M}" """)

[<Fact>]
let ``The value before a spec is formatted as any hole's is`` () =
    formatsTo "def s = \"{price * 2.0:e}\"" (normalized "def s = \"{ (price * 2.0) :e}\"")
