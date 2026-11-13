/// How the values of the core IR are held in WebAssembly (see "Value representation" in
/// `docs/src/content/work-in-progress/memory-management.md`): the value type each type is held in, in locals and on the
/// stack, and where each part of an object on the heap is. An int and a `U64` are `i64`s, stored in 8 bytes; an `I8` and
/// a `U8` are `i32`s stored in 1 byte, and an `I16` and a `U16` in 2; every other value is an `i32` (a pointer, a code, a
/// bool, a boxed float, an `I32`, a `U32`), stored in 4. Values are laid one after another, unaligned.
module Transpiler.Wasm.CoreIrLayout

open NyxCompiler
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.WasmRuntime

/// The WebAssembly value type a value of `ty` is held in.
let valueType (ty: IrType) =
    match ty with
    | IrInt
    | IrSized SizedInteger.U64 -> "i64"
    | _ -> "i32"

/// How a value of `ty` is written in a field, a payload or a list cell.
let internal storage (ty: IrType) =
    match ty with
    | IrInt
    | IrSized SizedInteger.U64 -> Wide
    | IrSized SizedInteger.I8
    | IrSized SizedInteger.U8 -> Byte
    | IrSized SizedInteger.I16
    | IrSized SizedInteger.U16 -> Half
    | _ -> Word

/// How many bytes a value of `ty` takes in a field, a payload or a list cell.
let size (ty: IrType) = (storage ty).Bytes

let private atOffset (instruction: string) (offset: int) =
    if offset = 0 then instruction else $"{instruction} offset={offset}"

/// The instruction that reads a value of `ty` at `offset` past the address on the stack, sign-extending a narrow signed
/// integer and zero-extending a narrow unsigned one.
let load (ty: IrType) (offset: int) =
    let instruction =
        match ty with
        | IrSized SizedInteger.I8 -> "i32.load8_s"
        | IrSized SizedInteger.I16 -> "i32.load16_s"
        | _ -> (storage ty).CopyLoad
    atOffset instruction offset

/// The instruction that writes a value of `ty` at `offset` past an address, both on the stack.
let store (ty: IrType) (offset: int) = atOffset (storage ty).Store offset

/// Where each of the values of `types` is, laid one after another from 0, each in `size` bytes.
let offsets (types: IrType list) : int list =
    types |> List.scan (fun offset ty -> offset + size ty) 0 |> List.take types.Length

/// How many bytes the values of `types`, laid one after another, take.
let totalSize (types: IrType list) = types |> List.sumBy size

/// Where the field in `slot` of a record of `fields` (in the order they are stored) is, from its pointer.
let fieldOffset (fields: (string * IrType) list) (slot: int) = (offsets (List.map snd fields)).[slot]

/// How many bytes a record of `fields` holds its fields in.
let recordSize (fields: (string * IrType) list) = totalSize (List.map snd fields)

/// Where a list cell's tail is, after its head, an element of `element`.
let tailOffset (element: IrType) = size element

/// How many bytes a list cell of an element of `element` takes: its head, then its tail.
let listCellSize (element: IrType) = size element + 4

/// Where a tag cell's payload is, after its code.
let payloadOffset = tagPayloadOffset

/// How many bytes a tag cell with a payload of `payload` takes: its code, then its payload.
let tagCellSize (payload: IrType) = payloadOffset + size payload

/// How many bytes the header in front of an object of `ty` on the heap takes, which is how far before its pointer its
/// count is: an info word follows the count of a string, a record and a closure (see `Header`).
let headerSize (ty: IrType) =
    match IrType.unfold ty with
    | IrString
    | IrRecord _
    | IrFunction _ -> Header.withInfo
    | _ -> Header.countOnly

/// Where each value a closure block holds is: its function's table slot, then what it captures, of `captured`.
let closureOffsets (captured: IrType list) = offsets (IrWord :: captured)

/// How many bytes a closure block over captures of `captured` takes.
let closureSize (captured: IrType list) = totalSize (IrWord :: captured)
