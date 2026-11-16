module TranspilerWasmTests.Features.Core.Allocator

open System
open Wasmtime
open Xunit
open Transpiler.Wasm.CodeGen

/// The allocator's `alloc` and `free`, instantiated on Wasmtime, together with the block size `alloc` recorded.
type private Allocator(engine: Engine, store: Store, instance: Instance) =
    let alloc = instance.GetFunction<int, int>("alloc")
    let free = instance.GetAction<int>("free")
    let memory = instance.GetMemory("memory")
    member _.Alloc(size: int) = alloc.Invoke size
    member _.Free(ptr: int) = free.Invoke ptr
    member _.BlockSize(ptr: int) = memory.ReadInt32(int64 (ptr - 4))
    member _.Pages = memory.GetSize()
    interface IDisposable with
        member _.Dispose() =
            store.Dispose()
            engine.Dispose()

let private instantiate () =
    let engine = new Engine()
    let store = new Store(engine)
    use module' = Module.FromText(engine, "allocator", allocatorModuleWat WasmOptions.Default)
    use linker = new Linker(engine)
    new Allocator(engine, store, linker.Instantiate(store, module'))

[<Fact>]
let ``Allocator - hands out distinct 4-byte-aligned blocks recording their rounded size`` () =
    use allocator = instantiate ()
    let a = allocator.Alloc 5
    let b = allocator.Alloc 5
    Assert.NotEqual(a, b)
    Assert.Equal(0, a % 4)
    Assert.Equal(0, b % 4)
    Assert.Equal(8, allocator.BlockSize a)
    Assert.True(b - a >= 12, "a block's size word must not overlap the block before it")

[<Fact>]
let ``Allocator - a freed small block is reused by the next allocation of its size`` () =
    use allocator = instantiate ()
    let a = allocator.Alloc 8
    let b = allocator.Alloc 8
    allocator.Free a
    allocator.Free b
    // A free list is last-in, first-out.
    Assert.Equal(b, allocator.Alloc 8)
    Assert.Equal(a, allocator.Alloc 7)
    Assert.NotEqual(a, allocator.Alloc 8)

[<Fact>]
let ``Allocator - a freed small block is not handed out for another size`` () =
    use allocator = instantiate ()
    let a = allocator.Alloc 8
    allocator.Free a
    let b = allocator.Alloc 12
    let c = allocator.Alloc 4
    Assert.NotEqual(a, b)
    Assert.NotEqual(a, c)
    Assert.Equal(a, allocator.Alloc 8)

[<Fact>]
let ``Allocator - a freed block of 256 bytes goes back to its own size's list, not the large blocks' (#1464)`` () =
    use allocator = instantiate ()
    let a = allocator.Alloc 256
    let _ = allocator.Alloc 8
    allocator.Free a
    Assert.NotEqual(a, allocator.Alloc 260)
    Assert.NotEqual(a, allocator.Alloc 252)
    Assert.Equal(a, allocator.Alloc 256)

[<Fact>]
let ``Allocator - a freed large block is reused by an allocation it can hold`` () =
    use allocator = instantiate ()
    let big = allocator.Alloc 401
    let _ = allocator.Alloc 8
    let bigger = allocator.Alloc 800
    let _ = allocator.Alloc 8
    allocator.Free bigger
    allocator.Free big
    // First fit in address order: `big` is too small for this, so the search moves on to `bigger`.
    Assert.Equal(bigger, allocator.Alloc 600)
    // `big` has too little to spare to split, so it is handed out whole and keeps its own size.
    Assert.Equal(big, allocator.Alloc 300)
    Assert.Equal(404, allocator.BlockSize big)
    allocator.Free big
    Assert.Equal(big, allocator.Alloc 404)

[<Fact>]
let ``Allocator - a large block with room to spare is split, and the rest is handed out next`` () =
    use allocator = instantiate ()
    let big = allocator.Alloc 800
    let _ = allocator.Alloc 8
    allocator.Free big
    Assert.Equal(big, allocator.Alloc 400)
    Assert.Equal(400, allocator.BlockSize big)
    let rest = allocator.Alloc 396
    Assert.Equal(big + 404, rest)
    Assert.Equal(396, allocator.BlockSize rest)

[<Fact>]
let ``Allocator - freed large blocks next to each other are joined into one`` () =
    use allocator = instantiate ()
    let a = allocator.Alloc 400
    let b = allocator.Alloc 400
    let c = allocator.Alloc 400
    let _ = allocator.Alloc 8
    // The middle block last, so that it joins both neighbours.
    allocator.Free a
    allocator.Free c
    allocator.Free b
    Assert.Equal(a, allocator.Alloc 1208)
    Assert.Equal(1208, allocator.BlockSize a)

[<Fact>]
let ``Allocator - a freed large block at the top of the heap goes back to it`` () =
    use allocator = instantiate ()
    let a = allocator.Alloc 400
    let b = allocator.Alloc 400
    allocator.Free a
    // `b` ends at the top, so it and `a`, joined to it, both go back: the next block starts where `a` did.
    allocator.Free b
    Assert.Equal(a, allocator.Alloc 1600)
    Assert.Equal(1600, allocator.BlockSize a)

[<Fact>]
let ``Allocator - a string grown by copying reuses the copies it frees`` () =
    use allocator = instantiate ()
    // What `acc ++ "ab"` does: a new block two bytes longer, then the old one freed.
    let mutable acc = allocator.Alloc 40
    let first = acc
    for size in 42 .. 2 .. 20040 do
        let grown = allocator.Alloc size
        allocator.Free acc
        acc <- grown
    // The heap holds at most about two copies of the string, not every copy there was.
    Assert.True(acc - first < 3 * 20040, $"the last copy starts {acc - first} bytes past the first")

[<Fact>]
let ``Allocator - grows the memory when the heap runs out`` () =
    use allocator = instantiate ()
    let pagesBefore = allocator.Pages
    let p = allocator.Alloc 100000
    Assert.True(allocator.Pages > pagesBefore)
    Assert.Equal(100000, allocator.BlockSize p)
    allocator.Free p
    Assert.Equal(p, allocator.Alloc 70000)
