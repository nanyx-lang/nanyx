module Nanyx.Compiler.Tests.Features.NestedTagPatterns

open Xunit
open NyxCompiler

let private assertCompiles (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``Arms that name one tag with different tags in its payload check against the scrutinee's union`` () =
    // Each arm used to make the payload the single tag it names, so `#helpRequested` and `#missingArgument` met.
    assertCompiles (
        "type ParseError = #helpRequested | #missingArgument(string)\n" +
        "def parse: string -> #some(int) | #error(ParseError) = { s -> #some(1) }\n" +
        "def describe: string -> string = { s ->\n" +
        "  match parse(s)\n" +
        "    | #some(n) -> \"ok {n}\"\n" +
        "    | #error(#helpRequested) -> \"help\"\n" +
        "    | #error(#missingArgument(name)) -> \"missing {name}\"\n" +
        "}\n")

[<Fact>]
let ``The payloads' union is inferred from the arms, at any depth`` () =
    assertCompiles (
        "def g = {\n" +
        "  | #a(#b(#x)) -> 1\n" +
        "  | #a(#b(#y)) -> 2\n" +
        "  | #c -> 3\n" +
        "}\n" +
        "def one = g(#a(#b(#y)))\n" +
        "def two = g(#c)\n")

[<Fact>]
let ``A catch-all arm leaves room for payloads the arms do not name`` () =
    assertCompiles (
        "def f: string -> #a(#x | #y | #z) | #b = { s -> #b }\n" +
        "def describe: string -> string = { s ->\n" +
        "  match f(s)\n" +
        "    | #a(#x) -> \"x\"\n" +
        "    | #a(#y) -> \"y\"\n" +
        "    | _ -> \"other\"\n" +
        "}\n")

[<Fact>]
let ``A payload no arm names is reported as a missing case`` () =
    let result =
        Compiler.compile (
            "def f: string -> #a(#x | #y | #z) | #b = { s -> #b }\n" +
            "def describe: string -> string = { s ->\n" +
            "  match f(s)\n" +
            "    | #a(#x) -> \"x\"\n" +
            "    | #a(#y) -> \"y\"\n" +
            "    | #b -> \"b\"\n" +
            "}\n")
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Contains("#a(#z)", diagnostic.Message)
