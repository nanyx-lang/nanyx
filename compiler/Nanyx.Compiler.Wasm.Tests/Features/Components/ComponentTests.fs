/// The core module of a WASI 0.3 component (`WasmOptions.Component`, see `ComponentAbi`), and the componentizer that
/// wraps it (`Nanyx.Runtime.Componentizer`), with the console host's `wit/`, for its world `nanyx:console/command`, and
/// the HTTP host's, for `wasi:http/service`, each with WASI 0.3.0's own WIT, as WASI's v0.3.0 tag has it, under
/// `wit/deps`.
module TranspilerWasmTests.Features.Components.Component

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Transpiler.Wasm
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

let private componentOptions = { WasmOptions.Default with Component = Some CliRun }

/// The console host's WIT package, whose world `command` the tests wrap modules as.
let private wit = Path.Combine(NyxCompiler.ShippedPackages.directoryOf NyxCompiler.Hosts.consolePackage, "wit")

let private handlerOptions = { WasmOptions.Default with Component = Some HttpHandler }

/// The HTTP host's WIT package, with which the tests wrap modules as components of `wasi:http/service`.
let private httpWit = Path.Combine(NyxCompiler.ShippedPackages.directoryOf "nanyx.http", "wit")

let private service = "wasi:http/service@0.3.0"

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

[<Fact>]
let ``A wasi:http/service world's entry is the handler, and any other world's is run`` () =
    Assert.Equal(HttpHandler, ComponentEntry.ofWorld service)
    Assert.Equal(CliRun, ComponentEntry.ofWorld "wasi:cli/command@0.3.0")
    Assert.Equal(CliRun, ComponentEntry.ofWorld "nanyx:console/command")

[<Fact>]
let ``A handler's core module exports handle, lifted async with its callback, and can't exit`` () =
    let wat = transpileWatWith handlerOptions (normalized "dbg(1)\n")
    Assert.Contains("""(export "[async-lift]wasi:http/handler@0.3.0#handle")""", wat)
    Assert.Contains("""(export "[callback][async-lift]wasi:http/handler@0.3.0#handle")""", wat)
    Assert.DoesNotContain("wasi:cli/run", wat)
    Assert.DoesNotContain("(start ", wat)
    Assert.DoesNotContain("wasi:cli/exit", wat)

[<Fact>]
let ``A handler's core module defines the compiler's own foreign functions, rather than import them`` () =
    let source =
        "import nanyx/host.runtime\n\n@extern(\"nanyx:component\", \"argument\")\ndef argument: [HostRuntime] () -> I32\n\n"
        + "@extern(\"nanyx:component\", \"result\")\ndef result: [HostRuntime] I32, I32, I32, I32, I32 -> ()\n\n"
        + "result(argument(), 0, 0, 0, 0)\n"
    let wat = hostWatWith handlerOptions source
    Assert.DoesNotContain("nanyx:component", importedModules wat)
    Assert.Contains("call $import_nanyx-3A-component_argument", wat)
    Assert.Contains("(func $import_nanyx-3A-component_argument (result i32)", wat)

[<Fact>]
let ``The componentizer wraps a handler's core module as a component of wasi:http/service`` () =
    let wat = transpileWatWith handlerOptions (normalized "dbg(1)\nprintln(\"hi\")\n")
    match Nanyx.Runtime.Componentizer.componentize (Transpiler.Wasm.WasmBinary.assemble wat) httpWit service with
    | Ok bytes -> Assert.Equal<byte array>(componentPreamble, preamble bytes)
    | Error message -> Assert.Fail message

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``A component's cabi_realloc hands out blocks aligned to the 8 bytes it is asked for, which it can free (#2275)`` (checkMemory: bool) =
    let engine, store, instance = instantiateCore (transpileWatWith { componentOptions with CheckMemory = checkMemory } (normalized "dbg(1)\n"))
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
