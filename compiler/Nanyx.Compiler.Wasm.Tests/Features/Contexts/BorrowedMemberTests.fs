/// A function called through a function value borrows the context members it is called with, as it borrows its
/// environment (#2559, see "Closures have two entries" in docs/src/content/deep-dives/memory-management.md): the caller
/// holds them throughout, so a call counts none of them up or down, and a function that keeps one takes a reference of
/// its own there.
module TranspilerWasmTests.Features.Contexts.BorrowedMember

open System.Text.RegularExpressions
open Xunit
open TranspilerWasmTestHelpers

/// How many times `code` counts a function value up or down.
let private countings (code: string) = Regex.Matches(code, @"call \$__nanyx_(dup|drop)_fn").Count

/// The text of the function in `wat` whose name starts with `prefix`, as a specialisation's does.
let private functionStarting (prefix: string) (wat: string) =
    let found = Regex.Match(wat, $@"\(func \${Regex.Escape prefix}\S* ")
    Assert.True(found.Success, $"no function {prefix}")
    wat.Substring(found.Index, wat.IndexOf("\n  )", found.Index) - found.Index)

/// A loop that calls a function value that requires `Clock` 1,000 times, and the lambda it calls, which reads `now` on
/// one branch only.
let private clockLoop =
    normalized
        """def loopWith: [c] ([c] int -> int), int, int -> int = { f, n, acc ->
  if n == 0 then acc else loopWith(f, n - 1, f(acc))
}

@entry
def main = {
  println(loopWith({ x -> if x < 0 then now() else x + 1 }, 1000, 0))
}
"""

[<Fact>]
let ``A call of a function value that requires a context counts none of its members`` () =
    let wat = transpileWat clockLoop
    Assert.Equal(0, countings (functionStarting "loopWith" wat))
    Assert.Equal(0, countings (functionStarting "__lambda" wat))
    runSource clockLoop |> assertRan "1000\n"

[<Fact>]
let ``list.map and list.fold with a lambda that requires Clock count none of its members for each element`` () =
    let source =
        normalized
            """def nums: int, list(int) -> list(int) = { n, acc -> if n == 0 then acc else nums(n - 1, [n, ...acc]) }

@entry
def main = {
  def doubled = nums(1000, []) \list.map { x -> if x < 0 then now() else x * 2 }
  println(doubled \list.fold(0, { acc, x -> if x < 0 then now() else acc + x }))
}
"""
    let wat = transpileWat source
    Assert.Equal(0, countings (functionStarting "prelude_mapReversed" wat))
    Assert.Equal(0, countings (functionStarting "list_fold" wat))
    runSource source |> assertRan "1001000\n"

[<Fact>]
let ``A lambda that keeps a context member takes a reference of its own to it`` () =
    // `adder` makes a closure that captures `now`, which outlives the call that made it.
    normalized
        """def adder: [Clock] int -> ([Clock] int -> int) = { x -> { y -> if y < 0 then now() + x + y else x + y } }

def apply: [c] ([c] int -> ([c] int -> int)), int, int -> int = { make, x, y ->
  def made = make(x)
  made(y)
}

@entry
def main = {
  def add = adder(5)
  println(add(10))
  println(apply(adder, 1, 2))
  println(apply({ x -> { y -> if y < 0 then now() else x * y } }, 3, 4))
}
"""
    |> runSource
    |> assertRan "15\n3\n12\n"

/// `withMember(n)` calls a function value that requires a context of the program's own `n` times, and `plain(n)` one
/// that requires nothing.
let private ownContext =
    normalized
        """context Scale = (scale: int -> int)

def loopWith: [c] ([c] int -> int), int, int -> int = { f, n, acc ->
  if n == 0 then acc else loopWith(f, n - 1, f(acc))
}

def withMember: int -> int = { n ->
  use Scale(scale = { x -> x }) in loopWith({ x -> if x < 0 then scale(x) else x + 1 }, n, 0)
}

def plain: int -> int = { n -> loopWith({ x -> x + 1 }, n, 0) }
"""

[<Fact>]
let ``A call of a function value that requires a context costs about what one that requires nothing does`` () =
    use program = instantiateWasm ownContext []
    let time (name: string) =
        Assert.Equal(1000, program.Call(name, 1000))
        let clock = System.Diagnostics.Stopwatch.StartNew()
        program.Call(name, 2_000_000) |> ignore
        clock.Elapsed.TotalMilliseconds
    // Counting the member up and down at each call took several times as long as the call.
    let holds (withMember: float) (plain: float) = withMember / plain < 2.0
    let withMember, plain = time "withMember", time "plain"
    // When the first runs miss, each is run again in turn, and each one's quickest run counts, so that load on the
    // machine, which slows one run and not the other, can't make the test fail (#2388).
    let withMember, plain =
        if holds withMember plain then withMember, plain
        else
            let again = List.init 5 (fun _ -> time "withMember", time "plain")
            min withMember (again |> List.map fst |> List.min), min plain (again |> List.map snd |> List.min)
    Assert.True(holds withMember plain, $"2,000,000 calls took {withMember:F1} ms with a member, and {plain:F1} ms without")
    Assert.Equal(0, program.LiveBlocks)
