/// A program that recurses too deeply in a call that is not in tail position runs out of stack, and `nanyx run` says so,
/// naming the function, rather than printing the engine's backtrace (#1478, see
/// docs/src/content/deep-dives/tail-calls.md).
module TranspilerWasmTests.Features.TailCalls.StackOverflow

open Xunit
open TranspilerWasmTestHelpers

let private message (name: string) =
    $"Runtime error: the program ran out of stack in '{name}': a call that isn't a tail call keeps its caller's stack until it returns, so a function that calls itself that way uses stack for every call (see https://nanyx.dev/docs/tour/functions#tail-recursion)\n"

[<Fact>]
let ``A function that recurses too deeply runs out of stack, named, without a backtrace`` () =
    let source =
        normalized """def deep = { n -> if n == 0 then 0 else 1 + deep(n - 1) }

println("{deep(100000)}")
"""
    let outcome = runSource source
    Assert.Equal(message "deep", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal("", outcome.StandardOutput)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A local rec function that runs out of stack is named as it is written`` () =
    let source =
        normalized """@entry
def main = {
  rec go = { n -> if n == 0 then 0 else 1 + go(n - 1) }
  println(go(1000000))
}
"""
    let outcome = runSource source
    Assert.Equal(message "go", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A specialised function that runs out of stack is named without its type arguments`` () =
    let source =
        normalized """def count: list(a) -> int = {
  | [] -> 0
  | [_, ...rest] -> 1 + count(rest)
}

def build: int, list(int) -> list(int) = { n, acc -> if n == 0 then acc else build(n - 1, [n, ...acc]) }

println(count(build(1000000, [])))
"""
    let outcome = runSourceWith false "" source
    Assert.Equal(message "count", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A program run from a thread with a small stack still runs out of its own stack, not the thread's`` () =
    // A 1 MB thread, as Windows' main thread is. The program runs on a thread of its own with room past its stack
    // limit (see `ProgramStack`), so it traps there whatever the caller's stack. Without it, Windows stopped the whole
    // process when the program reached the end of the caller's stack (#2708).
    let source =
        normalized """def deep = { n -> if n == 0 then 0 else 1 + deep(n - 1) }

println("{deep(100000)}")
"""
    let outcome = Nanyx.LargeStack.runWithStack (1024 * 1024) (fun () -> runSource source)
    Assert.Equal(message "deep", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A call last in a memory block is no tail call, as the block frees its memory after it`` () =
    // A construct that runs code when its scope ends takes a call last in its body out of tail position (#2725).
    let source =
        normalized """def count: int, int -> int = { n, acc ->
  if n == 0 then acc else memory { count(n - 1, acc + 1) }
}

println(count(1000000, 0))
"""
    let outcome = runSource source
    Assert.Equal(message "count", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A call last in a def that starts tasks, of a def that starts none, is no tail call, as the def waits for its tasks`` () =
    let source =
        normalized """def work: () -> int = { () -> 1 }

def a: [Async] int -> int = { n ->
  if n == 0 then 0
  else
    def t = start! work()
    def one = await(t)
    b(n - one)
}

def b: [Async] int -> int = { n -> a(n) }

@entry
def main: [Async, Console] () -> () = { () -> println("{a(200000)}") }
"""
    let outcome = runSource source
    Assert.Equal(message "a", outcome.StandardError.ReplaceLineEndings "\n")
    Assert.Equal(1, outcome.ExitCode)
