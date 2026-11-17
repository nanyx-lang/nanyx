/// A program that recurses too deeply in a call that is not in tail position runs out of stack, and `nanyx run` says so,
/// naming the function, rather than printing the engine's backtrace (#1478, see
/// docs/src/content/compiler-internals/tail-calls.md).
module TranspilerWasmTests.Features.TailCalls.StackOverflow

open Xunit
open TranspilerWasmTestHelpers

let private message (name: string) =
    $"Runtime error: the program ran out of stack in '{name}': a recursive call that isn't in tail position uses stack for every call (see https://nanyx.dev/docs/tour/functions#tail-recursion)\n"

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
