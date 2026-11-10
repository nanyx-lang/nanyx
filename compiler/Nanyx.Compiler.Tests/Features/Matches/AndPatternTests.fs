module Nanyx.Compiler.Tests.Features.AndPatterns

open Xunit
open NyxCompiler

let private assertCompiles (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``Every part of an and-pattern matches the value, and binds its names for the arm`` () =
    // The pattern-matching page's examples (#427).
    assertCompiles (
        "def describeNumber: int -> string = { n ->\n" +
        "  match n\n" +
        "    | { < 0 } -> \"negative\"\n" +
        "    | 0 -> \"zero\"\n" +
        "    | n & { > 100 } -> \"{n}, which is large!\"\n" +
        "    | _ -> \"positive\"\n" +
        "}\n")
    assertCompiles (
        "def show: int -> string = { value ->\n" +
        "  match value\n" +
        "    | { < 0 } & n -> \"Negative: {n}\"\n" +
        "    | { > 100 } & n -> \"Large: {n}\"\n" +
        "    | n -> \"Something else: {n}\"\n" +
        "}\n")
    // Names from several parts, nested, in a lambda's parameter and in an if-is.
    assertCompiles (
        "def f: #some((int, string)) | #none -> int = { o ->\n" +
        "  match o\n" +
        "    | #some((a, b) & pair) -> a + b.length + pair.0\n" +
        "    | #none -> 0\n" +
        "}\n" +
        "def g = { (a, b) & p -> a + b + p.1 }\n" +
        "def h: int -> int = { n -> if n is m & { > 0 } then m else 0 }\n")

[<Fact>]
let ``The nested patterns example checks, with an and-pattern on the whole list`` () =
    // The pattern-matching page's "Nesting patterns" example, but for its arm of two requests (#461).
    assertCompiles (
        "type Result =\n" +
        "  | #some(int)\n" +
        "  | #error(string)\n" +
        "\n" +
        "type Request = (\n" +
        "  id: int\n" +
        "  status: Result\n" +
        "  tags: list(string)\n" +
        ")\n" +
        "\n" +
        "def processRequests: list(Request) -> string = { requests ->\n" +
        "  match requests\n" +
        "    | [] ->\n" +
        "      \"No requests\"\n" +
        "    | [(id = 1, status = #some(value), tags = [\"urgent\", ...rest]), ...more] ->\n" +
        "      \"Urgent request 1 with value {value}\"\n" +
        "    | [(status = #some(n), ...), ...rest] & requests & { .length > 5 } ->\n" +
        "      \"Multiple requests with first one ok, total: {requests.length}\"\n" +
        "    | _ ->\n" +
        "      \"Other pattern\"\n" +
        "}\n")

[<Fact>]
let ``Every part of an and-pattern is checked against the value's type`` () =
    let guard = Compiler.compile "def f: int -> int = { n ->\n  match n\n    | m & { .length > 5 } -> m\n    | _ -> 0\n}\n"
    Assert.False(guard.Diagnostics.IsEmpty, "an int has no length")
    let tag = Compiler.compile "def f: int -> int = { n ->\n  match n\n    | m & #some(_) -> m\n    | _ -> 0\n}\n"
    Assert.False(tag.Diagnostics.IsEmpty, "an int is not a tag")

[<Fact>]
let ``A name bound by two parts of an and-pattern is an error`` () =
    let source = "def f: (int, int) -> int = { p ->\n  match p\n    | (a, _) & (_, a) -> a\n}\n"
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(DiagnosticCodes.NameBoundTwiceInPattern.Code, diagnostic.Code)
    Assert.Equal(source.IndexOf "(a, _) &", diagnostic.Span.Value.Start)
    Assert.Contains("'a'", diagnostic.Message)

[<Fact>]
let ``An and-pattern covers what all its parts cover`` () =
    // A guard part covers nothing for certain, so the arm after it is still needed.
    let guarded =
        Compiler.compile "def f: int -> int = { n ->\n  match n\n    | m & { > 100 } -> m\n}\n"
    Assert.Contains("Non-exhaustive match", (Assert.Single guarded.Diagnostics).Message)
    assertCompiles "def f: int -> int = { n ->\n  match n\n    | m & _ -> m\n}\n"
    assertCompiles (
        "def f: #some(int) | #none -> int = { o ->\n" +
        "  match o\n" +
        "    | #some(n) & whole -> n\n" +
        "    | #none & _ -> 0\n" +
        "}\n")
    let partialTag =
        Compiler.compile (
            "def f: #some(int) | #none -> int = { o ->\n" +
            "  match o\n" +
            "    | #some(n) & #some(1) -> n\n" +
            "    | #none -> 0\n" +
            "}\n")
    Assert.Contains("Non-exhaustive match", (Assert.Single partialTag.Diagnostics).Message)
