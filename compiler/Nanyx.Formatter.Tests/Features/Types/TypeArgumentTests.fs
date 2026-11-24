module Nanyx.Formatter.Tests.Features.Types.TypeArguments

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Type parameters and type arguments read as the docs write them`` () =
    unchanged "def deserialise: [Decode(a)] Type(a), string -> a = f\n"
    unchanged "def parse: [Decode(a)] (string, into: Type(a)) -> a = f\n"
    unchanged "def same: [Decode(into)] (string, into: Type(into)) -> into = f\n"
    unchanged "def p = deserialise(type Point, text)\n"
    unchanged "def q = parse(text, into = type list(int))\n"
    unchanged "def r = make(type (int -> string))\n"

[<Fact>]
let ``Type parameters written the old way are printed as written, for the compiler's fix to rewrite`` () =
    unchanged "def deserialise: [Decode(a)] type a, string -> a = f\n"
    unchanged "def parse: [Decode(into)] (string, into: type) -> into = f\n"
