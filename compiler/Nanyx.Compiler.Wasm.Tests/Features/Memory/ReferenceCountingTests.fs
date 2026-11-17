module TranspilerWasmTests.Features.Memory.ReferenceCounting

open Xunit
open TranspilerWasmTestHelpers

// Each program exports `run`, a loop that makes and gives up values `n` times. When everything an iteration allocates
// is freed by the next, the heap stays within a few kilobytes however long the loop runs, and running the loop again
// takes all its memory from the free lists.

let private assertRunsInConstantMemory (source: string) =
    use program = instantiateWasm source []
    let iterations = 20000
    program.Call("run", iterations) |> ignore
    let heapAfterFirstRun = program.HeapPointer
    Assert.True(heapAfterFirstRun < 16384, $"{iterations} iterations took the heap to {heapAfterFirstRun}")
    program.Call("run", iterations) |> ignore
    Assert.Equal(heapAfterFirstRun, program.HeapPointer)

let private count =
    normalized """def count: list(string) -> int = {
  | [] -> 0
  | [_s, ...rest] -> 1 + count(rest)
}
"""

let private loop =
    normalized """def go: (int, int) -> int = { i, acc ->
  if i == 0 then acc else go(i - 1, acc + step(i))
}
def run: int -> int = { n -> go(n, 0) }
"""

[<Fact>]
let ``Counting - a loop over lists and records runs in constant memory`` () =
    let source =
        count
        + normalized """def step: int -> int = { i ->
  def words = ["one {i}", "two {i}"] ++ ["three"]
  def entry = (name = "entry {i}", words = words, size = i)
  def bigger = entry with size = entry.size + 1
  count(bigger.words) + bigger.size + count([bigger.name, ...words])
}
"""
        + loop
    assertRunsInConstantMemory source

[<Fact>]
let ``Counting - a list built by recursion is freed`` () =
    let source =
        count
        + normalized """def build: int -> list(string) = { n ->
  match n
  | 0 -> []
  | _ -> ["item {n}", ...build(n - 1)]
}
def step: int -> int = { i -> count(build(5)) + i }
"""
        + loop
    assertRunsInConstantMemory source

[<Fact>]
let ``Counting - closures and what they capture are freed`` () =
    let source =
        count
        + normalized """def step: int -> int = { i ->
  def suffix = "-{i}"
  def tagged = list.map(["a", "b {i}"], { s -> s ++ suffix })
  count(tagged)
}
"""
        + loop
    assertRunsInConstantMemory source

[<Fact>]
let ``Counting - tags with payloads are freed on every branch`` () =
    let source =
        normalized """def find: (list(string), int) -> #found(string) | #missing = { xs, i ->
  match xs
  | [] -> #missing
  | [x, ...rest] -> if i == 0 then #found(x) else find(rest, i - 1)
}
def step: int -> int = { i ->
  match find(["a {i}", "b {i}", "c"], i % 4)
  | #found(s) -> if s == "c" then 2 else 1
  | #missing -> 0
}
"""
        + loop
    assertRunsInConstantMemory source

[<Fact>]
let ``Counting - a loop over lists in step frees them`` () =
    let source =
        count
        + normalized """def step: int -> int = { i ->
  def words = ["one {i}", "two {i}", "three"]
  def groups = [["a {i}"], ["b", "c {i}"]]
  for words, groups, 0.. | word, group, n -> count([word, ...group]) + n
  count(words)
}
"""
        + loop
    assertRunsInConstantMemory source
// `series` borrows `r`, which its tail call passes on unchanged, and owns `term`, which it gives up. A caller that
// passes one variable as both, or reads it again in a later argument, keeps it alive until the call is done.
let private series =
    """def series: string, string, int -> int = { r, term, k ->
  if k > 3 then string.length(term) else series(r, term ++ r, k + 1)
}
"""

[<Fact>]
let ``Counting - a variable passed to a borrowed and an owned parameter lives until the call is done`` () =
    let callers =
        """def withItself: string -> int = { z ->
  def r = z ++ "!"
  series(r, r, 1)
}
def withItsDouble: string -> int = { z ->
  def r = z ++ "!"
  series(r, r ++ r, 3)
}
println("{withItself("ab")} {withItsDouble("ab")}")
"""
    runSource ("" + series + callers) |> assertRan "12 9\n"

[<Fact>]
let ``Counting - a variable passed to a borrowed and an owned parameter is freed`` () =
    let step =
        """def step: int -> int = { i ->
  def r = "x {i % 3}"
  if series(r, r, 1) + series(r, r ++ r, 3) > 20 then 1 else 0
}
"""
    assertRunsInConstantMemory (series + step + loop)
