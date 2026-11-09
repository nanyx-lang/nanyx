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
    use module' = Module.FromText(engine, "allocator", allocatorModuleWat)
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
let ``Allocator - a freed large block is reused by an allocation it can hold`` () =
    use allocator = instantiate ()
    let big = allocator.Alloc 101
    let bigger = allocator.Alloc 200
    allocator.Free bigger
    allocator.Free big
    // First fit: `big`, freed last, is too small for this, so the search moves on to `bigger`.
    Assert.Equal(bigger, allocator.Alloc 150)
    Assert.Equal(big, allocator.Alloc 60)
    // The reused block keeps its own size, so freeing it again returns all of it.
    Assert.Equal(104, allocator.BlockSize big)
    allocator.Free big
    Assert.Equal(big, allocator.Alloc 104)

[<Fact>]
let ``Allocator - grows the memory when the heap runs out`` () =
    use allocator = instantiate ()
    let pagesBefore = allocator.Pages
    let p = allocator.Alloc 100000
    Assert.True(allocator.Pages > pagesBefore)
    Assert.Equal(100000, allocator.BlockSize p)
    allocator.Free p
    Assert.Equal(p, allocator.Alloc 70000)
