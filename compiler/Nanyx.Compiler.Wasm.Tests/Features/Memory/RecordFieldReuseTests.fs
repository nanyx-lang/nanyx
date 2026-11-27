module TranspilerWasmTests.Features.Memory.RecordFieldReuse

open Xunit
open TranspilerWasmTestHelpers

// A record built from the fields of another that dies there, `P(x = p.x + 1, y = p.y, z = p.z + n)`, is written into
// that record's block when nothing else holds it, as a `with` or a match arm that takes it apart is (#2209, see
// `CoreIrReuseFields`). The tests count the blocks the allocator hands out.

let private stepping =
    normalized """type P = (x: int, y: int, z: int)

def step: int, P -> P = { n, p ->
  if n == 0 then p else step(n - 1, P(x = p.x + 1, y = p.y, z = p.z + n))
}

def run: int -> int = { n ->
  def p = step(n, P(x = 0, y = 1, z = 2))
  p.x + p.z
}

def kept: int -> int = { n ->
  def p = P(x = n, y = 1, z = 2)
  def q = step(3, p)
  p.x * 1000 + q.x + q.z
}
"""

[<Fact>]
let ``A record built from the fields of one that dies there is written into its block`` () =
    use program = instantiateWasm stepping []
    // The first record alone, however many steps: each step made a new one, and freed the one before.
    let few, small = allocationsOf program "run" 10
    let many, large = allocationsOf program "run" 10_000
    Assert.Equal((1, 1), (few, many))
    Assert.Equal((10 + 2 + 55, 10_000 + 2 + 50_005_000), (small, large))
    Assert.Equal(0, program.LiveBlocks)

[<Fact>]
let ``A record still held elsewhere is copied, and left as it was`` () =
    use program = instantiateWasm stepping []
    let allocations, result = allocationsOf program "kept" 7
    // `p`, and the record the first step makes, since the caller still holds `p`; the later steps write into that one.
    Assert.Equal((2, 7_000 + 10 + 8), (allocations, result))
    Assert.Equal(0, program.LiveBlocks)

let private queueing =
    normalized """import nanyx/queue

def fill: int, Queue(int) -> Queue(int) = { n, q ->
  if n == 0 then q else fill(n - 1, Queue.enqueue(q, n))
}

def drain: Queue(int), int -> int = { q, acc ->
  match Queue.dequeue(q)
    | #some(value, rest) -> drain(rest, acc + value)
    | #none -> acc
}

def queued: int -> int = { n -> drain(fill(n, Queue.empty), 0) }
"""

[<Fact>]
let ``A queue's record is written into its block at each enqueue and dequeue`` () =
    use program = instantiateWasm queueing []
    // `Queue.empty`, a top-level value, is made once and kept: what is live after no element.
    allocationsOf program "queued" 0 |> ignore
    let kept = program.LiveBlocks
    let few, small = allocationsOf program "queued" 100
    let many, large = allocationsOf program "queued" 1_000
    // At most three blocks for each element: its cell in the back list, its cell when the back list is reversed into
    // the front, and what a dequeue gives. The queue's record was two more: one at each enqueue, and one at each dequeue.
    Assert.InRange(many - few, 0, 3 * 900)
    Assert.Equal((5_050, 500_500), (small, large))
    Assert.Equal(kept, program.LiveBlocks)
