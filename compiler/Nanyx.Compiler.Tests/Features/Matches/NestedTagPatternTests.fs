module Nanyx.Compiler.Tests.Features.NestedTagPatterns

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private assertCompiles (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``Arms that name one tag with different tags in its payload check against the scrutinee's union`` () =
    // Each arm used to make the payload the single tag it names, so `#helpRequested` and `#missingArgument` met.
    assertCompiles (
        withFinalNewline """type ParseError = #helpRequested | #missingArgument(string)
def parse: string -> #some(int) | #error(ParseError) = { s -> #some(1) }
def describe: string -> string = { s ->
  match parse(s)
    | #some(n) -> "ok {n}"
    | #error(#helpRequested) -> "help"
    | #error(#missingArgument(name)) -> "missing {name}"
}""")

[<Fact>]
let ``The payloads' union is inferred from the arms, at any depth`` () =
    assertCompiles (
        withFinalNewline """def g = {
  | #a(#b(#x)) -> 1
  | #a(#b(#y)) -> 2
  | #c -> 3
}
def one = g(#a(#b(#y)))
def two = g(#c)""")

[<Fact>]
let ``A catch-all arm leaves room for payloads the arms do not name`` () =
    assertCompiles (
        withFinalNewline """def f: string -> #a(#x | #y | #z) | #b = { s -> #b }
def describe: string -> string = { s ->
  match f(s)
    | #a(#x) -> "x"
    | #a(#y) -> "y"
    | _ -> "other"
}""")

[<Fact>]
let ``A payload no arm names is reported as a missing case`` () =
    let result =
        Compiler.compile (
            withFinalNewline """def f: string -> #a(#x | #y | #z) | #b = { s -> #b }
def describe: string -> string = { s ->
  match f(s)
    | #a(#x) -> "x"
    | #a(#y) -> "y"
    | #b -> "b"
}""")
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Contains("#a(#z)", diagnostic.Message)
