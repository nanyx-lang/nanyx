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

let private compoundSource =
    """type Point = (x: int, y: float)

type Shape = #circle(float) | #rect(Point) | #empty

type Color = #red | #green | #blue

type Named = (name: string, tags: list(string))

@component
export def move: Point, int -> Point = { p, dx -> (x = p.x + dx, y = p.y * 2.0) }

@component
export def area: Shape -> float = { s ->
  match s
    | #circle(r) -> r * r
    | #rect(p) -> I64.toFloat(p.x) * p.y
    | #empty -> 0.0
}

@component
export def next: Color -> Color = { c ->
  match c
    | #red -> #green
    | #green -> #blue
    | #blue -> #red
}

@component
export def describe: Named -> Named = { n -> (name = n.name ++ "!", tags = n.tags ++ [ n.name ]) }

@component
export def sum: list(int) -> int = { xs -> list.fold(xs, 0, { acc, x -> acc + x }) }

@component
export def upTo: int -> list(int) = { n -> [ n, n + 1, n + 2 ] }

@component
export def swap: (int, string) -> (string, int) = { pair ->
  match pair
    | (n, s) -> (s, n)
}

@component
export def wrap: float -> Shape = { r ->
  match r > 0.0
    | true -> #circle(r)
    | false -> #empty
}

@component
export def many: int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int -> int = { a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q ->
  a + q
}
"""

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Records, tuples, lists and variants cross as the canonical ABI lays them out, and leave nothing on the heap`` (checkMemory: bool) =
    let typed =
        match Compiler.compile (normalized compoundSource) with
        | { Typed = Some typed; Diagnostics = [] } -> typed
        | result -> failwith $"%A{result.Diagnostics}"
    let exports = ComponentInterface.functions typed |> List.map (fun func -> ComponentInterface.exportName package func.Export, func)
    let options = { WasmOptions.Default with Component = Some CliRun; ComponentExports = exports; CheckMemory = checkMemory }
    let engine, store, instance = instantiateCore (transpileTypedModuleWithOptions options typed |> fst)
    use _ = engine
    use _ = store
    let names = exports |> List.map (fun (name, func) -> func.Export.Name, name) |> Map.ofList
    let memory = instance.GetMemory "memory"
    let realloc = instance.GetFunction<int, int, int, int, int>("cabi_realloc")
    let post (name: string) (area: int) = instance.GetAction<int>("cabi_post_" + names.[name]).Invoke(area)
    let liveBlocks () = if checkMemory then instance.GetGlobal("live_blocks").GetValue() :?> int else 0
    // As the host does, a string in a block of its own, at its address and length.
    let pass (text: string) =
        let bytes = Encoding.UTF8.GetBytes text
        let address = realloc.Invoke(0, 0, 1, max bytes.Length 1)
        bytes |> Array.iteri (fun index byte -> memory.WriteByte(int64 (address + index), byte))
        address, bytes.Length
    let readString (at: int) =
        let address = memory.ReadInt32(int64 at)
        let length = memory.ReadInt32(int64 at + 4L)
        Encoding.UTF8.GetString(Array.init length (fun index -> memory.ReadByte(int64 (address + index))))
    let before = liveBlocks ()

    // A record, passed as its fields' values, returned in memory: `x` at 0 and `y` at 8.
    let moved = instance.GetFunction<int64, double, int64, int>(names.["move"]).Invoke(3L, 1.5, 4L)
    Assert.Equal(7L, memory.ReadInt64(int64 moved))
    Assert.Equal(3.0, memory.ReadDouble(int64 moved + 8L))
    post "move" moved

    // A variant, passed as its discriminant and its payload's values in the places its cases share: an f64 in an i64.
    let area = instance.GetFunction<int, int64, double, double>(names.["area"])
    Assert.Equal(4.0, area.Invoke(0, System.BitConverter.DoubleToInt64Bits 2.0, 0.0))
    Assert.Equal(7.5, area.Invoke(1, 3L, 2.5))
    Assert.Equal(0.0, area.Invoke(2, 0L, 0.0))

    // A variant of no payloads is its discriminant alone, both ways.
    let next = instance.GetFunction<int, int>(names.["next"])
    Assert.Equal<int list>([ 1; 2; 0 ], [ next.Invoke 0; next.Invoke 1; next.Invoke 2 ])

    // A record of a string and a list of strings, each in blocks of its own.
    let name, nameLength = pass "ada"
    let tags = realloc.Invoke(0, 0, 4, 16)
    for index, tag in List.indexed [ "x"; "yz" ] do
        let address, length = pass tag
        memory.WriteInt32(int64 (tags + index * 8), address)
        memory.WriteInt32(int64 (tags + index * 8 + 4), length)
    let described = instance.GetFunction<int, int, int, int, int>(names.["describe"]).Invoke(name, nameLength, tags, 2)
    Assert.Equal("ada!", readString described)
    let tagsAddress = memory.ReadInt32(int64 described + 8L)
    Assert.Equal(3, memory.ReadInt32(int64 described + 12L))
    Assert.Equal<string list>([ "x"; "yz"; "ada" ], [ for index in 0..2 -> readString (tagsAddress + index * 8) ])
    post "describe" described

    // A list of numbers, both ways, and an empty one.
    let numbers = realloc.Invoke(0, 0, 8, 24)
    for index, value in List.indexed [ 5L; 6L; 7L ] do
        memory.WriteInt64(int64 (numbers + index * 8), value)
    Assert.Equal(18L, instance.GetFunction<int, int, int64>(names.["sum"]).Invoke(numbers, 3))
    Assert.Equal(0L, instance.GetFunction<int, int, int64>(names.["sum"]).Invoke(0, 0))
    let upTo = instance.GetFunction<int64, int>(names.["upTo"]).Invoke(10L)
    let items = memory.ReadInt32(int64 upTo)
    Assert.Equal(3, memory.ReadInt32(int64 upTo + 4L))
    Assert.Equal<int64 list>([ 10L; 11L; 12L ], [ for index in 0..2 -> memory.ReadInt64(int64 (items + index * 8)) ])
    post "upTo" upTo

    // A tuple, passed as its items' values, returned in memory: the string at 0 and the int at 8.
    let text, textLength = pass "hi"
    let swapped = instance.GetFunction<int64, int, int, int>(names.["swap"]).Invoke(42L, text, textLength)
    Assert.Equal("hi", readString swapped)
    Assert.Equal(42L, memory.ReadInt64(int64 swapped + 8L))
    post "swap" swapped

    // A variant returned in memory: its discriminant at 0, and its payload at 8, after it, aligned.
    let wrap = instance.GetFunction<double, int>(names.["wrap"])
    let circle = wrap.Invoke 1.25
    Assert.Equal(0uy, memory.ReadByte(int64 circle))
    Assert.Equal(1.25, memory.ReadDouble(int64 circle + 8L))
    post "wrap" circle
    let empty = wrap.Invoke -1.0
    Assert.Equal(2uy, memory.ReadByte(int64 empty))
    post "wrap" empty

    // More than 16 values of parameters come in a block of their own.
    let block = realloc.Invoke(0, 0, 8, 17 * 8)
    for index in 0..16 do
        memory.WriteInt64(int64 (block + index * 8), int64 (index + 1))
    Assert.Equal(18L, instance.GetFunction<int, int64>(names.["many"]).Invoke(block))

    Assert.Equal(before, liveBlocks ())

[<Fact>]
let ``The componentizer accepts the WIT of records, tuples, lists and variants with the core module's exports of them`` () =
    let typed =
        match Compiler.compile (normalized compoundSource) with
        | { Typed = Some typed } -> typed
        | result -> failwith $"%A{result.Diagnostics}"
    let functions = ComponentInterface.functions typed
    let exports = functions |> List.map (fun func -> ComponentInterface.exportName package func.Export, func)
    let wat = transpileTypedModuleWithOptions { WasmOptions.Default with Component = Some CliRun; ComponentExports = exports } typed |> fst
    use target = new TempDirectory()
    let hostWit = System.IO.Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "wit")
    let world = ComponentInterface.write hostWit "nanyx:console/command" package functions target.Path
    match Nanyx.Runtime.Componentizer.componentize (WasmBinary.assemble wat) target.Path world with
    | Ok _ -> ()
    | Error message -> Assert.Fail message
