/// The functions a component's core module exports for the definitions a program marks `@component` (#2241): each
/// takes its arguments and gives its result as the canonical ABI passes them, and leaves nothing on the heap once the
/// host has called its post-return function.
module TranspilerWasmTests.Features.Components.ComponentExports

open System.Text
open Xunit
open NyxCompiler
open Transpiler.Wasm
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// The WIT package the tests export from.
let private package = "acme:test@0.1.0"

let private source =
    """@component
export def area: float, float -> float = { width, height -> width * height }

@component
export def twice: I32 -> I32 = { n -> n * 2 }

@component
export def isEven: int -> bool = { n -> n % 2 == 0 }

@component
export def tick: () -> () = { -> () }

@component
export def greet: string -> string = { name -> "Hello, " ++ name ++ "!" }

@component
export def measure: string, U8 -> int = { text, extra -> string.length(text) + U8.toInt(extra) }

@component
export def title: () -> string = { -> "shapes" }
"""

/// The core module of a component compiled from `source`, checking its memory if `checkMemory`, instantiated with
/// each import a function that traps, and the name each export of a def is exported under, by the def's name.
let private instantiate (checkMemory: bool) =
    let typed =
        match Compiler.compile (normalized source) with
        | { Typed = Some typed; Diagnostics = [] } -> typed
        | result -> failwith $"%A{result.Diagnostics}"
    let exports = ComponentInterface.functions typed |> List.map (fun func -> ComponentInterface.exportName package func.Export, func)
    let options = { WasmOptions.Default with Component = Some CliRun; ComponentExports = exports; CheckMemory = checkMemory }
    let engine, store, instance = instantiateCore (transpileTypedModuleWithOptions options typed |> fst)
    let names = exports |> List.map (fun (name, func) -> func.Export.Name, name) |> Map.ofList
    engine, store, instance, names

[<Fact>]
let ``An export of numbers and bools takes and gives them as they are, and one of () takes and gives nothing`` () =
    let engine, store, instance, names = instantiate false
    use _ = engine
    use _ = store
    Assert.Equal(12.0, instance.GetFunction<double, double, double>(names.["area"]).Invoke(3.0, 4.0))
    Assert.Equal(-42, instance.GetFunction<int, int>(names.["twice"]).Invoke(-21))
    Assert.Equal(1, instance.GetFunction<int64, int>(names.["isEven"]).Invoke(10L))
    Assert.Equal(0, instance.GetFunction<int64, int>(names.["isEven"]).Invoke(7L))
    instance.GetAction(names.["tick"]).Invoke()

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``An export of strings takes them from blocks the host allocated, and frees what it returns after the host has copied it`` (checkMemory: bool) =
    let engine, store, instance, names = instantiate checkMemory
    use _ = engine
    use _ = store
    let memory = instance.GetMemory "memory"
    let realloc = instance.GetFunction<int, int, int, int, int>("cabi_realloc")
    let liveBlocks () = if checkMemory then instance.GetGlobal("live_blocks").GetValue() :?> int else 0
    // As the host does: the argument in a block it allocates, then the result read from the block returned.
    let pass (text: string) =
        let bytes = Encoding.UTF8.GetBytes text
        let address = if bytes.Length = 0 then 1 else realloc.Invoke(0, 0, 1, bytes.Length)
        bytes |> Array.iteri (fun index byte -> memory.WriteByte(int64 (address + index), byte))
        address, bytes.Length
    let read (area: int) =
        let address = memory.ReadInt32(int64 area)
        let length = memory.ReadInt32(int64 area + 4L)
        Encoding.UTF8.GetString(Array.init length (fun index -> memory.ReadByte(int64 (address + index))))
    let before = liveBlocks ()
    for name in [ "world"; ""; String.replicate 300 "é" ] do
        let address, length = pass name
        let area = instance.GetFunction<int, int, int>(names.["greet"]).Invoke(address, length)
        Assert.Equal($"Hello, {name}!", read area)
        instance.GetAction<int>("cabi_post_" + names.["greet"]).Invoke(area)
        let address, length = pass name
        let measured = instance.GetFunction<int, int, int, int64>(names.["measure"]).Invoke(address, length, 255)
        Assert.Equal(int64 (Encoding.UTF8.GetByteCount name + 255), measured)
    // A string literal is never freed.
    for _ in 1..3 do
        let area = instance.GetFunction<int>(names.["title"]).Invoke()
        Assert.Equal("shapes", read area)
        instance.GetAction<int>("cabi_post_" + names.["title"]).Invoke(area)
    Assert.Equal(before, liveBlocks ())

[<Fact>]
let ``The componentizer wraps a core module with exports of its own as a component of a world that includes the host's`` () =
    let typed =
        match Compiler.compile (normalized source) with
        | { Typed = Some typed } -> typed
        | result -> failwith $"%A{result.Diagnostics}"
    let functions = ComponentInterface.functions typed
    let exports = functions |> List.map (fun func -> ComponentInterface.exportName package func.Export, func)
    let options = { WasmOptions.Default with Component = Some CliRun; ComponentExports = exports }
    let wat = transpileTypedModuleWithOptions options typed |> fst
    use target = new TempDirectory()
    let hostWit = System.IO.Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "wit")
    let world = ComponentInterface.write hostWit "nanyx:console/command" package functions target.Path
    Assert.Equal("acme:test/component@0.1.0", world)
    match Nanyx.Runtime.Componentizer.componentize (WasmBinary.assemble wat) target.Path world with
    | Ok bytes -> Assert.Equal<byte array>([| 0x00uy; 0x61uy; 0x73uy; 0x6duy; 0x0duy; 0x00uy; 0x01uy; 0x00uy |], bytes.[.. 7])
    | Error message -> Assert.Fail message
