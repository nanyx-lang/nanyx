/// Tasks: `start!` starts one, `await` waits for it, and each def is the scope of the tasks it starts (#1890, see
/// "Structured concurrency" in docs/src/content/work-in-progress/concurrency-and-crash-boundaries.md). The scheduler runs
/// an instance's tasks one at a time, first in first out, each from the next time the code that started it waits
/// (see `WasmScheduler`). Nothing waits on its own yet, so each program is also run with every wait stopping once
/// (`--yield-at-every-wait`), which is where tasks take turns.
module TranspilerWasmTests.Features.Async.RunTask

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// What `nanyx run --check-memory`, with `options`, gives for the script `source`.
let private run (options: string list) (source: string) =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), withFinalNewline source)
    let outcome = nanyxIn directory.Path ([ "run"; "--check-memory" ] @ options @ [ "main.nyx" ])
    { outcome with
        StandardOutput = outcome.StandardOutput.ReplaceLineEndings "\n"
        StandardError = outcome.StandardError.ReplaceLineEndings "\n" }

/// Asserts that `source` prints `straight` run straight through, and `stopping` with every wait stopping once.
let private assertPrints (straight: string) (stopping: string) (source: string) =
    for options, expected in [ [], straight; [ "--yield-at-every-wait" ], stopping; [ "--yield-at-every-wait"; "--no-inline" ], stopping ] do
        let outcome = run options source
        Assert.Equal("", outcome.StandardError)
        Assert.Equal(normalized expected, outcome.StandardOutput)
        Assert.Equal(0, outcome.ExitCode)

/// A function that waits, on the file system, so that with every wait stopping, a task takes turns there.
let private waiting =
    """::dependency nanyx.fs

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

def pause: [Async, FileSystem] () -> () = { _ -> file.exists(path.of("nothing.txt")) \ignore }
"""

[<Fact>]
let ``Tasks run at once, and await gives what each gave back`` () =
    let source =
        """def both = { _ ->
  def a = start! 1 + 2
  def b = start! "x"
  "{await(a)} {await(b)}"
}

println(both())"""
    assertPrints "3 x\n" "3 x\n" source

[<Fact>]
let ``A def waits for the tasks it started, awaited or not`` () =
    let source =
        """def fireAndForget = { _ ->
  start! println("the task runs")
  println("the def returns")
  42
}

println(fireAndForget())
println("after")"""
    assertPrints "the def returns\nthe task runs\n42\nafter\n" "the def returns\nthe task runs\n42\nafter\n" source

[<Fact>]
let ``start! in a map lambda starts every task before the first await`` () =
    let source =
        """def labelled: [Console] int -> string = { n ->
  println("task {n}")
  "#{n}"
}

def all = { xs: list(int) ->
  def tasks = xs \list.map { x -> start! labelled(x) }
  println("started")
  tasks \list.map(await)
}

println(all([1, 2, 3]))"""
    assertPrints "started\ntask 1\ntask 2\ntask 3\n[#1, #2, #3]\n" "started\ntask 1\ntask 2\ntask 3\n[#1, #2, #3]\n" source

[<Fact>]
let ``A pipe after start! runs in the task`` () =
    let source =
        """def report: [Console] int -> int = { n ->
  println("in the task: {n}")
  n
}

def run = { _ ->
  def t = start! 41 \report
  println("started")
  await(t) + 1
}

println(run())"""
    assertPrints "started\nin the task: 41\n42\n" "started\nin the task: 41\n42\n" source

[<Fact>]
let ``Tasks take turns where they wait, each in its own memory block`` () =
    let source =
        waiting
        + """
def counter: [Async, Console, FileSystem] string -> int = { name ->
  memory {
    mut count = 0
    count := count + 1
    println("{name} {count}")
    pause()
    count := count + 1
    println("{name} {count}")
    count
  }
}

def both = { _ ->
  def a = start! counter("a")
  def b = start! counter("b")
  await(a) + await(b)
}

println(both())"""
    assertPrints "a 1\na 2\nb 1\nb 2\n4\n" "a 1\nb 1\na 2\nb 2\n4\n" source

[<Fact>]
let ``A task that starts tasks finishes once they have`` () =
    let source =
        waiting
        + """
def slow: [Async, Console, FileSystem] () -> int = { _ ->
  pause()
  println("inner")
  20
}

def nested = { _ ->
  def work = { _ ->
    def inner = start! slow()
    start! println("forgotten inner")
    await(inner) + 1
  }
  def outer = start! work()
  println("started")
  await(outer) * 2
}

println(nested())"""
    assertPrints "started\ninner\nforgotten inner\n42\n" "started\nforgotten inner\ninner\n42\n" source

[<Fact>]
let ``A crash in a task ends the program`` () =
    let source =
        """def risky = { _ ->
  def a = start! crash("boom")
  def b = start! println("the sibling")
  await(a)
}

risky()
println("after")"""
    for options in [ []; [ "--yield-at-every-wait" ] ] do
        let outcome = run options source
        Assert.Equal(1, outcome.ExitCode)
        Assert.Contains("boom", outcome.StandardError)
        Assert.Equal("", outcome.StandardOutput)
