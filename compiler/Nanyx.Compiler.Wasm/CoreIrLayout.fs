/// How the values of the core IR are held in WebAssembly (see "Value representation" in
/// `docs/src/content/work-in-progress/memory-management.md`): the value type each type is held in, in locals and on the
/// stack, and where each part of an object on the heap is. An int is an `i64`, stored in 8 bytes; every other value is
/// an `i32` (a pointer, a code, a bool, a boxed float), stored in 4.
module Transpiler.Wasm.CoreIrLayout

open Transpiler.Wasm.CoreIr

/// The WebAssembly value type a value of `ty` is held in.
let valueType (ty: IrType) =
    match ty with
    | IrInt -> "i64"
    | _ -> "i32"

/// How many bytes a value of `ty` takes in a field, a payload or a list cell.
let size (ty: IrType) =
    match ty with
    | IrInt -> 8
    | _ -> 4

/// The instruction that reads a value of `ty` at `offset` past the address on the stack.
let load (ty: IrType) (offset: int) =
    if offset = 0 then $"{valueType ty}.load" else $"{valueType ty}.load offset={offset}"

/// The instruction that writes a value of `ty` at `offset` past an address, both on the stack.
let store (ty: IrType) (offset: int) =
    if offset = 0 then $"{valueType ty}.store" else $"{valueType ty}.store offset={offset}"

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
let payloadOffset = 4

/// How many bytes a tag cell with a payload of `payload` takes: its code, then its payload.
let tagCellSize (payload: IrType) = payloadOffset + size payload

/// Where each value a closure block holds is: its function's table slot, then what it captures, of `captured`.
let closureOffsets (captured: IrType list) = offsets (IrWord :: captured)

/// How many bytes a closure block over captures of `captured` takes.
let closureSize (captured: IrType list) = totalSize (IrWord :: captured)
