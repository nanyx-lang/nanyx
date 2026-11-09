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
    "def count: list(string) -> int = {\n" +
    "  | [] -> 0\n" +
    "  | [_s, ...rest] -> 1 + count(rest)\n" +
    "}\n"

let private loop =
    "def go: (int, int) -> int = { i, acc ->\n" +
    "  if i == 0 then acc else go(i - 1, acc + step(i))\n" +
    "}\n" +
    "def run: int -> int = { n -> go(n, 0) }\n"

[<Fact>]
let ``Counting - a loop over lists and records runs in constant memory`` () =
    let source =
        count +
        "def step: int -> int = { i ->\n" +
        "  def words = [\"one {i}\", \"two {i}\"] ++ [\"three\"]\n" +
        "  def entry = (name = \"entry {i}\", words = words, size = i)\n" +
        "  def bigger = entry with size = entry.size + 1\n" +
        "  count(bigger.words) + bigger.size + count([bigger.name, ...words])\n" +
        "}\n" +
        loop
    assertRunsInConstantMemory source

[<Fact>]
let ``Counting - a list built by recursion is freed`` () =
    let source =
        count +
        "def build: int -> list(string) = { n ->\n" +
        "  match n\n" +
        "  | 0 -> []\n" +
        "  | _ -> [\"item {n}\", ...build(n - 1)]\n" +
        "}\n" +
        "def step: int -> int = { i -> count(build(5)) + i }\n" +
        loop
    assertRunsInConstantMemory source

[<Fact>]
let ``Counting - closures and what they capture are freed`` () =
    let source =
        count +
        "def step: int -> int = { i ->\n" +
        "  def suffix = \"-{i}\"\n" +
        "  def tagged = List.map([\"a\", \"b {i}\"], { s -> s ++ suffix })\n" +
        "  count(tagged)\n" +
        "}\n" +
        loop
    assertRunsInConstantMemory source

[<Fact>]
let ``Counting - tags with payloads are freed on every branch`` () =
    let source =
        "def find: (list(string), int) -> #found(string) | #missing = { xs, i ->\n" +
        "  match xs\n" +
        "  | [] -> #missing\n" +
        "  | [x, ...rest] -> if i == 0 then #found(x) else find(rest, i - 1)\n" +
        "}\n" +
        "def step: int -> int = { i ->\n" +
        "  match find([\"a {i}\", \"b {i}\", \"c\"], i % 4)\n" +
        "  | #found(s) -> if s == \"c\" then 2 else 1\n" +
        "  | #missing -> 0\n" +
        "}\n" +
        loop
    assertRunsInConstantMemory source
