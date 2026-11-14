module TranspilerWasmTests.Features.ControlFlow.BottomType

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a branch of type undefined that is not taken leaves the other branch's value`` () =
    let source =
        normalized """import nanyx/console

def stop: int -> undefined = { x -> stop(x) }

def double = { n -> if n > 0 then n * 2 else stop(n) }

def name = { n -> match n | 1 -> "one" | _ -> stop(n) }

@entry
def main = {
  println("{double(21)} {name(1)}")
}
"""
    runSource source |> assertRan "42 one\n"

[<Fact>]
let ``Run - panic writes its message to stderr and exits with 1, running nothing after it`` () =
    let source =
        normalized """import nanyx/console

def half: int -> int = { n -> if n % 2 == 0 then n / 2 else panic("odd: {n}") }

@entry
def main = {
  println("{half(4)}")
  println("{half(3)}")
  println("never")
}
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("2\n", outcome.StandardOutput)
    Assert.Equal("panic: odd: 3\n", outcome.StandardError)
