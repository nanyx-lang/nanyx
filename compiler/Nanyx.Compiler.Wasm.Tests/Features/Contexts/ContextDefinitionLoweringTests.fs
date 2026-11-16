/// A definition that declares contexts is not exported, and is lowered in its own form, which takes the members, only
/// where a call asks for it: a function value, or a caller that does not know the instance (#1269, see
/// `docs/src/content/work-in-progress/context-calls.md`).
module TranspilerWasmTests.Features.Contexts.ContextDefinitionLowering

open Xunit
open TranspilerWasmTestHelpers

let private greet =
    """def greet: [Console] int -> () = { n -> println("hello {n}") }
"""

[<Fact>]
let ``A definition that declares contexts is not exported`` () =
    let source =
        greet
        + """
greet(1)
greet(2)
"""
    let wat = transpileWatNotInlined (source.ReplaceLineEndings "\n")
    Assert.DoesNotContain("(export \"greet\"", wat)
    runSource (source.ReplaceLineEndings "\n") |> assertRan "hello 1\nhello 2\n"

[<Fact>]
let ``A definition that declares contexts and that every caller knows the instance of is emitted once`` () =
    let source =
        greet
        + """
greet(1)
greet(2)
"""
    let wat = transpileWatNotInlined (source.ReplaceLineEndings "\n")
    Assert.Equal(1, countOccurrences "(func $greet" wat)

[<Fact>]
let ``Run - a definition that declares contexts passed as a value is lowered in its own form, and memory balances`` () =
    let source =
        greet
        + """
def twice: [Console] ([Console] int -> ()) -> () = { f ->
  f(1)
  f(2)
}

twice(greet)
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "hello 1\nhello 2\n"
