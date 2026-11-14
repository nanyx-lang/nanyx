module Nanyx.Compiler.Tests.Features.AndPatterns

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private assertCompiles (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``Every part of an and-pattern matches the value, and binds its names for the arm`` () =
    // The pattern-matching page's examples (#427).
    assertCompiles (
        withFinalNewline """def describeNumber: int -> string = { n ->
  match n
    | { < 0 } -> "negative"
    | 0 -> "zero"
    | n & { > 100 } -> "{n}, which is large!"
    | _ -> "positive"
}""")
    assertCompiles (
        withFinalNewline """def show: int -> string = { value ->
  match value
    | { < 0 } & n -> "Negative: {n}"
    | { > 100 } & n -> "Large: {n}"
    | n -> "Something else: {n}"
}""")
    // Names from several parts, nested, in a lambda's parameter and in an if-is.
    assertCompiles (
        withFinalNewline """def f: #some((int, string)) | #none -> int = { o ->
  match o
    | #some((a, b) & pair) -> a + b.length + pair.0
    | #none -> 0
}
def g = { (a, b) & p -> a + b + p.1 }
def h: int -> int = { n -> if n is m & { > 0 } then m else 0 }""")

[<Fact>]
let ``The nested patterns example checks, with an and-pattern on the whole list`` () =
    // The pattern-matching page's "Nesting patterns" example.
    assertCompiles (
        withFinalNewline """type Result =
  | #some(int)
  | #error(string)

type Request = (
  id: int
  status: Result
  tags: list(string)
)

def processRequests: list(Request) -> string = { requests ->
  match requests
    | [] ->
      "No requests"
    | [(id = 1, status = #some(value), tags = ["urgent", ...rest]), ...more] ->
      "Urgent request 1 with value {value}"
    | [(id = id, status = #error(msg), tags = []), (status = #some(val), ...), ...tail] ->
      "Error in request {id}: {msg}, followed by success"
    | [(status = #some(n), ...), ...rest] & requests & { .length > 5 } ->
      "Multiple requests with first one ok, total: {requests.length}"
    | _ ->
      "Other pattern"
}""")

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
        withFinalNewline """def f: #some(int) | #none -> int = { o ->
  match o
    | #some(n) & whole -> n
    | #none & _ -> 0
}""")
    let partialTag =
        Compiler.compile (
            withFinalNewline """def f: #some(int) | #none -> int = { o ->
  match o
    | #some(n) & #some(1) -> n
    | #none -> 0
}""")
    Assert.Contains("Non-exhaustive match", (Assert.Single partialTag.Diagnostics).Message)
