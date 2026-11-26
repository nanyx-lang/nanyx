/// Chains of pipes, each stage's value inferred once.
module Nanyx.Compiler.Tests.Features.PipeChain

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// `stages` pipes of `stage` after `start`, in a def `x` after `defs`.
let private chain (defs: string) (start: string) (stage: string) (stages: int) =
    let pipes = List.replicate stages $" \\{stage}" |> String.concat ""
    $"{defs}\ndef x = {start}{pipes}\n"

let private incs = chain "def inc: int -> int = { a -> a + 1 }" "1" "inc"

let private maps = chain "def xs = [1, 2, 3]" "xs" "map { a -> a + 1 }"

// A pipe's call took its value, the whole chain to its left, as its first argument and inferred it again, so each
// stage doubled the work of checking those before it: a chain of 14 took 54 s to check (#1851).
[<Fact>]
let ``A chain of 40 pipes checks`` () =
    Assert.Equal("int", Unifier.tyToString (compileClean (incs 40)).Types.["x"])
    Assert.Equal("list(int)", Unifier.tyToString (compileClean (maps 40)).Types.["x"])

[<Fact>]
let ``Checking a chain of pipes solves constraints that grow with its length`` () =
    let solved (source: int -> string) stages = snd (Solving.countingSolvedConstraints (fun () -> compileClean (source stages)))
    // What a chain adds to the def it is in: a stage solves about as many as another.
    let added (source: int -> string) stages = solved source stages - solved source 0
    for source in [ incs; maps ] do
        let atThree, atTwelve = added source 3, added source 12
        Assert.True(atTwelve < 8 * atThree, $"12 stages solved {atTwelve} constraints, {float atTwelve / float atThree:F1} times the {atThree} of 3")
