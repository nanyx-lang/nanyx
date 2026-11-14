module TranspilerWasmTests.Features.TailCalls.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

// Tail calls use no stack (docs/src/content/work-in-progress/tail-calls.md). Each of these recurses a million calls deep, far past
// what the stack holds when every call keeps a frame.

[<Fact>]
let ``Run - a function calling itself in tail position recurses a million deep`` () =
    let source =
        normalized """import nanyx/console

def countdown: (int, int) -> int = { n, acc ->
  if n == 0 then acc else countdown(n - 1, acc + 1)
}

def steps: (int, int) -> int = { n, acc ->
  match n
  | 0 -> acc
  | _ -> (n - 1) \steps(acc + 1)
}

println("{countdown(1000000, 0)}")
println("{steps(1000000, 0)}")
"""
    runSource source |> assertRan "1000000\n1000000\n"

[<Fact>]
let ``Run - a call in a lambda whose result is annotated is in tail position`` () =
    // The annotated result is a def of the made-up `__result` that the body gives back (see `LambdaResultSyntax`).
    let source =
        """import nanyx/console

def countdown = { (n: int), (acc: int): int -> if n == 0 then acc else countdown(n - 1, acc + 1) }

println("{countdown(1000000, 0)}")
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "1000000\n"

[<Fact>]
let ``Run - a local rec function calling itself in tail position recurses a million deep`` () =
    let source =
        normalized """import nanyx/console

def count: int -> int = { n ->
  rec loop = { i, acc ->
    if i == 0 then acc else loop(i - 1, acc + 1)
  }
  loop(n, 0)
}

println("{count(1000000)}")
"""
    runSource source |> assertRan "1000000\n"

[<Fact>]
let ``Run - functions calling each other in tail position recurse a million deep`` () =
    let source =
        normalized """import nanyx/console

def isEven: int -> bool = { n -> if n == 0 then true else isOdd(n - 1) }
def isOdd: int -> bool = { n -> if n == 0 then false else isEven(n - 1) }

println("{isEven(1000000)}")
"""
    runSource source |> assertRan "true\n"

[<Fact>]
let ``Run - a tail call through a closure uses no stack`` () =
    let source =
        normalized """import nanyx/console

def callBack: (int, (int -> int)) -> int = { n, f ->
  match n
  | 0 -> 0
  | _ -> f(n - 1)
}

def again: int -> int = { n -> callBack(n, again) }

println("{again(1000000)}")
"""
    runSource source |> assertRan "0\n"

[<Fact>]
let ``Run - a tail call that passes contexts on uses no stack`` () =
    let source =
        normalized """import nanyx/console

context Counter = (
  next: int -> int
)

def counted: [Counter] (int, int) -> int = { n, acc ->
  if n == 0 then acc else counted(next(n), acc + 1)
}

def main = {
  use Counter(next = { n -> n - 1 })
  println("{counted(1000000, 0)}")
}

main()
"""
    runSource source |> assertRan "1000000\n"

[<Fact>]
let ``Run - the body of use ... in is in tail position`` () =
    let source =
        normalized """import nanyx/console

context Counter = (next: int -> int)

def loop: (int, int) -> int = { n, acc ->
  if n == 0 then acc else use Counter(next = { m -> m - 1 }) in loop(next(n), acc + 1)
}

println("{loop(1000000, 0)}")
"""
    runSource source |> assertRan "1000000\n"
