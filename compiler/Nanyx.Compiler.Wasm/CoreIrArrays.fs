/// The functions that make an array from a list and a list from an array (see `Arrays`), one of each for each type of
/// element a module makes or reads arrays of. An array is a block of its length, then its elements one after another
/// (see `CoreIrLayout.arrayElementOffset`), made by `newCell`, so its count is just before it. The length and the
/// element at an index are read where they are needed (see `CoreIrEmit`).
module Transpiler.Wasm.CoreIrArrays

open NyxCompiler
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.WatText

/// The name of the function that makes an array of `element`s from a list of them.
let ofListName (element: IrType) = Intrinsics.arrayOfList + "_" + sanitizeIdentifier (IrType.mangle element)

/// The name of the function that makes a list of an array of `element`s' elements.
let toListName (element: IrType) = Intrinsics.arrayToList + "_" + sanitizeIdentifier (IrType.mangle element)

/// The element types of the arrays the functions of a module make from lists and turn into lists, each once, in the
/// order they are first needed.
let collect (functions: Function list) : IrType list =
    let operands (intrinsic: string) = CoreIrEquality.collectFor intrinsic (fun _ -> true) (fun _ -> []) functions
    [ for ty in operands Intrinsics.arrayOfList @ operands Intrinsics.arrayToList do
        match CoreIrRegions.arrayIn ty with
        | IrList element
        | IrArray element -> yield element
        | other -> internalError $"an array made from, or turned into, '{IrType.show other}'" ]
    |> List.distinct

/// The runtime helpers the functions of `elements` call.
let helpersUsed (elements: IrType list) : string list =
    [ if not elements.IsEmpty then Helpers.newCell
      for element in elements do
          Helpers.forStorage Helpers.listCons (CoreIrLayout.storage element) ]

/// The code that takes one more reference to the element on the stack and leaves it, when it is on the heap.
let private dup (element: IrType) =
    if CoreIrDrop.isHeapType element then [ $"call ${CoreIrDrop.dupName element}" ] else []

/// `$xs`'s elements, which it borrows, copied into a new array, each with a reference of the array's own. It counts
/// the list's cells first, to make the block once.
let private ofListFunction (element: IrType) =
    let storage = CoreIrLayout.storage element
    let size = storage.Bytes
    let first = CoreIrLayout.arrayElementOffset element 0
    lines
        [ yield $"(func ${ofListName element} (param $xs i32) (result i32)"
          yield "  (local $count i32)"
          yield "  (local $cell i32)"
          yield "  (local $array i32)"
          yield "  (local $at i32)"
          yield "  local.get $xs"
          yield "  local.set $cell"
          yield "  block $counted"
          yield "    loop $count"
          yield "      local.get $cell"
          yield "      i32.eqz"
          yield "      br_if $counted"
          yield "      local.get $count"
          yield "      i32.const 1"
          yield "      i32.add"
          yield "      local.set $count"
          yield "      local.get $cell"
          yield $"      i32.load offset={CoreIrLayout.tailOffset element}"
          yield "      local.set $cell"
          yield "      br $count"
          yield "    end"
          yield "  end"
          yield "  local.get $count"
          yield $"  i32.const {size}"
          yield "  i32.mul"
          yield $"  i32.const {first}"
          yield "  i32.add"
          yield $"  call ${Helpers.newCell}"
          yield "  local.tee $array"
          yield "  local.get $count"
          yield $"  i32.store offset={CoreIrLayout.arrayLengthOffset}"
          yield "  local.get $array"
          yield $"  i32.const {first}"
          yield "  i32.add"
          yield "  local.set $at"
          yield "  local.get $xs"
          yield "  local.set $cell"
          yield "  block $copied"
          yield "    loop $copy"
          yield "      local.get $cell"
          yield "      i32.eqz"
          yield "      br_if $copied"
          yield "      local.get $at"
          yield "      local.get $cell"
          yield $"      {storage.CopyLoad}"
          yield! dup element |> List.map (fun line -> "      " + line)
          yield $"      {storage.Store}"
          yield "      local.get $at"
          yield $"      i32.const {size}"
          yield "      i32.add"
          yield "      local.set $at"
          yield "      local.get $cell"
          yield $"      i32.load offset={CoreIrLayout.tailOffset element}"
          yield "      local.set $cell"
          yield "      br $copy"
          yield "    end"
          yield "  end"
          yield "  local.get $array"
          yield ")" ]

/// `$array`'s elements, which it borrows, as a new list, each with a reference of the list's own. It makes the cells
/// from the last element back, so that each is made with its tail.
let private toListFunction (element: IrType) =
    let storage = CoreIrLayout.storage element
    let size = storage.Bytes
    lines
        [ yield $"(func ${toListName element} (param $array i32) (result i32)"
          yield "  (local $list i32)"
          yield "  (local $at i32)"
          yield "  (local $first i32)"
          yield "  local.get $array"
          yield $"  i32.const {CoreIrLayout.arrayElementOffset element 0}"
          yield "  i32.add"
          yield "  local.tee $first"
          yield "  local.get $array"
          yield $"  i32.load offset={CoreIrLayout.arrayLengthOffset}"
          yield $"  i32.const {size}"
          yield "  i32.mul"
          yield "  i32.add"
          yield "  local.set $at"
          yield "  block $done"
          yield "    loop $next"
          yield "      local.get $at"
          yield "      local.get $first"
          yield "      i32.le_u"
          yield "      br_if $done"
          yield "      local.get $at"
          yield $"      i32.const {size}"
          yield "      i32.sub"
          yield "      local.tee $at"
          yield $"      {storage.CopyLoad}"
          yield! dup element |> List.map (fun line -> "      " + line)
          yield "      local.get $list"
          yield $"      call ${Helpers.forStorage Helpers.listCons storage}"
          yield "      local.set $list"
          yield "      br $next"
          yield "    end"
          yield "  end"
          yield "  local.get $list"
          yield ")" ]

/// The functions of `elements`, as WebAssembly text: for each type of element, the one that makes an array from a list
/// and the one that makes a list from an array.
let render (elements: IrType list) : string list =
    elements |> List.collect (fun element -> [ ofListFunction element; toListFunction element ])
