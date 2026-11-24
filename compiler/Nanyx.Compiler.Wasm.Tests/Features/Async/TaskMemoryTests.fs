/// What a task holds once it has finished (#2666, see `WasmScheduler`): its scope takes it out and frees its block, and
/// what it gave back stays in its cell, which its `Task` holds, for as long as something can await it. So a scope that
/// starts a task in each turn of a loop, and awaits it, holds the same memory however many turns it runs, and a task
/// can still be awaited more than once.
module TranspilerWasmTests.Features.Async.TaskMemory

open Xunit
open TranspilerWasmTestHelpers

/// An entry point that starts a task in each of `turns` turns of a loop, and awaits it.
let private loop (turns: int) =
    normalized
        $"""@entry
def main = {{
  for 1..{turns} | i -> ignore(await(start! i))
  println("done")
}}
"""

/// The peak live bytes of `source`'s run, which must print `done`.
let private peakOf (source: string) =
    let outcome = runWithMemoryReport source
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal("done\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    reportedMemory "peak live bytes" outcome

[<Fact>]
let ``A loop that starts a task in each turn and awaits it holds the same memory for 400,000 turns as for 100,000`` () =
    // Each finished task was kept until its scope ended: 6,400,120 bytes at 100,000 turns, and 25,600,120 at 400,000.
    let atFewer = peakOf (loop 100_000)
    let atMore = peakOf (loop 400_000)
    Assert.True(atMore - atFewer < 256L, $"{atFewer} bytes at their peak for 100,000 turns, and {atMore} for 400,000")
    Assert.True(atFewer < 4096L, $"{atFewer} bytes at their peak for 100,000 turns")

[<Fact>]
let ``A task can be awaited more than once, after it has finished, and each await gives what it gave back`` () =
    normalized
        """def greeting: [Async] string -> string = { name ->
  def task = start! "hello, " ++ name
  def first = await(task)
  def second = await(task)
  "{first} / {second}"
}

@entry
def main = {
  println(greeting("world"))
}
"""
    |> runSource
    |> assertRan "hello, world / hello, world\n"

[<Fact>]
let ``A task awaited after the others have run, which it finished before, gives what it gave back`` () =
    // The first task has finished, and its block is gone, by the time the second is awaited: its cell keeps its value.
    normalized
        """def both: [Async] int -> int = { n ->
  def early = start! n * 2
  def late = start! n + 1
  def l = await(late)
  await(early) + l + await(early)
}

@entry
def main = {
  println(both(10))
}
"""
    |> runSource
    |> assertRan "51\n"
