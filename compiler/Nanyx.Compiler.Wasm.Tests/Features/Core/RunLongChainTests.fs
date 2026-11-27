/// Programs whose expressions nest thousands deep, which the backend recurses into once per level (#2299). A Debug
/// build ran out of a 1 MB stack at 50 additions and 30 branches.
module TranspilerWasmTests.Features.Core.RunLongChains

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A chain of 2,000 additions compiles and runs`` () =
    let source = "def x = 1" + String.replicate 1_999 " + 1" + "\nprintln(x)\n"
    runSource source |> assertRan "2000\n"

[<Fact>]
let ``An else-if chain of 2,000 branches compiles and runs`` () =
    let branches = [ for i in 1 .. 2_000 -> $"if n == {i} then {i * 2}" ] |> String.concat " else "
    let source = $"def f = {{ n: int -> {branches} else 0 }}\nprintln(f(1999))\nprintln(f(5000))\n"
    runSource source |> assertRan "3998\n0\n"

[<Fact>]
let ``An and chain of 200 is tests compiles and runs`` () =
    let tests = [ for i in 1 .. 200 -> $"m is #some(v{i})" ] |> String.concat " and "
    let source = $"def f = {{ m: #some(int) | #none -> {tests} and v200 == 7 }}\nprintln(f(#some(7)))\nprintln(f(#none))\n"
    runSource source |> assertRan "true\nfalse\n"

/// What the program `source` writes, run as `nanyx run` runs it.
let private ran (source: string) =
    let outcome = runSource source
    outcome |> assertRan outcome.StandardOutput
    outcome.StandardOutput

/// What the program `source` writes, compiled with debug probes, as the debugger will compile it (see `runProbed`).
let private ranProbed (source: string) =
    match runProbed source with
    | Ok printed -> printed
    | Error error -> failwith $"%A{error}"

/// Asserts that the program `source n`, run by `run`, writes `output n`, and that it compiles and runs in time that grows
/// with `n`, not with its square: at `n` it takes less than ten times what it takes at a quarter of `n`. A linear compile
/// takes about 5 times as long, and a quadratic one 16 or more (an else-if that checked in quadratic time took 20 times as
/// long). When the first runs miss, both sizes run again and each one's quicker run counts, so that load on the machine,
/// which slows one run and not the other, can't make the test fail (#2388).
let private runsInLinearTime (run: string -> string) (n: int) (source: int -> string) (output: int -> string) =
    let time size =
        let mutable printed = ""
        let milliseconds = Nanyx.TestTiming.millisecondsOf (fun () -> printed <- run (source size))
        Assert.Equal(output size, printed)
        milliseconds
    let quarter = n / 4
    let isLinear (atQuarter: float) (atN: float) = atN < 10.0 * atQuarter
    let atQuarter, atN = time quarter, time n
    if not (isLinear atQuarter atN) then
        let atQuarter = min atQuarter (time quarter)
        let atN = min atN (time n)
        Assert.True(isLinear atQuarter atN, $"{n:N0} took {atN:F0} ms, {atN / atQuarter:F1} times the {atQuarter:F0} ms of {quarter:N0}")

// The backend writes each level of a chain without copying the code of the levels inside it, and its passes look at
// each level once, so a chain compiles in time that grows with its length (#2351). 10,000 additions took 30 s to
// compile in a Release build when each level copied the code inside it.
[<Fact; Trait("Category", "Timing")>]
let ``A chain of 10,000 additions compiles and runs in time that grows with its length`` () =
    runsInLinearTime ran 10_000 (fun n -> "def x = 1" + String.replicate (n - 1) " + 1" + "\nprintln(x)\n") (fun n -> $"{n}\n")

// Each branch of an else-if is joined to the next, which makes a chain of 10,000 type variables, each solved to the one
// before it. The type checker follows such a chain once, not once for each variable on it, so a long else-if checks in
// time that grows with its length (#2362). 10,000 branches took 2 minutes in a Release build when it followed it each time.
[<Fact; Trait("Category", "Timing")>]
let ``An else-if chain of 10,000 branches compiles and runs in time that grows with its length`` () =
    let source n =
        let branches = [ for i in 1..n -> $"if n == {i} then {i * 2}" ] |> String.concat " else "
        $"def f = {{ n: int -> {branches} else 0 }}\nprintln(f({n - 1}))\nprintln(f(20000))\n"
    runsInLinearTime ran 10_000 source (fun n -> $"{(n - 1) * 2}\n0\n")

// A debug build's probe before each statement writes the parameters and the locals bound last, at most
// `CoreIrProbes.probeLimit` of them, and what a probe may write is worked out once for its chain, so a function of
// thousands of locals compiles in time that grows with their number. Each probe wrote every local in scope, and 8,000
// locals ran the backend out of memory (#2488). `nanyx run` compiles without probes (#2225), so this compiles with them.
[<Fact; Trait("Category", "Timing")>]
let ``A function of 8,000 locals compiles and runs in time that grows with their number`` () =
    let source n =
        let defs = [ for i in 1..n -> $"  def x{i} = x0 + 1" ] |> String.concat "\n"
        $"def run: int -> int = {{ x0 ->\n{defs}\n  x{n}\n}}\n\nprintln(run(0))\n"
    runsInLinearTime ranProbed 8_000 source (fun _ -> "1\n")

// A `memory` block of thousands of stores is one chain of that many links, which the inliner simplifies in each function
// it is inlined into. A body is simplified until a pass changes nothing, and gone over no more once nothing is inlined
// into it, so the chain compiles in time that grows with its length (#2408, #2579).
[<Fact; Trait("Category", "Timing")>]
let ``A memory block of 4,000 stores compiles and runs in time that grows with their number`` () =
    let source n =
        let stores = String.replicate n "    x := x + 1\n"
        $"def run: int -> int = {{ start ->\n  memory {{\n    mut x = start\n{stores}    x\n  }}\n}}\n\nprintln(run(0))\n"
    runsInLinearTime ran 4_000 source (fun n -> $"{n}\n")
