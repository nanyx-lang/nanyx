module Nanyx.Formatter.Tests.DocTests

open System.Diagnostics
open Xunit
open Nanyx.Formatter

/// `f(a, b, c)`, which breaks one argument per line, indented by 2.
let private call name (args: string list) =
    Doc.group (
        Doc.concat [
            Doc.text $"{name}("
            Doc.nest 2 (Doc.concat [ Doc.softLine; Doc.join (Doc.concat [ Doc.text ","; Doc.line ]) (args |> List.map Doc.text) ])
            Doc.softLine
            Doc.text ")"
        ]
    )

[<Fact>]
let ``A group that fits is laid out on one line`` () =
    Assert.Equal("f(a, b, c)\n", Doc.render 80 (call "f" [ "a"; "b"; "c" ]))

[<Fact>]
let ``A group that does not fit breaks its lines`` () =
    Assert.Equal("f(\n  aaaa,\n  bbbb\n)\n", Doc.render 10 (call "f" [ "aaaa"; "bbbb" ]))

[<Fact>]
let ``A line of exactly the width fits, and one more character breaks`` () =
    let doc = call "f" [ "abc"; "de" ] // "f(abc, de)" is 10 characters
    Assert.Equal("f(abc, de)\n", Doc.render 10 doc)
    Assert.Equal("f(\n  abc,\n  de\n)\n", Doc.render 9 doc)

[<Fact>]
let ``What follows a group on its line counts towards whether it fits`` () =
    let doc = Doc.concat [ call "f" [ "a"; "b" ]; Doc.text " + somethingLong" ]
    Assert.Equal("f(\n  a,\n  b\n) + somethingLong\n", Doc.render 20 doc)
    Assert.Equal("f(a, b) + somethingLong\n", Doc.render 23 doc)

[<Fact>]
let ``What follows a group after a line break does not count`` () =
    let doc = Doc.concat [ call "f" [ "a"; "b" ]; Doc.hardLine; Doc.text "aVeryLongSecondLineThatDoesNotFit" ]
    Assert.Equal("f(a, b)\naVeryLongSecondLineThatDoesNotFit\n", Doc.render 10 doc)

[<Fact>]
let ``An outer group can break while the groups inside it stay flat`` () =
    let inner = call "inner" [ "a"; "b" ]

    let outer =
        Doc.group (
            Doc.concat [
                Doc.text "outer("
                Doc.nest 2 (Doc.concat [ Doc.softLine; Doc.text "firstArgument,"; Doc.line; inner ])
                Doc.softLine
                Doc.text ")"
            ]
        )

    Assert.Equal("outer(\n  firstArgument,\n  inner(a, b)\n)\n", Doc.render 20 outer)

[<Fact>]
let ``A hard line breaks every group around it`` () =
    let doc =
        Doc.group (
            Doc.concat [
                Doc.text "{"
                Doc.nest 2 (Doc.concat [ Doc.line; Doc.text "a"; Doc.hardLine; Doc.text "b" ])
                Doc.line
                Doc.text "}"
            ]
        )

    Assert.Equal("{\n  a\n  b\n}\n", Doc.render 80 doc)

[<Fact>]
let ``Nesting adds to the enclosing indent`` () =
    let doc =
        Doc.concat [
            Doc.text "a"
            Doc.nest 2 (Doc.concat [ Doc.hardLine; Doc.text "b"; Doc.nest 2 (Doc.concat [ Doc.hardLine; Doc.text "c" ]) ])
            Doc.hardLine
            Doc.text "d"
        ]

    Assert.Equal("a\n  b\n    c\nd\n", Doc.render 80 doc)

[<Fact>]
let ``Indent is measured from the start of the line, not from a column in it`` () =
    let doc = Doc.concat [ Doc.text "def longName = "; Doc.nest 2 (Doc.concat [ Doc.text "{"; Doc.hardLine; Doc.text "x" ]) ]
    Assert.Equal("def longName = {\n  x\n", Doc.render 80 doc)

[<Fact>]
let ``No line ends in spaces, and empty lines have no indent`` () =
    let doc = Doc.nest 4 (Doc.concat [ Doc.text "a "; Doc.hardLine; Doc.hardLine; Doc.text "b" ])
    Assert.Equal("a\n\n    b\n", Doc.render 80 doc)

[<Fact>]
let ``The output ends with exactly one line break`` () =
    Assert.Equal("a\n", Doc.render 80 (Doc.concat [ Doc.text "a"; Doc.hardLine; Doc.hardLine ]))
    Assert.Equal("", Doc.render 80 Doc.empty)

[<Fact>]
let ``Text cannot contain a line break`` () =
    Assert.Throws<System.ArgumentException>(fun () -> Doc.text "a\nb" |> ignore) |> ignore
    Assert.Throws<System.ArgumentException>(fun () -> Doc.render 80 (Text "a\nb") |> ignore) |> ignore

[<Fact>]
let ``Verbatim text keeps its line breaks, with no indent added after them`` () =
    let doc = Doc.nest 4 (Doc.concat [ Doc.hardLine; Doc.text "s = "; Doc.Verbatim "\"one\n  two\""; Doc.text ";" ])
    Assert.Equal("\n    s = \"one\n  two\";\n", Doc.render 80 doc)

[<Fact>]
let ``A group with verbatim line breaks in it cannot be flat`` () =
    let doc = Doc.group (Doc.concat [ Doc.text "a"; Doc.line; Doc.Verbatim "\"x\ny\"" ])
    Assert.Equal("a\n\"x\ny\"\n", Doc.render 80 doc)

[<Fact>]
let ``A line suffix goes at the end of its line, after whatever follows it there`` () =
    let doc = Doc.concat [ Doc.text "a"; Doc.lineSuffix "-- note"; Doc.text " }"; Doc.hardLine; Doc.text "b" ]
    Assert.Equal("a } -- note\nb\n", Doc.render 80 doc)

[<Fact>]
let ``Two line suffixes on one line get a line each`` () =
    let doc = Doc.nest 2 (Doc.concat [ Doc.text "a"; Doc.lineSuffix "-- one"; Doc.lineSuffix "-- two"; Doc.hardLine; Doc.text "b" ])
    Assert.Equal("a -- one\n  -- two\n  b\n", Doc.render 80 doc)

[<Fact>]
let ``A line suffix takes no width`` () =
    let doc = Doc.group (Doc.concat [ Doc.text "f("; Doc.lineSuffix "-- a long comment that does not fit"; Doc.softLine; Doc.text "x)" ])
    Assert.Equal("f(x) -- a long comment that does not fit\n", Doc.render 10 doc)

[<Fact>]
let ``Rendering a deeply nested document takes time in proportion to its size`` () =
    let depth = 20_000

    // Not nested in indent as well, or the output itself would grow with the square of the depth.
    let doc =
        Seq.fold
            (fun inner i -> Doc.group (Doc.concat [ Doc.text $"f{i}("; Doc.softLine; inner; Doc.softLine; Doc.text ")" ]))
            (Doc.text "x")
            (seq { 1..depth })

    let stopwatch = Stopwatch.StartNew()
    let rendered = Doc.render 100 doc
    stopwatch.Stop()

    Assert.EndsWith(")\n", rendered)
    Assert.True(stopwatch.Elapsed.TotalSeconds < 5.0, $"Rendering took {stopwatch.Elapsed}")

[<Fact>]
let ``IfBreak follows its group`` () =
    let doc = Doc.group (Doc.concat [ Doc.text "a"; IfBreak(Doc.empty, Doc.text ","); Doc.line; Doc.text "b" ])
    Assert.Equal("a, b\n", Doc.render 80 doc)
    Assert.Equal("a\nb\n", Doc.render 2 doc)

[<Fact>]
let ``IfGroupBroken follows the named group laid out before it`` () =
    let head = NamedGroup(1, Doc.concat [ Doc.text "head"; Doc.nest 2 (Doc.concat [ Doc.softLine; Doc.text ": long" ]) ])
    let doc = Doc.concat [ head; IfGroupBroken(1, Doc.text " broken", Doc.text " flat") ]
    Assert.Equal("head: long flat\n", Doc.render 80 doc)
    Assert.Equal("head\n  : long broken\n", Doc.render 8 doc)

[<Fact>]
let ``A fill puts as many items on a line as fit`` () =
    let doc = Fill [ Doc.text "aaa"; Doc.line; Doc.text "bbb"; Doc.line; Doc.text "ccc"; Doc.line; Doc.text "ddd" ]
    Assert.Equal("aaa bbb\nccc ddd\n", Doc.render 7 doc)
    Assert.Equal("aaa bbb ccc ddd\n", Doc.render 80 doc)

[<Fact>]
let ``A stack sets apart the items that take more than one line, measured where they are`` () =
    let item s = StackItem(Doc.text s, false)
    let long = StackItem(Doc.group (Doc.concat [ Doc.text "long("; Doc.nest 2 (Doc.concat [ Doc.softLine; Doc.text "argument" ]); Doc.softLine; Doc.text ")" ]), false)
    let doc = Stack(true, [ item "a"; long; item "b" ])
    Assert.Equal("a\nlong(argument)\nb\n", Doc.render 80 doc)
    Assert.Equal("a\n\nlong(\n  argument\n)\n\nb\n", Doc.render 10 doc)

[<Fact>]
let ``A stack keeps the author's blank lines and puts the separating one before leading comments`` () =
    let doc = Stack(true, [ StackItem(Doc.text "a", false); StackBlank; StackItem(Doc.text "b", false); StackComment(WrittenComment "-- c", true); StackItem(Doc.concat [ Doc.text "x"; Doc.hardLine; Doc.text "y" ], false) ])
    Assert.Equal("a\n\nb\n\n-- c\nx\ny\n", Doc.render 80 doc)
