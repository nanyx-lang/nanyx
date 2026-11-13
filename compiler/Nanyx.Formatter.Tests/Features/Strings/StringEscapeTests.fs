/// The formatter writes a double-quoted string's escapes as they were written (#247), though the parser decodes them.
module Nanyx.Formatter.Tests.StringEscapeTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Escapes in a string without holes are kept as written`` () =
    unchanged (normalized """def s = "a\nb\tc\rd\\e\"f\{g\}h\u{e9}" """)

[<Fact>]
let ``Escapes around the holes of an interpolated string are kept as written`` () =
    unchanged (normalized """def s = "\{x\} = {x}\n\"{f("\t")}\"\u{1F600}" """)

[<Fact>]
let ``A character written as itself is not turned into an escape`` () =
    unchanged (normalized """def s = "é} {x} quote: '" """)

[<Fact>]
let ``A string written across lines stays across lines`` () =
    unchanged (normalized """def s = "first {x}
second\n" """)

[<Fact>]
let ``A single-quoted string keeps its backslashes`` () =
    unchanged (normalized """def s = 'a\nb\q{c}'""")
