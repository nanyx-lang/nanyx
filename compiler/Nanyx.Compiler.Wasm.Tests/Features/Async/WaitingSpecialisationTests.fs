/// A function type that requires `Async` can wait, which its IR type says (`IrFunction`'s `waits`), so a function
/// generic in contexts is specialised apart for contexts that can wait and for those that can't (#1890, see
/// docs/src/content/deep-dives/async.md).
module TranspilerWasmTests.Features.Async.WaitingSpecialisation

open System.Text.RegularExpressions
open Xunit
open TranspilerWasmTestHelpers

/// The names of the functions `wat` defines that start with `prefix`, without their resume entries (see
/// `TaskLowering`).
let private functionsNamed (prefix: string) (wat: string) =
    Regex.Matches(wat, $@"\(func \$({Regex.Escape prefix}\S*)")
    |> Seq.map _.Groups[1].Value
    |> Seq.filter (fun name -> not (name.EndsWith "__resume"))
    |> List.ofSeq

[<Fact>]
let ``A function generic in contexts is specialised apart for a function that waits and one that doesn't`` () =
    let wat =
        transpileWatNotInlined
            """import nanyx/async as (Async)

def twice = { f -> [f(), f()] }

def both: [Async] ([Async] () -> int), (() -> int) -> list(int) = { waiting, pure -> twice(waiting) ++ twice(pure) }
"""
    let specialisations = functionsNamed "twice" wat
    Assert.Equal(2, specialisations.Length)
    Assert.Equal(1, specialisations |> List.filter (fun name -> name.EndsWith "_waits") |> List.length)

[<Fact>]
let ``A function generic in contexts used only with functions that don't wait is specialised as before`` () =
    let wat =
        transpileWatNotInlined
            """def twice = { f -> [f(), f()] }

def both: (() -> int), (() -> int) -> list(int) = { first, second -> twice(first) ++ twice(second) }
"""
    Assert.Equal<string list>([ "twice__int" ], functionsNamed "twice" wat)
