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
    """::install nanyx.fs

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

[<Fact>]
let ``A def whose parameter or local def is named start starts no task, so it is no scope`` () =
    let source =
        """def from = { start: int, n: int -> start + n }

def later = { _ ->
  def start = 2
  start * 3
}

println(from(1, 2))
println(later())
"""
    Assert.DoesNotContain("$__nanyx_scope_enter", transpileWat source)
    runSource source |> assertRan "3\n6\n"

[<Fact>]
let ``An await in the right operand of and and or runs only when the left doesn't decide`` () =
    let source =
        """def check: [Async] int -> bool = { n ->
  def t = start! n > 2
  n > 0 and await(t) or not(n > 0) and await(start! true)
}

println(check(1))
println(check(5))
println(check(-1))"""
    assertPrints "false\ntrue\ntrue\n" "false\ntrue\ntrue\n" source

[<Fact>]
let ``A local def or a parameter named start wins over Async's`` () =
    let source =
        """def local = { _ ->
  def start = { n: int -> n + 1 }
  start(41)
}

def given = { start: string -> "given {start}" }

def both = { _ ->
  def task = start! 1
  await(task)
}

println(local())
println(given("x"))
println(both())"""
    assertPrints "42\ngiven x\n1\n" "42\ngiven x\n1\n" source

/// A def that calls one with a scope of its own is a state machine, which keeps the temporaries a call that can wait is
/// given in locals. `between(text, copied, text.length)` counts `text` for its first argument before `text.length`
/// gives up the reference the def holds: the kept temporary is computed after that count, as it would be where it is
/// used (#2490).
[<Fact>]
let ``A call that can wait, given a value and then a field of it, keeps the value alive`` () =
    assertPrints
        "b\nb\n"
        "b\nb\n"
        """type Text = (subject: string, offsets: array(int), length: int)

def byteAt: Text, int -> int = { text, i ->
  def offsets = text.offsets

  match offsets(i)
    | #some(b) -> b
    | #notFound -> 0
}

def between: Text, int, int -> string = { text, start, end ->
  concurrent({
    def from = byteAt(text, start)
    string.substring(text.subject, start = from, length = byteAt(text, end) - from)
  })
}

def pieces: Text -> [] seq(int) = { text ->
  seq.unfold(0) { i -> if i + 2 > string.length(text.subject) then #done else #some(i, i + 2) }
}

def rejoin: string -> string = { s ->
  def text = Text(subject = s, offsets = array.init(3, { i -> i }), length = 2)
  def folded = seq.fold(pieces(text), (0, []), { (copied, done), at -> (at + 1, [between(text, copied, at), ...done]) })
  def (copied, done) = folded
  string.concat(list.reverse([between(text, copied, text.length), ...done]))
}

println(rejoin("ab"))
println(rejoin("ab"))"""

[<Fact>]
let ``A task that calls itself in tail position after a lambda it made waits frees the lambda once`` () =
    // The lambda, which holds what it captures on the heap, is held only by the task, so the count the tail call takes of
    // it has to come before the call that waits, which drops the reference that call read it with. A debug build's
    // return probe stands between the two (#2591).
    let source =
        """def waitThen: [Async, Clock] int -> int = { x ->
  sleep(1ms)
  x + 1
}

def loopWith: [c] ([c] int -> int), int, int -> int = { f, n, acc ->
  if n == 0 then acc else loopWith(f, n - 1, f(acc))
}

def both = { (step: int) ->
  def once = start! loopWith({ x -> waitThen(x) + step }, 1, 100)
  def thrice = start! loopWith({ x -> waitThen(x) + step }, 3, 100)
  "{await(once)} {await(thrice)}"
}

println(both(10))"""
    assertPrints "111 133\n" "111 133\n" source
    // With the probes, which `nanyx run` leaves out (#2225), straight through and with every wait stopping, with
    // nothing left allocated.
    for yieldAtEveryWait in [ false; true ] do
        let options = { probedOptions with CheckMemory = true; YieldAtEveryWait = yieldAtEveryWait }
        let wat = Nanyx.LargeStack.run (fun () -> transpileWatWith options (withFinalNewline source))
        match runCheckedHost [ "main" ] wat with
        | Ok(printed, live) -> Assert.Equal(("111 133\n", 0), (printed, live))
        | Error error -> failwith $"%A{error}"
