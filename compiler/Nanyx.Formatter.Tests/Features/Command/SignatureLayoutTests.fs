module Nanyx.Formatter.Tests.Features.Command.SignatureLayout

open Xunit
open Nanyx.Formatter

[<Fact>]
let ``A signature that fits stays on one line`` () =
    Assert.Equal(Some "x: int", SignatureLayout.layOut 20 "x" "int")

[<Fact>]
let ``A signature that does not fit puts its type on the next line, broken, as a def's`` () =
    Assert.Equal(
        Some "Points.Point.new\n  : int,\n    int\n    -> #some(Point) | #error(string)",
        SignatureLayout.layOut 40 "Points.Point.new" "int, int -> #some(Point) | #error(string)")

[<Fact>]
let ``A signature the formatter can't read as a def's is not laid out`` () =
    Assert.Equal(None, SignatureLayout.layOut 30 "==" "a, a -> bool")

[<Fact>]
let ``A type still too long for its line is broken as a def's`` () =
    Assert.Equal(
        Some "lookup\n  : string\n    -> | #some(Entry)\n       | #notFound\n       | #error(string)",
        SignatureLayout.layOut 30 "lookup" "string -> #some(Entry) | #notFound | #error(string)")
