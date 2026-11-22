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

// The backend writes each level of a chain without copying the code of the levels inside it, and its passes look at
// each level once, so a chain compiles in time that grows with its length (#2351). 10,000 additions took 30 s to
// compile in a Release build when each level copied the code inside it.
[<Fact>]
let ``A chain of 10,000 additions compiles and runs in time that grows with its length`` () =
    let source = "def x = 1" + String.replicate 9_999 " + 1" + "\nprintln(x)\n"
    let clock = System.Diagnostics.Stopwatch.StartNew()
    let outcome = runSource source
    clock.Stop()
    outcome |> assertRan "10000\n"
    Assert.True(clock.ElapsedMilliseconds < 20_000L, $"10,000 additions took {clock.ElapsedMilliseconds} ms")
