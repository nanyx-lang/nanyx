module Nanyx.Formatter.Tests.Features.Types.SignatureBreaking

// A def's type too long for its `: ` line (#2270): its parameters one per line, `->` and the result on a line of their
// own, a result union one case per line, and contexts on a line of their own.

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

/// Formats `source` at `width` columns, and checks that formatting the result again changes nothing.
let private settled (width: int) (source: string) =
    let once = formatWith width source
    Assert.Equal(once, formatWith width once)
    once

[<Fact>]
let ``A signature that fits on its line is not broken`` () =
    Assert.Equal(
        lines [ "def connect"; "  : string, int -> #ok(Conn) | #error(string)"; "  = 0" ],
        settled 50 "def connect: string, int -> #ok(Conn) | #error(string) = 0")

[<Fact>]
let ``A long parameter list goes one parameter per line, then the arrow and the result`` () =
    Assert.Equal(
        lines [ "def connect"; "  : string,"; "    int,"; "    Options"; "    -> #ok(Conn) | #error(string)"; "  = 0" ],
        settled 40 "def connect: string, int, Options -> #ok(Conn) | #error(string) = 0")

[<Fact>]
let ``A long result union goes one case per line, each after a bar`` () =
    Assert.Equal(
        lines [
            "def lookup"
            "  : string"
            "    -> | #some(Entry)"
            "       | #notFound"
            "       | #error(string)"
            "  = 0"
        ],
        settled 40 "def lookup: string -> #some(Entry) | #notFound | #error(string) = 0")

[<Fact>]
let ``A union that is the whole type goes one case per line`` () =
    Assert.Equal(
        lines [ "def pick"; "  : | #first"; "    | #second"; "    | #third"; "  = #first" ],
        settled 20 "def pick: #first | #second | #third = #first")

[<Fact>]
let ``Long contexts go on a line of their own, and the rest of the type stays whole where it fits`` () =
    Assert.Equal(
        lines [ "def serve"; "  : [Console, FileSystem, Http, Clock]"; "    Request -> Response"; "  = 0" ],
        settled 40 "def serve: [Console, FileSystem, Http, Clock] Request -> Response = 0")

[<Fact>]
let ``Contexts that still don't fit go one per line, under the first`` () =
    Assert.Equal(
        lines [ "def serve"; "  : [Console,"; "     FileSystem,"; "     Http]"; "    Request -> Response"; "  = 0" ],
        settled 25 "def serve: [Console, FileSystem, Http] Request -> Response = 0")

[<Fact>]
let ``Contexts and a long parameter list break together`` () =
    Assert.Equal(
        lines [ "def serve"; "  : [Console, Http]"; "    Request,"; "    Options,"; "    string"; "    -> Response"; "  = 0" ],
        settled 30 "def serve: [Console, Http] Request, Options, string -> Response = 0")

[<Fact>]
let ``A type inside a parameter is not broken`` () =
    Assert.Equal(
        lines [ "def apply"; "  : (int -> #a | #b | #c | #d),"; "    int"; "    -> int"; "  = 0" ],
        settled 20 "def apply: (int -> #a | #b | #c | #d), int -> int = 0")

[<Fact>]
let ``Only the outer arrow of a curried function breaks`` () =
    Assert.Equal(
        lines [ "def curried"; "  : int"; "    -> string -> bool"; "  = 0" ],
        settled 20 "def curried: int -> string -> bool = 0")

[<Fact>]
let ``A declaration with no value breaks its type too`` () =
    Assert.Equal(
        lines [ "def connect"; "  : string,"; "    int"; "    -> #ok | #error" ],
        settled 20 "def connect: string, int -> #ok | #error\n")

[<Fact>]
let ``A comment before the equals keeps its line with a broken signature`` () =
    Assert.Equal(
        lines [ "def connect"; "  : string,"; "    int"; "    -> #ok | #error -- opens it"; "  = 0" ],
        settled 25 (lines [ "def connect: string, int -> #ok | #error  -- opens it"; "  = 0" ]))
