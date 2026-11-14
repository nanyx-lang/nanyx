module TranspilerWasmTests.Features.Memory.RecordUpdate

open Xunit
open TranspilerWasmTestHelpers

// `record with field = value` takes the record over: when it holds the only reference it updates the record in place,
// and otherwise it copies it (see `UpdateRecord` in `CoreIrEmit`). The tests count the blocks the allocator hands out.

let private particles =
    normalized """type Particle = (x: int, y: int, vx: int, vy: int, name: string)

def step: Particle -> Particle = { p ->
  p with (x = p.x + p.vx, y = p.y + p.vy)
}

def simulate: Particle, int -> Particle = { p, n ->
  if n == 0 then p else simulate(step(p), n - 1)
}
"""

/// How many blocks `name(argument)` allocates, and what it returns.
let private allocationsOf (program: WasmProgram) (name: string) (argument: int) =
    let before = program.Allocations
    let result = program.Call(name, argument)
    program.Allocations - before, result

[<Fact>]
let ``Record update - with on a record held once updates it in place`` () =
    // Each step used to copy the record (#1295).
    let source =
        particles
        + normalized """def run: int -> int = { n ->
  def end = simulate(Particle(x = 0, y = 0, vx = 3, vy = -2, name = "first"), n)
  end.x + end.y
}
"""
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 1000
    // The particle itself, and nothing for any step.
    Assert.Equal(1, allocations)
    Assert.Equal(1000, result)
    Assert.Equal(0, program.LiveBlocks)

[<Fact>]
let ``Record update - with on a record still used afterwards copies it and leaves it as it was`` () =
    let source =
        particles
        + normalized """def run: int -> int = { n ->
  def p = Particle(x = n, y = 0, vx = 0, vy = 0, name = "first")
  def q = p with (x = 1)
  p.x * 10 + q.x
}
"""
    use program = instantiateWasm source []
    let allocations, result = allocationsOf program "run" 7
    Assert.Equal(2, allocations)
    Assert.Equal(71, result)
    Assert.Equal(0, program.LiveBlocks)

[<Fact>]
let ``Run - with in place replaces a field on the heap, made from the value it replaces`` () =
    // The new name is read from the old one, which the update then drops. `runSource` checks that nothing is freed
    // twice or left on the heap.
    let source =
        normalized """import nanyx/console

type Named = (name: string, count: int)

def rename: Named, int -> Named = { r, n ->
  if n == 0 then r else rename(r with (name = r.name ++ "!", count = r.count + 1), n - 1)
}

@entry
def main = {
  def start = Named(name = "a{1}", count = 0)
  def shared = start with (count = 10)
  def renamed = rename(start, 3)
  println("{renamed.name} {renamed.count}")
  println("{shared.name} {shared.count}")
}
"""
    runSource source |> assertRan "a1!!! 3\na1 10\n"
