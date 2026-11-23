/// What the emitter (see `CoreIrEmit`) knows of the intrinsics and runtime helpers it writes out where they are used:
/// which instructions each is, and what its operands are taken as.
module internal Transpiler.Wasm.CoreIrEmitIntrinsics

open NyxCompiler
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr

/// The helpers whose parameters and results of type int are `i64`s. The others take and give `i32`s, which the
/// emitter converts an int to and from.
let takesInts = set [ Helpers.intToFloat; Helpers.floatToInt ]

/// The intrinsics that add, subtract and multiply integers wrapping around, each with its instruction's operation.
let wrappingOperations =
    Map.ofList [ Intrinsics.wrappingAdd, "add"; Intrinsics.wrappingSub, "sub"; Intrinsics.wrappingMul, "mul" ]

/// The bitwise intrinsics on ints, each with the instructions it is after its arguments. A shift takes its count
/// modulo 64, as `i64.shl` and the others do.
let bitwiseOperations =
    Map.ofList
        [ Intrinsics.bitAnd, [ "i64.and" ]
          Intrinsics.bitOr, [ "i64.or" ]
          Intrinsics.bitXor, [ "i64.xor" ]
          Intrinsics.bitNot, [ "i64.const -1"; "i64.xor" ]
          Intrinsics.shiftLeft, [ "i64.shl" ]
          Intrinsics.shiftRight, [ "i64.shr_s" ]
          Intrinsics.shiftRightUnsigned, [ "i64.shr_u" ] ]

/// The intrinsics that read and write linear memory at an address (see `nanyx/host.memory`), each with the instruction
/// that does it after the address and the value written, and whether it writes, leaving unit.
let memoryAccesses =
    Map.ofList
        [ Intrinsics.memoryReadByte, ("i32.load8_u", false)
          Intrinsics.memoryWriteByte, ("i32.store8", true)
          Intrinsics.memoryReadI32, ("i32.load", false)
          Intrinsics.memoryWriteI32, ("i32.store", true)
          Intrinsics.memoryReadI64, ("i64.load", false)
          Intrinsics.memoryWriteI64, ("i64.store", true) ]

/// The integer type of a value of `ty`, as `SizedIntegerCode` takes it: a sized integer, or `None` for an int.
let integerType (ty: IrType) =
    match ty with
    | IrSized integer -> Some integer
    | IrInt -> None
    | other -> internalError $"a conversion of '{IrType.show other}', which is not an integer"

/// The type of the elements of an array of `ty`.
let arrayElement (ty: IrType) =
    match ty with
    | IrArray element -> element
    | other -> internalError $"an array intrinsic on '{IrType.show other}', which is not an array"

/// The intrinsics on arrays, which `FunctionEmitter.ArrayIntrinsic` writes out where they are used.
let arrayIntrinsics =
    set [
        Intrinsics.arrayLength
        Intrinsics.marraySet
        Intrinsics.arrayWrite
        Intrinsics.arrayNew
        Intrinsics.arrayGet
        Intrinsics.arrayOfList
        Intrinsics.arrayToList ]

/// The operators that give a number, where the others on numbers give a bool.
let arithmetic = set [ "+"; "-"; "*"; "/"; "%" ]

/// The code that converts a value of `from` on the stack to one of `into`, when they are held differently: only a
/// value of no known type (see `IrError`) is held differently from a value it is taken as. It is an `i32`, and an int
/// that becomes one keeps its low 32 bits. A float or an `F32` and such a value have nothing to keep of each other: each
/// is 0 as the other.
let conversion (from: IrType) (into: IrType) =
    match CoreIrLayout.valueType from, CoreIrLayout.valueType into with
    | fromType, intoType when fromType = intoType -> ""
    | ("f64" | "f32"), intoType
    | _, ("f64" | "f32" as intoType) -> $"drop\n    {intoType}.const 0"
    | "i64", _ -> "i32.wrap_i64"
    | _ -> "i64.extend_i32_s"

/// The helpers that take an int as an index into a string, which they are given clamped (see `Helpers.intToIndex`).
/// Any other helper is given an int's low 32 bits.
let takesIndices = set [ Helpers.stringByteAt; Helpers.stringSlice ]
