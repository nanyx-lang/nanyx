/// A lambda in a function specialised on the context members known where it is called is specialised on the members it
/// captures in turn (#2311, see "Specialising on known members" in docs/src/content/deep-dives/context-calls.md): a call
/// of one is a direct call, and one the lambda captures only for its calls is not captured, so a lambda that captured
/// nothing else is a static closure.
module TranspilerWasmTests.Features.Contexts.LambdaKnownMember

open System.Text.RegularExpressions
open Xunit
open TranspilerWasmTestHelpers

/// `summed(n)` sums `passes` times a list of `n` ints it makes once, with `list.sum`, which folds with a lambda that
/// adds through `Add(a)`; `folded(n)` with a lambda written at `int`; `listed(n)` only makes the list.
let private summing =
    normalized
        """def countUp: int, list(int) -> list(int) = { n, acc ->
  if n == 0 then acc else countUp(n - 1, [n * 7919 % 1000003, ...acc])
}

def sumPasses: list(int), int, int -> int = { xs, n, acc ->
  if n == 0 then acc else sumPasses(xs, n - 1, acc + list.sum(xs))
}

def foldPasses: list(int), int, int -> int = { xs, n, acc ->
  if n == 0 then acc else foldPasses(xs, n - 1, acc + list.fold(xs, 0, { total, x -> total + x }))
}

def summed: int -> int = { n -> sumPasses(countUp(n, []), 20, 0) }

def folded: int -> int = { n -> foldPasses(countUp(n, []), 20, 0) }

def listed: int -> int = { n -> list.length(countUp(n, [])) }
"""

[<Fact>]
let ``list.sum of ints folds with a lambda that adds directly`` () =
    let wat = transpileWat summing
    let lambdas = Regex.Matches(wat, @"\(func \$__lambda\S*__known [\s\S]*?\n  \)") |> Seq.map _.Value |> List.ofSeq
    Assert.NotEmpty(lambdas)
    for lambda in lambdas do
        Assert.DoesNotContain("call_indirect", lambda)

[<Fact>]
let ``list.sum of ints allocates nothing`` () =
    use program = instantiateWasm summing []
    let liveBefore = program.LiveBlocks
    let forSummed, _ = allocationsOf program "summed" 1000
    let forListed, _ = allocationsOf program "listed" 1000
    // The lambda captured the member it adds through, so each of the 20 calls made its closure.
    Assert.Equal(forListed, forSummed)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``list.sum of ints takes about as long as the fold it wraps written at int`` () =
    use program = instantiateWasm summing []
    let time (name: string) =
        program.Call(name, 1000) |> ignore
        let clock = System.Diagnostics.Stopwatch.StartNew()
        program.Call(name, 100_000) |> ignore
        clock.Elapsed.TotalMilliseconds
    // Each element's addition through the member's closure took nearly twice as long.
    let holds (summed: float) (folded: float) = summed / folded < 1.4
    let summed, folded = time "summed", time "folded"
    // When the first runs miss, each is run again in turn, and each one's quickest run counts, so that load on the
    // machine, which slows one run and not the other, can't make the test fail (#2388).
    let summed, folded =
        if holds summed folded then summed, folded
        else
            let again = List.init 5 (fun _ -> time "summed", time "folded")
            min summed (again |> List.map fst |> List.min), min folded (again |> List.map snd |> List.min)
    Assert.True(holds summed folded, $"list.sum took {summed:F1} ms, and the fold {folded:F1} ms")

[<Fact>]
let ``seq.range of ints steps with direct calls of succ and compare`` () =
    let source = normalized "def ranged: int -> int = { n -> seq.fold(seq.range(1, n), 0, { acc, x -> acc + x }) }\n"
    let wat = transpileWat source
    let step = Regex.Match(wat, @"\(func \$__lambda\S*__known [\s\S]*?\n  \)")
    Assert.True(step.Success, "no specialised step")
    Assert.DoesNotContain("call_indirect", step.Value)
    use program = instantiateWasm source []
    Assert.Equal(5050, program.Call("ranged", 100))
