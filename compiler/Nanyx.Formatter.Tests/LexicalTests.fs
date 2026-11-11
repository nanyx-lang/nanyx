/// Digit separators and block comments, which the formatter keeps exactly as they are written.
module Nanyx.Formatter.Tests.LexicalTests

open Xunit
open Nanyx.Formatter

let private lines (ls: string list) = String.concat "\n" ls + "\n"

let private format (source: string) =
    match Formatter.formatSource Formatter.Options.defaults "" source with
    | Result.Ok formatted -> formatted
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// Formatting `source` gives it back unchanged.
let private unchanged (source: string) = Assert.Equal(source, format source)

[<Fact>]
let ``Numbers grouped with underscores are kept as written`` () =
    unchanged (lines [ "def million = 1_000_000"; "def pi = 3.141_592"; "def big = 1_000.5e1_0"; "def small = -1_000" ])

[<Fact>]
let ``Block comments at the top level are kept as written`` () =
    unchanged (
        lines
            [ "---"
              "This is a multi-line comment."
              "   Its lines keep their indent,"
              ""
              "and its blank lines."
              "---"
              ""
              "----------------------------------"
              "This is also a multi-line comment."
              "---"
              "----------------------------------"
              "def a = 1" ]
    )

[<Fact>]
let ``A block comment inside a lambda keeps its lines as written`` () =
    unchanged (lines [ "def f = { x ->"; "  ---"; "  one"; ""; "      two"; "  ---"; "  x + 1"; "}" ])

[<Fact>]
let ``A block comment's opening line is indented with the code, and the rest is left as it is`` () =
    Assert.Equal(
        lines [ "def f = { x ->"; "  ---"; "one"; "    ---"; "  x + 1"; "}" ],
        format (lines [ "def f = { x ->"; "    ---"; "one"; "    ---"; "    x + 1"; "}" ])
    )
