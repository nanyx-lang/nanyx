module TranspilerWasmTests.Features.Commands.Check

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// What `nanyx check` printed for a script holding `source`.
let private check (source: string) =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, source)
    nanyxWithInput "" [ "check"; path ]

[<Fact>]
let ``Check - a script prints the types of its own defs, in Nanyx syntax and nothing from the prelude`` () =
    let outcome = check """def greet = { name -> "Hello " ++ name }"""
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
        check
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
