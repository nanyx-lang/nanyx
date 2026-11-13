module TranspilerWasmTests.Features.Memory.Reuse

open Xunit
open TranspilerWasmTestHelpers

// A `match` arm that takes apart a value it holds the only reference to, and builds one of the same size, writes the
// new value into the old one's memory (see `CoreIrReuse`). The tests count the blocks the allocator hands out.

let private lists =
    "def build: int -> list(int) = { n -> if n == 0 then [] else [n, ...build(n - 1)] }\n" +
    "def sum: list(int) -> int = {\n" +
    "  | [] -> 0\n" +
    "  | [x, ...rest] -> x + sum(rest)\n" +
    "}\n" +
    "def double: list(int) -> list(int) = {\n" +
    "  | [] -> []\n" +
    "  | [x, ...rest] -> [x * 2, ...double(rest)]\n" +
    "}\n" +
    "def built: int -> int = { n -> sum(build(n)) }\n"

/// How many blocks `name(argument)` allocates, and what it returns.
let private allocationsOf (program: WasmProgram) (name: string) (argument: int) =
    let before = program.Allocations
    let result = program.Call(name, argument)
    program.Allocations - before, result

[<Fact>]
let ``Reuse - mapping over a list held once rewrites its cells in place`` () =
    use program = instantiateWasm (lists + "def run: int -> int = { n -> sum(double(build(n))) }\n") []
    let buildAllocations, total = allocationsOf program "built" 100
    let mapAllocations, doubled = allocationsOf program "run" 100
    Assert.Equal(100, buildAllocations)
    // Building the list allocates each cell; doubling it allocates none.
    Assert.Equal(buildAllocations, mapAllocations)
    Assert.Equal(2 * total, doubled)

[<Fact>]
let ``Reuse - List.map with a closure over a list held once rewrites its cells in place`` () =
    let source = lists + "def run: int -> int = { n -> sum(list.map(build(n), { x -> x + 1 })) }\n"
    use program = instantiateWasm source []
    let buildAllocations, total = allocationsOf program "built" 100
    let mapAllocations, mapped = allocationsOf program "run" 100
    // As many blocks as building it: the closure captures nothing, so it is static data.
    Assert.Equal(buildAllocations, mapAllocations)
    Assert.Equal(total + 100, mapped)

[<Fact>]
let ``Reuse - mapping over a list still used afterwards leaves it as it was`` () =
    let source =
        lists +
        "def run: int -> int = { n ->\n" +
        "  def xs = build(n)\n" +
        "  def ys = double(xs)\n" +
        "  sum(xs) * 1000 + sum(ys)\n" +
        "}\n"
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 10
    Assert.Equal(20, allocations)
    Assert.Equal(55 * 1000 + 110, result)

[<Fact>]
let ``Reuse - a tuple taken apart and rebuilt is rewritten in place`` () =
    let source =
        "def step: (int, (int, string)) -> (int, string) = { i, state ->\n" +
        "  match state\n" +
        "  | (count, label) -> (count + i, label)\n" +
        "}\n" +
        "def go: (int, (int, string)) -> int = { i, state ->\n" +
        "  if i == 0 then state.0 else go(i - 1, step(i, state))\n" +
        "}\n" +
        "def run: int -> int = { n -> go(n, (0, \"total\")) }\n"
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 100
    // The record built at the start, and nothing after.
    Assert.Equal(1, allocations)
    Assert.Equal(5050, result)

[<Fact>]
let ``Reuse - a tag's cell taken apart and rebuilt is rewritten in place`` () =
    let source =
        "def bump: #some(int) | #none -> #some(int) | #none = {\n" +
        "  | #some(n) -> #some(n + 1)\n" +
        "  | #none -> #none\n" +
        "}\n" +
        "def go: (int, #some(int) | #none) -> #some(int) | #none = { i, value ->\n" +
        "  if i == 0 then value else go(i - 1, bump(value))\n" +
        "}\n" +
        "def run: int -> int = { n ->\n" +
        "  match go(n, #some(0))\n" +
        "  | #some(total) -> total\n" +
        "  | #none -> -1\n" +
        "}\n"
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 100
    Assert.Equal(1, allocations)
    Assert.Equal(100, result)
