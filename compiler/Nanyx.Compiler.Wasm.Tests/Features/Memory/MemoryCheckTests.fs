module TranspilerWasmTests.Features.Memory.MemoryChecks

open System
open Wasmtime
open Xunit
open Transpiler.Wasm.CodeGen

/// The checked allocator and reference counts (see `WasmOptions.CheckMemory`), instantiated on Wasmtime.
type private CheckedAllocator(engine: Engine, store: Store, instance: Instance) =
    let memory = instance.GetMemory("memory")
    member _.Alloc(size: int) = instance.GetFunction<int, int>("alloc").Invoke size
    member _.Free(block: int) = instance.GetAction<int>("free").Invoke block
    member _.Retain(count: int) = instance.GetAction<int>("retain").Invoke count
    member _.Release(count: int) = instance.GetFunction<int, int>("release").Invoke count
    member _.LiveBlocks = instance.GetGlobal(CheckedExports.liveBlocks).GetValue() :?> int
    member _.Allocations = instance.GetGlobal(CheckedExports.allocations).GetValue() :?> int
    member _.Word(address: int) = memory.ReadInt32(int64 address)
    member _.SetWord(address: int, value: int) = memory.WriteInt32(int64 address, value)
    interface IDisposable with
        member _.Dispose() =
            store.Dispose()
            engine.Dispose()

let private instantiate () =
    let engine = new Engine()
    let store = new Store(engine)
    store.SetWasiConfiguration(WasiConfiguration())
    use module' = Module.FromText(engine, "allocator", allocatorModuleWat { CheckMemory = true })
    use linker = new Linker(engine)
    linker.DefineWasi()
    new CheckedAllocator(engine, store, linker.Instantiate(store, module'))

/// Asserts that `action` stops the program as a failed memory check does.
let private assertCheckFails (action: unit -> unit) =
    let error = Assert.ThrowsAny<WasmtimeException>(fun () -> action ())
    Assert.Equal(Nullable CheckedExports.failureExitCode, error.ExitCode)

[<Fact>]
let ``Checks - count the blocks handed out and taken back`` () =
    use allocator = instantiate ()
    let a = allocator.Alloc 8
    let b = allocator.Alloc 16
    Assert.Equal(2, allocator.LiveBlocks)
    allocator.Free a
    Assert.Equal(1, allocator.LiveBlocks)
    allocator.Free b
    Assert.Equal(0, allocator.LiveBlocks)
    Assert.Equal(2, allocator.Allocations)

[<Fact>]
let ``Checks - stop the program at a block freed twice`` () =
    use allocator = instantiate ()
    let block = allocator.Alloc 8
    allocator.Free block
    assertCheckFails (fun () -> allocator.Free block)

[<Fact>]
let ``Checks - a freed block handed out again can be freed again`` () =
    use allocator = instantiate ()
    let block = allocator.Alloc 40
    allocator.Free block
    let again = allocator.Alloc 40
    Assert.Equal(block, again)
    Assert.Equal(40, allocator.Word(again - 4))
    allocator.Free again
    Assert.Equal(0, allocator.LiveBlocks)

[<Fact>]
let ``Checks - fill a freed block with poison after its free-list link`` () =
    use allocator = instantiate ()
    let block = allocator.Alloc 12
    allocator.Free block
    Assert.Equal(0xDEADBEEF, allocator.Word(block + 4))
    Assert.Equal(0xDEADBEEF, allocator.Word(block + 8))

[<Fact>]
let ``Checks - stop the program at releasing an object after it was freed`` () =
    use allocator = instantiate ()
    let block = allocator.Alloc 12
    allocator.SetWord(block, 1)
    Assert.Equal(1, allocator.Release block)
    allocator.Free block
    assertCheckFails (fun () -> allocator.Release block |> ignore)

[<Fact>]
let ``Checks - stop the program at retaining an object after it was freed`` () =
    use allocator = instantiate ()
    let block = allocator.Alloc 12
    allocator.Free block
    assertCheckFails (fun () -> allocator.Retain block)

[<Fact>]
let ``Checks - leave an object below the heap alone`` () =
    use allocator = instantiate ()
    // A string literal's count is 0, and the word before it is not a size word: here it looks like a freed mark.
    allocator.SetWord(196, 1)
    allocator.SetWord(200, 0)
    Assert.Equal(0, allocator.Release 200)
    allocator.Retain 200
    Assert.Equal(0, allocator.Word 200)
