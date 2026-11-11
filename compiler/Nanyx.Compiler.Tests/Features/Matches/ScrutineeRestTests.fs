module Nanyx.Compiler.Tests.Features.ScrutineeRests

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private find =
    "def find: int -> #some(string) | #notFound = {\n" +
    "  | 1 -> #some(\"1\")\n" +
    "  | _ -> #notFound\n" +
    "}\n"

[<Fact>]
let ``A match on a call that widened its argument's union needs no catch-all`` () =
    // #183: the call's `#some(b) | r` is `#some(string) | #notFound | r'` here, with `r'` the room widening left.
    // Nothing can add a tag to it, so the arms are checked against the tags written.
    compileClean (
        find +
        "def describe: int -> string = { n ->\n" +
        "  match find(n) \\mapOption({ s -> s ++ \"!\" })\n" +
        "    | #some(s) -> s\n" +
        "    | #notFound -> \"not found\"\n" +
        "}\n")
    |> ignore

[<Fact>]
let ``A match on a call that widened its argument's union is checked against the tags it has`` () =
    let source =
        find +
        "def describe: int -> string = { n ->\n" +
        "  match find(n) \\mapOption({ s -> s ++ \"!\" })\n" +
        "    | #some(s) -> s\n" +
        "}\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Equal("Non-exhaustive match, missing tags: notFound", diagnostic.Message)

[<Fact>]
let ``A match on a union whose rest an annotation names needs a catch-all`` () =
    let source =
        "def value: (#some(int) | r) -> int = { x ->\n" +
        "  match x\n" +
        "    | #some(n) -> n\n" +
        "}\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingCatchAllArm.Code, diagnostic.Code)

[<Fact>]
let ``A match on a call whose rest is its caller's parameter's needs a catch-all`` () =
    // `keep(x)` has the rest of `x`, which whoever calls `describe` can give more tags.
    let source =
        "def keep: (#some(a) | r) -> (#some(a) | r) = { x -> x }\n" +
        "def describe = { x ->\n" +
        "  match keep(x)\n" +
        "    | #some(n) -> \"some\"\n" +
        "    | #none -> \"none\"\n" +
        "}\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingCatchAllArm.Code, diagnostic.Code)
