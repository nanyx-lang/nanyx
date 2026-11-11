/// The formatter writes a double-quoted string's escapes as they were written (#247), though the parser decodes them.
module Nanyx.Formatter.Tests.StringEscapeTests

open Xunit
open Nanyx.Formatter

let private format (source: string) =
    match Formatter.formatSource Formatter.Options.defaults "" source with
    | Result.Ok formatted -> formatted
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// Formatting `source` gives it back unchanged.
let private unchanged (source: string) = Assert.Equal(source, format source)

[<Fact>]
let ``Escapes in a string without holes are kept as written`` () =
    unchanged "def s = \"a\\nb\\tc\\rd\\\\e\\\"f\\{g\\}h\\u{e9}\"\n"

[<Fact>]
let ``Escapes around the holes of an interpolated string are kept as written`` () =
    unchanged "def s = \"\\{x\\} = {x}\\n\\\"{f(\"\\t\")}\\\"\\u{1F600}\"\n"

[<Fact>]
let ``A character written as itself is not turned into an escape`` () =
    unchanged "def s = \"tab:\t{x} quote: '\"\n"

[<Fact>]
let ``A string written across lines stays across lines`` () =
    unchanged "def s = \"first {x}\nsecond\\n\"\n"

[<Fact>]
let ``A single-quoted string keeps its backslashes`` () =
    unchanged "def s = 'a\\nb\\q{c}'\n"
