module TranspilerWasmTests.Features.Commands.Check

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Check - a script prints the types of its own defs, in Nanyx syntax and nothing from the prelude`` () =
    let outcome = checkScript """def greet = { name -> "Hello " ++ name }"""
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(
        """
Phase: Typed
No diagnostics.
Types:
greet : string -> string
""" |> normalizeOutput |> _.TrimStart(),
        normalizeOutput outcome.StandardOutput)

[<Fact>]
let ``Check - a script's defs are in source order, written or inferred, with the contexts they require`` () =
    let outcome =
        checkScript
            """
def twice = { x -> "{x}{x}" }
def answer: int = 42
def pair = { a, b -> (a, b) }
"""
    Assert.Equal(0, outcome.ExitCode)
    Assert.EndsWith(
        """
Types:
twice : [ToString(a)] a -> string
answer : int
pair : a, b -> (a, b)
""" |> normalizeOutput,
        normalizeOutput outcome.StandardOutput)
