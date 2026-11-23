module Nanyx.Compiler.Tests.Features.ExpectedFunctionContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda checked against a function type that requires a context can use that context, wherever the type comes from:
// a record field's, a function's result, as well as a parameter's (see `LambdaSuppliedContextTests`). Whoever calls the
// lambda supplies it (#2467).

[<Fact>]
let ``A lambda given for a record field whose type requires a context can call its members`` () =
    compileClean (
        """
context T = (post: string -> int)

type Client = (ingest: [T] string -> int)

def make: () -> Client = {
  Client(ingest = { s -> post(s) + 1 })
}
"""
            .ReplaceLineEndings "\n"
    )
    |> ignore

[<Fact>]
let ``A lambda a function gives back, whose result type requires a context, can call its members`` () =
    compileClean (
        """
context T = (post: string -> int)

def ingestWith: int -> ([T] string -> int) = { n -> { s -> post(s) + n } }
"""
            .ReplaceLineEndings "\n"
    )
    |> ignore

[<Fact>]
let ``A lambda in a record or tuple literal a function gives back can call the members its field's type requires`` () =
    compileClean (
        """
context T = (post: string -> int)

def make: () -> (ingest: [T] string -> int) = { (ingest = { s -> post(s) + 1 }) }

def both: () -> (([T] string -> int), int) = { ({ s -> post(s) * 2 }, 3) }
"""
            .ReplaceLineEndings "\n"
    )
    |> ignore

[<Fact>]
let ``A lambda given for a record field whose type requires no context can't call a context's members`` () =
    let source =
        """
context T = (post: string -> int)

type Client = (ingest: string -> int)

def make: () -> Client = { Client(ingest = { s -> post(s) + 1 }) }
"""
            .ReplaceLineEndings "\n"
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("'post' is not in scope", message)
    Assert.Equal("post", at)
