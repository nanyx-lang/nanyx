module TranspilerWasmTests.Features.Memory.DropFunctions

open System
open Wasmtime
open Xunit
open Transpiler.Wasm
open Transpiler.Wasm.CoreIr
open TranspilerWasmTestHelpers

/// A program compiled and instantiated on Wasmtime, with the drop functions of `dropped` exported as well, so that a
/// test can give up a value its functions made. Nothing in compiled code calls a drop function yet.
type private Program(engine: Engine, store: Store, instance: Instance) =
    let memory = instance.GetMemory("memory")
    member _.Call(name: string, argument: int) = instance.GetFunction<int, int>(name).Invoke argument
    member _.Drop(ty: IrType, value: int) = instance.GetAction<int>(CoreIrDrop.dropName ty).Invoke value
    member _.HeapPointer = instance.GetGlobal("heap_ptr").GetValue() :?> int
    member _.Word(address: int) = memory.ReadInt32(int64 address)
    member _.Text(pointer: int) = memory.ReadString(int64 pointer, memory.ReadInt32(int64 (pointer - 4)))
    interface IDisposable with
        member _.Dispose() =
            store.Dispose()
            engine.Dispose()

let private instantiate (source: string) (dropped: IrType list) =
    let wat = transpileWat source
    let exports =
        [ yield! dropped |> List.map (fun ty -> $"  (export \"{CoreIrDrop.dropName ty}\" (func ${CoreIrDrop.dropName ty}))")
          if wat.Contains "(global $heap_ptr" then "  (export \"heap_ptr\" (global $heap_ptr))" ]
        |> String.concat "\n"
    let engine = new Engine()
    let store = new Store(engine)
    use module' = Module.FromText(engine, "program", wat.Substring(0, wat.LastIndexOf ')') + exports + "\n)")
    use linker = new Linker(engine)
    new Program(engine, store, linker.Instantiate(store, module'))

/// Builds a value with `make`, drops it, and builds it again: when the drop freed every object, the second build takes
/// all its memory from the free lists and the heap does not grow.
let private assertDropFreesEverything (program: Program) (make: string) (ty: IrType) =
    program.Drop(ty, program.Call(make, 20))
    let heapAfterFirst = program.HeapPointer
    program.Drop(ty, program.Call(make, 20))
    Assert.Equal(heapAfterFirst, program.HeapPointer)

[<Fact>]
let ``Drop - a list of strings frees every cell and every string`` () =
    let source = "def build: int -> list(string) = { n -> [\"a {n}\", \"b {n}\", \"c {n}\"] }\n"
    use program = instantiate source [ IrList IrString ]
    assertDropFreesEverything program "build" (IrList IrString)

[<Fact>]
let ``Drop - a record frees the strings in its fields`` () =
    let source = "def pair: int -> (string, int) = { n -> (\"pair {n}\", n) }\n"
    let ty = IrRecord [ "0", IrString; "1", IrInt ]
    use program = instantiate source [ ty ]
    assertDropFreesEverything program "pair" ty

[<Fact>]
let ``Drop - a tag's cell is freed with its payload, and a tag without one is left alone`` () =
    let source =
        "def wrap: int -> #some(string) | #none = { n ->\n" +
        "  match n\n" +
        "  | 0 -> #none\n" +
        "  | _ -> #some(\"wrapped {n}\")\n" +
        "}\n"
    let ty = IrTags [ "none", None; "some", Some IrString ]
    use program = instantiate source [ ty ]
    assertDropFreesEverything program "wrap" ty
    program.Drop(ty, program.Call("wrap", 0))

[<Fact>]
let ``Drop - a tag's payload is dropped by the type its code gives it`` () =
    let source =
        "def describe: int -> #name(string) | #age(int) = { n ->\n" +
        "  match n\n" +
        "  | 0 -> #age(n)\n" +
        "  | _ -> #name(\"name {n}\")\n" +
        "}\n"
    let ty = IrTags [ "age", Some IrInt; "name", Some IrString ]
    use program = instantiate source [ ty ]
    assertDropFreesEverything program "describe" ty
    let heap = program.HeapPointer
    program.Drop(ty, program.Call("describe", 0))
    program.Drop(ty, program.Call("describe", 0))
    Assert.Equal(heap, program.HeapPointer)

[<Fact>]
let ``Drop - a closure frees what it captures`` () =
    let source =
        "def prefixer: int -> (string -> string) = { n ->\n" +
        "  def prefix = \"{n}: \"\n" +
        "  { s -> prefix ++ s }\n" +
        "}\n"
    let ty = IrFunction(IrString, IrString)
    use program = instantiate source [ ty ]
    assertDropFreesEverything program "prefixer" ty

[<Fact>]
let ``Drop - a string literal is never freed`` () =
    let source = "def greeting: int -> string = { _n -> \"hello\" }\n"
    use program = instantiate source [ IrString ]
    let literal = program.Call("greeting", 0)
    program.Drop(IrString, literal)
    program.Drop(IrString, literal)
    Assert.Equal(0, program.Word(literal - 8))
    Assert.Equal("hello", program.Text literal)
