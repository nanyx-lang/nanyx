/// The functions a component's core module imports for the definitions a program marks `@import` (#2241): a call
/// lowers its arguments as the canonical ABI passes them, and lifts the result the imported function gives, leaving
/// nothing on the heap; and outside a component, a call of one is an error.
module TranspilerWasmTests.Features.Components.ComponentImports

open System
open System.Text
open Xunit
open NyxCompiler
open Transpiler.Wasm
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

let private source =
    """type Point = (x: int, y: float)

type Shape = #circle(float) | #rect(Point) | #empty

@import("acme:geometry/shapes@1.0.0", "area")
def area: Shape -> float

@import("acme:geometry/shapes@1.0.0")
def describeAll: string, list(int) -> string

@import("acme:geometry/shapes@1.0.0")
def origin: () -> Point

@component
export def total: float -> float = { r -> area(#circle(r)) + area(#rect((x = 2, y = 1.5))) + area(#empty) }

@component
export def label: string -> string = { s -> describeAll(s, [ 1, 2, 3 ]) }

@component
export def originX: () -> int = { -> origin().x }
"""

let private package = "acme:app@0.1.0"

let private typedSource () =
    match Compiler.compile (normalized source) with
    | { Typed = Some typed; Diagnostics = [] } -> typed
    | result -> failwith $"%A{result.Diagnostics}"

/// A string the module's memory holds at `address`, of `length` bytes.
let private readText (memory: Wasmtime.Memory) (address: int) (length: int) =
    Encoding.UTF8.GetString(Array.init length (fun index -> memory.ReadByte(int64 (address + index))))

/// The imported functions, as another component would implement them: each reads its arguments as the canonical ABI
/// passes them, and gives its result, a string in a block it allocates through the module's `cabi_realloc`.
let private implement (func: Wasmtime.FunctionImport) : Wasmtime.Function.UntypedCallbackDelegate option =
    match func.ModuleName, func.Name with
    | "acme:geometry/shapes@1.0.0", "area" ->
        Some (Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments results ->
            let area =
                match arguments.[0].AsInt32() with
                | 0 -> BitConverter.Int64BitsToDouble(arguments.[1].AsInt64()) * 10.0
                | 1 -> float (arguments.[1].AsInt64()) * arguments.[2].AsDouble()
                | _ -> 100.0
            results.[0] <- Wasmtime.ValueBox.op_Implicit area))
    | "acme:geometry/shapes@1.0.0", "describe-all" ->
        Some (Wasmtime.Function.UntypedCallbackDelegate(fun caller arguments _ ->
            let memory = caller.GetMemory "memory"
            let text = readText memory (arguments.[0].AsInt32()) (arguments.[1].AsInt32())
            let items = arguments.[2].AsInt32()
            let count = arguments.[3].AsInt32()
            let result = arguments.[4].AsInt32()
            let sum = List.sum [ for index in 0 .. count - 1 -> memory.ReadInt64(int64 (items + index * 8)) ]
            let bytes = Encoding.UTF8.GetBytes $"{text}:{sum}"
            let realloc = caller.GetFunction("cabi_realloc").WrapFunc<int, int, int, int, int>()
            let address = realloc.Invoke(0, 0, 1, bytes.Length)
            bytes |> Array.iteri (fun index byte -> memory.WriteByte(int64 (address + index), byte))
            memory.WriteInt32(int64 result, address)
            memory.WriteInt32(int64 result + 4L, bytes.Length)))
    | "acme:geometry/shapes@1.0.0", "origin" ->
        Some (Wasmtime.Function.UntypedCallbackDelegate(fun caller arguments _ ->
            let memory = caller.GetMemory "memory"
            let result = arguments.[0].AsInt32()
            memory.WriteInt64(int64 result, 7L)
            memory.WriteDouble(int64 result + 8L, 0.5)))
    | _ -> None

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``A call of an imported function lowers its arguments and lifts its result, leaving nothing on the heap`` (checkMemory: bool) =
    let typed = typedSource ()
    let exports = ComponentInterface.functions typed |> List.map (fun func -> ComponentInterface.exportName package func.Export, func)
    let options = { WasmOptions.Default with Component = Some CliRun; ComponentExports = exports; CheckMemory = checkMemory }
    let engine, store, instance = instantiateCoreWith implement (transpileTypedModuleWithOptions options typed |> fst)
    use _ = engine
    use _ = store
    let names = exports |> List.map (fun (name, func) -> func.Export.Name, name) |> Map.ofList
    let memory = instance.GetMemory "memory"
    let realloc = instance.GetFunction<int, int, int, int, int>("cabi_realloc")
    let liveBlocks () = if checkMemory then instance.GetGlobal("live_blocks").GetValue() :?> int else 0
    let before = liveBlocks ()
    Assert.Equal(123.0, instance.GetFunction<double, double>(names.["total"]).Invoke(2.0))
    Assert.Equal(7L, instance.GetFunction<int64>(names.["originX"]).Invoke())
    let bytes = Encoding.UTF8.GetBytes "ab"
    let address = realloc.Invoke(0, 0, 1, bytes.Length)
    bytes |> Array.iteri (fun index byte -> memory.WriteByte(int64 (address + index), byte))
    let labelled = instance.GetFunction<int, int, int>(names.["label"]).Invoke(address, bytes.Length)
    Assert.Equal("ab:6", readText memory (memory.ReadInt32(int64 labelled)) (memory.ReadInt32(int64 labelled + 4L)))
    instance.GetAction<int>("cabi_post_" + names.["label"]).Invoke(labelled)
    Assert.Equal(before, liveBlocks ())

[<Fact>]
let ``The componentizer builds a component that imports the interfaces the program's imports name, from their WIT`` () =
    let typed = typedSource ()
    let functions = ComponentInterface.functions typed
    let imports = ComponentInterface.imports typed
    let exports = functions |> List.map (fun func -> ComponentInterface.exportName package func.Export, func)
    let wat = transpileTypedModuleWithOptions { WasmOptions.Default with Component = Some CliRun; ComponentExports = exports } typed |> fst
    use target = new TempDirectory()
    let hostWit = IO.Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "wit")
    let world = ComponentInterface.write hostWit "nanyx:console/command" package functions imports target.Path
    Assert.Contains("  import acme:geometry/shapes@1.0.0;", IO.File.ReadAllText(IO.Path.Combine(target.Path, "component.wit")))
    match Nanyx.Runtime.Componentizer.componentize (WasmBinary.assemble wat) target.Path world with
    | Ok _ -> ()
    | Error message -> Assert.Fail message

[<Fact>]
let ``A call of an imported function outside a component is an error naming it`` () =
    let typed = typedSource ()
    match tryTranspileTypedModuleWith WasmOptions.Default typed with
    | Error diagnostic ->
        Assert.Equal(DiagnosticCodes.ComponentImportOutsideComponent.Code, diagnostic.Code)
        Assert.Contains("'area' imports 'area' from acme:geometry/shapes@1.0.0", diagnostic.Message)
    | Ok _ -> Assert.Fail "Expected an error"
