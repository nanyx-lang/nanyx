module TranspilerWasmTests.Features.Memory.Examples

open System.IO
open Wasmtime
open Xunit
open Transpiler.Wasm
open TranspilerWasmTestHelpers

/// Runs the program in `path`, and gives the bytes of the heap the allocator handed out to it (each block with its size
/// word) and those still in use at the end: not back on a free list (see `WasmRuntime.runtimeHelpers`).
let private heapAfterRunning (path: string) =
    let wat = transpileWatFile path
    use engine = new Engine()
    use store = new Store(engine)
    store.SetWasiConfiguration(WasiConfiguration())
    use linker = new Linker(engine)
    linker.DefineWasi()
    let exported = wat.Substring(0, wat.LastIndexOf ')') + "  (export \"heap_ptr\" (global $heap_ptr))\n)"
    use module' = Module.FromText(engine, "example", exported)
    let instance = linker.Instantiate(store, module')
    let memory = instance.GetMemory("memory")
    let word (address: int) = memory.ReadInt32(int64 address)
    let heads, heapStart = CodeGen.allocatorLayout
    let handedOut = (instance.GetGlobal("heap_ptr").GetValue() :?> int) - heapStart
    let rec freeBytes (block: int) = if block = 0 then 0 else word (block - 4) + 4 + freeBytes (word block)
    handedOut, handedOut - (heads |> List.sumBy (word >> freeBytes))

[<Theory>]
[<InlineData("directives.nyx")>]
[<InlineData("money.nyx")>]
[<InlineData("shop.nyx")>]
let ``Counting - an example frees everything it allocates`` (example: string) =
    let path = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples", example)
    let handedOut, live = heapAfterRunning path
    Assert.True(handedOut > 0, "the example allocates nothing")
    Assert.Equal(0, live)
