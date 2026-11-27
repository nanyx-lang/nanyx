/// A function between a wait and its task's start keeps its frame while it is resumed and stops again where it was, and
/// a call of a state machine named in the code is resumed with a direct call (#2561, see "How tasks are compiled" in
/// docs/src/content/deep-dives/async.md): a wait many calls deep allocates no more than one at the top, but for the frames
/// of the first wait.
module TranspilerWasmTests.Features.Async.WaitDepth

open Xunit
open TranspilerWasmTestHelpers

/// A program that starts and awaits `waits` tasks, one at a time, from a loop `depth` calls deep.
let private waitingAt (depth: int) (waits: int) =
    $"""def one: [Async] int -> int = {{ x ->
  def t = start! x * x
  await(t)
}}

def loop: [Async] int, int -> int = {{ n, acc ->
  if n == 0 then acc else loop(n - 1, acc + one(n))
}}

def deep: [Async] int, int -> int = {{ d, n ->
  if d == 0 then loop(n, 0) else 1 + deep(d - 1, n)
}}

@entry
def main = {{
  println(deep({depth}, {waits}))
}}
"""

/// The blocks `nanyx run --memory`, with `options`, says `source` allocated, once it printed `expected`.
let private allocations (options: string list) (expected: string) (source: string) =
    let outcome = runWithMemoryReportUsing options source
    Assert.Equal(0, outcome.ExitCode)
    Assert.StartsWith(expected, outcome.StandardOutput.ReplaceLineEndings "\n")
    reportedMemory "allocations" outcome

[<Fact>]
let ``A wait 50 calls deep allocates no more than one at the top, but for the frames of the first wait`` () =
    for options in [ []; [ "--yield-at-every-wait" ] ] do
        let atTop = allocations options "2686700\n" (waitingAt 0 200)
        let deep = allocations options "2686750\n" (waitingAt 50 200)
        // Each of the 50 calls allocated a frame at each of the 200 waits: 10,000 more blocks.
        Assert.True(deep - atTop <= 60, $"{options}: 200 waits 50 calls deep made {deep} blocks, and at the top {atTop}")
