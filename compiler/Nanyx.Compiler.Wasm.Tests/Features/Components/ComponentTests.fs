/// The core module of a WASI 0.3 component (`WasmOptions.Component`, see `ComponentAbi`), and the componentizer that
/// wraps it (`Nanyx.Runtime.Componentizer`), with the console host's `wit/`: its world `nanyx:console/command`, with WASI
/// 0.3.0's own WIT, as WASI's v0.3.0 tag has it, under `wit/deps`.
module TranspilerWasmTests.Features.Components.Component

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

let private componentOptions = { WasmOptions.Default with Component = true }

/// The console host's WIT package, whose world `command` the tests wrap modules as.
let private wit = Path.Combine(NyxCompiler.ShippedPackages.directoryOf NyxCompiler.Hosts.consolePackage, "wit")

/// The modules a module's text imports from.
let private importedModules (wat: string) =
    Regex.Matches(wat, """\(import "([^"]+)" """) |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq

/// What a binary starts with: a component's preamble, or a core module's.
let private preamble (bytes: byte array) = bytes |> Array.take 8

let private componentPreamble = [| 0x00uy; 0x61uy; 0x73uy; 0x6duy; 0x0duy; 0x00uy; 0x01uy; 0x00uy |]

[<Fact>]
let ``A component's core module exports its entry as wasi:cli/run's run, and cabi_realloc, and runs nothing when instantiated`` () =
    let wat = transpileWatWith componentOptions (normalized "dbg(1)\n")
    Assert.Contains("""(export "wasi:cli/run@0.3.0#run")""", wat)
    Assert.Contains("""(export "cabi_realloc")""", wat)
    Assert.DoesNotContain("(start ", wat)

[<Fact>]
let ``A component's core module writes and exits through wasi:cli alone, not WASI preview 1`` () =
    // `println` is a foreign function of preview 1's `fd_write`, which the module defines itself on `wasi:cli`.
    let wat = transpileWatWith componentOptions (normalized "dbg(1)\nprintln(\"hi\")\n")
    Assert.Equal<Set<string>>(set [ "$root"; "wasi:cli/exit@0.3.0"; "wasi:cli/stderr@0.3.0"; "wasi:cli/stdout@0.3.0" ], importedModules wat)

[<Fact>]
let ``A program with no top-level code still has an entry`` () =
    let wat = transpileWatWith componentOptions (normalized "def double = { x -> x * 2 }\n")
    Assert.Contains("""(export "wasi:cli/run@0.3.0#run")""", wat)

[<Theory>]
[<InlineData("dbg(1)\nprintln(\"hi\")\n")>]
// A crash writes why to stderr, with the runtime's own helper, and stops.
[<InlineData("println(\"before\")\nlet xs = [1, 2]\nprintln(xs[5])\n")>]
let ``The componentizer wraps a component's core module as a component of the console host's world`` (source: string) =
    let wat = transpileWatWith { componentOptions with Places = true } (normalized source)
    match Nanyx.Runtime.Componentizer.componentize (Transpiler.Wasm.WasmBinary.assemble wat) wit "command" with
    | Ok bytes -> Assert.Equal<byte array>(componentPreamble, preamble bytes)
    | Error message -> Assert.Fail message

[<Fact>]
let ``The componentizer says why it can't wrap a module that imports what the world doesn't have`` () =
    // A module the runtime runs imports WASI preview 1, which no WASI 0.3 world has.
    let wat = transpileWatWith WasmOptions.Default (normalized "println(\"hi\")\n")
    match Nanyx.Runtime.Componentizer.componentize (Transpiler.Wasm.WasmBinary.assemble wat) wit "command" with
    | Ok _ -> Assert.Fail "A module that imports WASI preview 1 was wrapped"
    | Error message -> Assert.Contains("wasi_snapshot_preview1", message)

/// The core module of a component compiled from `source`, instantiated on Wasmtime with each of its imports a function
/// that traps.
let private instantiateCore (options: WasmOptions) (source: string) =
    let engine = new Wasmtime.Engine()
    let store = new Wasmtime.Store(engine)
    use module' = Wasmtime.Module.FromText(engine, "component", transpileWatWith options (normalized source))
    use linker = new Wasmtime.Linker(engine)
    for import in module'.Imports do
        match import with
        | :? Wasmtime.FunctionImport as func ->
            let trap = Wasmtime.Function.UntypedCallbackDelegate(fun _ _ _ -> failwith $"{func.ModuleName} {func.Name} was called")
            linker.Define(func.ModuleName, func.Name, Wasmtime.Function.FromCallback(store, trap, func.Parameters, func.Results))
        | _ -> ()
    engine, store, linker.Instantiate(store, module')

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``A component's cabi_realloc hands out blocks aligned to the 8 bytes it is asked for, which it can free (#2275)`` (checkMemory: bool) =
    let engine, store, instance = instantiateCore { componentOptions with CheckMemory = checkMemory } "dbg(1)\n"
    use _ = engine
    use _ = store
    let realloc = instance.GetFunction<int, int, int, int, int>("cabi_realloc")
    // With memory checks, the blocks still live: one more for each new block, none for a block grown or shrunk.
    let liveBlocks () = instance.GetGlobal("live_blocks").GetValue() :?> int
    let liveBefore = if checkMemory then liveBlocks () else 0
    let sizes = [ 1; 4; 8; 12; 16; 24; 100; 252; 256; 260; 300; 1000; 4096 ]
    // New blocks, of every size class and some large ones, one after another, and each grown and shrunk, which frees
    // the block it is given: every block, new or reused, is a multiple of 8.
    for round in 1..4 do
        for size in sizes do
            let block = realloc.Invoke(0, 0, 8, size)
            Assert.True(block % 8 = 0, $"cabi_realloc(0, 0, 8, {size}) gave {block}, in round {round}")
            let grown = realloc.Invoke(block, size, 8, size * 2 + round * 4)
            Assert.True(grown % 8 = 0, $"cabi_realloc({block}, {size}, 8, {size * 2 + round * 4}) gave {grown}")
            let shrunk = realloc.Invoke(grown, size * 2 + round * 4, 8, size)
            Assert.True(shrunk % 8 = 0, $"cabi_realloc({grown}, ..., 8, {size}) gave {shrunk}")
            // A 4-byte aligned block in between moves the next one off an 8-byte boundary.
            Assert.Equal(0, realloc.Invoke(0, 0, 4, size + 4) % 4)
    if checkMemory then
        Assert.Equal(liveBefore + 4 * sizes.Length * 2, liveBlocks ())
