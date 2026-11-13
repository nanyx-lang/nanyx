module Nanyx.Compiler.Tests.Features.PatternErrors

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A pattern with several bad parts reports each of them, not only the last.

/// The code and the text of each diagnostic compiling `source` gives, in the order reported.
let private errorsIn (source: string) =
    let source = source.ReplaceLineEndings "\n"
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

[<Theory>]
[<InlineData("def f: (int, int) -> int = { p -> match p\n  | (a & a, b & b) -> 1\n  | _ -> 0 }\n")>]
[<InlineData("def f: list(int) -> int = { xs -> match xs\n  | [a & a, b & b] -> 1\n  | _ -> 0 }\n")>]
[<InlineData("def f: list(int) -> int = { xs -> match xs\n  | [a & a, ..., b & b] -> 1\n  | _ -> 0 }\n")>]
[<InlineData("def f: (a: int, b: int) -> int = { r -> match r\n  | (a = a & a, b = b & b) -> 1\n  | _ -> 0 }\n")>]
[<InlineData("def f: int, int -> int = { x, y -> match x, y\n  | a & a, b & b -> 1\n  | else -> 0 }\n")>]
let ``Each bad part of a pattern is reported`` (source: string) =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.NameBoundTwiceInPattern.Code, "a & a"; DiagnosticCodes.NameBoundTwiceInPattern.Code, "b & b" ],
        errorsIn source)
