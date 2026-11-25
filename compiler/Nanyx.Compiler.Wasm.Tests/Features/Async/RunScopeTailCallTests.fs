/// A def that is the scope of its tasks, called last by one, joins its caller's scope (#2562, see `TaskScopes`): the
/// call stays a tail call, so a def that starts a task in each turn and calls itself last, or two that call each other
/// last, is a loop, and the tasks of every turn are waited for when the outermost call returns.
module TranspilerWasmTests.Features.Async.RunScopeTailCall

open System
open Xunit
open TranspilerWasmTestHelpers

/// The quickest of five runs, in milliseconds, of a def that awaits a task in each of its turns and calls itself last
/// (`looped`), at `n` and `4 * n` turns, and of the same loop with the task in a def of its own (`split`), which was a
/// loop before.
let private timings =
    normalized
        """import nanyx/math

def looped: [Async] int, int -> int = { n, acc ->
  if n == 0 then acc else looped(n - 1, acc + await(start! n))
}

def step: [Async] int -> int = { n ->
  await(start! n)
}

def split: [Async] int, int -> int = { n, acc ->
  if n == 0 then acc else split(n - 1, acc + step(n))
}

def timed: [Async, Clock] ([Async] () -> int) -> int = { run ->
  def before = ticks()
  run()
  ticks() - before
}

def quickest: [Async, Clock] ([Async] () -> int), int, int -> int = { run, times, best ->
  if times == 0 then best else quickest(run, times - 1, min(best, timed(run)))
}

@entry
def main = {
  def n = 100_000
  println("{quickest({ looped(n, 0) }, 5, 1_000_000)}")
  println("{quickest({ looped(4 * n, 0) }, 5, 1_000_000)}")
  println("{quickest({ split(n, 0) }, 5, 1_000_000)}")
}
"""

[<Fact>]
let ``A def that awaits a task in each turn and calls itself last is a loop, as quick as one whose task is in a def of its own`` () =
    // It took 70 ms for 4,000 turns, and ran out of stack at 8,000, before (#2562).
    let measure () =
        let outcome = runSource timings
        Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
        match outcome.StandardOutput.Trim().Split('\n') |> Array.map (fun line -> float (line.Trim())) with
        | [| looped; looped4; split |] -> looped, looped4, split
        | lines -> failwith $"three timings, not {lines.Length}"
    // A millisecond is the clock's tick, so each counts at least one.
    let holds (looped, looped4, split) = max looped4 1.0 < 10.0 * max looped 1.0 && max looped 1.0 < 3.0 * max split 1.0
    // When the first run misses, it is run again, and each timing's quickest counts, so that load on the machine can't
    // make the test fail (#2388).
    let first = measure ()
    let looped, looped4, split =
        if holds first then first
        else
            let runs = first :: List.init 3 (fun _ -> measure ())
            runs |> List.map (fun (l, _, _) -> l) |> List.min,
            runs |> List.map (fun (_, l, _) -> l) |> List.min,
            runs |> List.map (fun (_, _, s) -> s) |> List.min
    Assert.True(
        holds (looped, looped4, split),
        $"{looped} ms for 100,000 turns, {looped4} ms for 400,000, and {split} ms for 100,000 with the task in a def of its own")

[<Fact>]
let ``Two defs that start tasks and call each other last are a loop`` () =
    normalized
        """def ping: [Async] int, int -> int = { n, acc ->
  if n == 0 then acc else pong(n - 1, acc + await(start! n))
}

def pong: [Async] int, int -> int = { n, acc ->
  if n == 0 then acc else ping(n - 1, acc + await(start! 2 * n))
}

@entry
def main = {
  println(ping(100_000, 0))
}
"""
    |> runSource
    |> assertRan "7500050000\n"

/// A def that starts a task nobody awaits in each turn, with `task` as its work, and calls itself last.
let private countdown (task: string) =
    normalized
        $"""def countdown: [Async, Console] int -> () = {{ n ->
  start! {task}
  if n == 1 then println("last turn") else countdown(n - 1)
}}

@entry
def main = {{
  countdown(3)
  println("returned")
}}
"""

[<Fact>]
let ``A task nobody awaits, started in each turn of a loop, runs before the loop's outermost call returns`` () =
    // Each turn's task belongs to the outermost call's scope, so they all run once the last turn is done, in the order
    // they were started.
    countdown """println("task {n}")"""
    |> runSource
    |> assertRan "last turn\ntask 3\ntask 2\ntask 1\nreturned\n"

[<Fact>]
let ``A task that crashes, started in a turn of a loop, ends the program`` () =
    let outcome = runSource (countdown """(if n == 2 then crash("the task of turn 2 failed") else println("task {n}"))""")
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("the task of turn 2 failed", outcome.StandardError)
    Assert.DoesNotContain("returned", outcome.StandardOutput)
