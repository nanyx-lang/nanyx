/// A generic definition is specialised at each set of type arguments it is called at, up to a cap; a call past the cap
/// goes through one specialisation more, with its type arguments' values boxed, converting its arguments and its result
/// (#2890, see `CoreIrLowerModule.boxedInstead`).
module TranspilerWasmTests.Features.Functions.RunSpecialisationCap

open Xunit
open TranspilerWasmTestHelpers

/// More types than the cap on specialisations, 64, allows: a record type of its own for each.
let private types = [ 1..70 ]

[<Fact>]
let ``Run - list functions called at more element types than the cap allows`` () =
    let calls =
        types
        |> List.map (fun i -> $"println(firstOr(list.map(list.reverse([(n{i} = 1), (n{i} = {i})]), {{ r -> r.n{i} }})))")
        |> String.concat "\n"
    let source =
        $"""def firstOr: list(int) -> int = {{ xs ->
  match list.head(xs)
    | #some(x) -> x
    | #emptyList -> 0
}}

{calls}
"""
    runSource source |> assertPrinted (types |> List.map (fun i -> $"{i}\n") |> String.concat "")

[<Fact>]
let ``Run - a function that requires a context, called at more types than the cap allows`` () =
    let calls = types |> List.map (fun i -> $"labelled((n{i} = {i}), \"{i}\")") |> String.concat "\n"
    let source =
        $"""def labelled: [Console] a, string -> a = {{ x, label ->
  println(label)
  x
}}

{calls}
"""
    runSource source |> assertPrinted (types |> List.map (fun i -> $"{i}\n") |> String.concat "")
