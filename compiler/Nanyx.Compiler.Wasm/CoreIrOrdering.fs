/// The ordering functions of a module: for each type the compiler's own `Ord` puts in order (see
/// `WellKnownContexts.ordering`), one that compares two values of it by their structure and leaves a negative number
/// when the first comes first, 0 when they are equal and a positive one when it comes after. Numbers and bools are
/// ordered numerically (`false` first), strings by their bytes, tuples position by position and lists element by
/// element, a list that is a prefix of the other first. Floats are in a total order, with NaN after every other float:
/// it is `<` on floats, not this, that follows IEEE 754. Records and tags have no order. A record only reaches here
/// inside a tuple, or a list the operator's type did not show was one where it is written, that a function requiring
/// `Ord(a)` orders by its structure rather than through the instance (see the equality design page's `What is
/// implemented`): it is then ordered field by field, in the order the fields are stored. Tags never reach here. An
/// ordering function only reads
/// its operands: it borrows them, as every runtime helper does.
module Transpiler.Wasm.CoreIrOrdering

open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr

/// The name of the ordering function of `ty`.
let compareName (ty: IrType) = "__nanyx_compare_" + sanitizeIdentifier (IrType.mangle ty)

/// The parts of a value of `ty` that are ordered, in the order they are compared, each with where it is stored: a
/// tuple's positions in order (its fields are stored by name, so `10` is before `2`), a list's element.
let private orderedParts (ty: IrType) : (int * IrType) list =
    match ty with
    | IrRecord fields ->
        fields
        |> List.mapi (fun slot (name, fieldTy) -> slot, name, fieldTy)
        |> List.sortBy (fun (slot, name, _) -> match System.Int32.TryParse name with | true, position -> position, slot | _ -> System.Int32.MaxValue, slot)
        |> List.map (fun (slot, _, fieldTy) -> slot, fieldTy)
    | IrList element -> [ 0, element ]
    | _ -> []

/// The types whose ordering functions the functions of a module call, in the order they are first needed: each type
/// the ordering intrinsic or an operator orders, and the parts of those types.
let collect (functions: Function list) : IrType list =
    CoreIrEquality.collectFor Helpers.valueCompare (fun _ -> true) (orderedParts >> List.map snd) functions

/// Leaves `a` and `b`'s order, as -1, 0 or 1, from the comparisons `greater` and `less` each leave 1 for.
let private sign (greater: string list) (less: string list) = greater @ less @ [ "i32.sub" ]

/// Returns from the function the order the code before it left, in `$order`, unless it is 0.
let private orReturnOrder = [ "local.tee $order"; "if"; "  local.get $order"; "  return"; "end" ]

let private indent (lines: string list) = lines |> List.map (fun line -> "  " + line)

/// The locals the ordering function of `ty` needs, and its body, which orders `$a` and `$b`.
let private compareBody (ty: IrType) : string list * string list =
    let both instruction = [ "local.get $a"; "local.get $b"; instruction ]
    match ty with
    | IrInt -> [], sign (both "i64.gt_s") (both "i64.lt_s")
    | IrSized integer ->
        let valueType = CoreIrLayout.valueType ty
        let signed = if NyxCompiler.SizedInteger.isSigned integer then "s" else "u"
        [], sign (both $"{valueType}.gt_{signed}") (both $"{valueType}.lt_{signed}")
    | IrBool
    | IrUnit -> [], sign (both "i32.gt_s") (both "i32.lt_s")
    | IrFloat
    | IrF32 ->
        // An `F32` is ordered as a float is, as an `f32`.
        let width = if ty = IrF32 then "f32" else "f64"
        let isNaN var = [ $"local.get ${var}"; $"local.get ${var}"; $"{width}.ne" ]
        let floats instruction = [ "local.get $x"; "local.get $y"; instruction ]
        [ $"(local $x {width})"; $"(local $y {width})" ],
        [ "local.get $a"
          $"{width}.load"
          "local.set $x"
          "local.get $b"
          $"{width}.load"
          "local.set $y"
          // NaN comes after every other float, and is equal to itself.
          yield! isNaN "x"
          "if"
          yield! isNaN "y" |> indent
          "  if"
          "    i32.const 0"
          "    return"
          "  end"
          "  i32.const 1"
          "  return"
          "end"
          yield! isNaN "y"
          "if"
          "  i32.const -1"
          "  return"
          "end"
          yield! sign (floats $"{width}.gt") (floats $"{width}.lt") ]
    | IrString ->
        // Byte by byte, unsigned, which is the order of the code points UTF-8 encodes; a prefix comes first.
        let length var = [ $"local.get ${var}"; $"call ${Helpers.stringLength}"; $"local.set $len{var}" ]
        let byte var = [ $"local.get ${var}"; "local.get $i"; "i32.add"; "i32.load8_u" ]
        [ "(local $lena i32)"; "(local $lenb i32)"; "(local $i i32)"; "(local $order i32)" ],
        [ yield! length "a"
          yield! length "b"
          "block $done"
          "  loop $next"
          "    local.get $i"
          "    local.get $lena"
          "    i32.ge_s"
          "    local.get $i"
          "    local.get $lenb"
          "    i32.ge_s"
          "    i32.or"
          "    br_if $done"
          yield! sign (byte "a" @ byte "b" @ [ "i32.gt_u" ]) (byte "a" @ byte "b" @ [ "i32.lt_u" ]) @ orReturnOrder |> indent |> indent
          "    local.get $i"
          "    i32.const 1"
          "    i32.add"
          "    local.set $i"
          "    br $next"
          "  end"
          "end"
          yield! sign [ "local.get $lena"; "local.get $lenb"; "i32.gt_s" ] [ "local.get $lena"; "local.get $lenb"; "i32.lt_s" ] ]
    | IrRecord fields ->
        [ "(local $order i32)" ],
        [ yield!
              orderedParts ty
              |> List.collect (fun (slot, fieldTy) ->
                  let field = CoreIrLayout.load fieldTy (CoreIrLayout.fieldOffset fields slot)
                  [ yield! [ "local.get $a"; field; "local.get $b"; field ]
                    $"call ${compareName fieldTy}"
                    yield! orReturnOrder ])
          "i32.const 0" ]
    | IrList element ->
        // Cell by cell along both lists, so that a long list takes no stack. The empty list is 0.
        [ "(local $order i32)" ],
        [ "block $done"
          "  loop $next"
          "    local.get $a"
          "    i32.eqz"
          "    local.get $b"
          "    i32.eqz"
          "    i32.or"
          "    br_if $done"
          "    local.get $a"
          $"    {CoreIrLayout.load element 0}"
          "    local.get $b"
          $"    {CoreIrLayout.load element 0}"
          $"    call ${compareName element}"
          yield! orReturnOrder |> indent |> indent
          "    local.get $a"
          $"    i32.load offset={CoreIrLayout.tailOffset element}"
          "    local.set $a"
          "    local.get $b"
          $"    i32.load offset={CoreIrLayout.tailOffset element}"
          "    local.set $b"
          "    br $next"
          "  end"
          "end"
          // One list ran out: it comes first, unless both did.
          yield! sign [ "local.get $a"; "i32.const 0"; "i32.ne" ] [ "local.get $b"; "i32.const 0"; "i32.ne" ] ]
    | IrTags _
    | IrFunction _
    | IrError
    | IrRecursive _
    | IrSelf
    | IrWord -> internalError $"an ordering function for {IrType.show ty}, which has no order"

/// The runtime helpers the ordering functions of `types` call.
let helpersUsed (types: IrType list) : string list =
    [ if types |> List.contains IrString then Helpers.stringLength ]

/// The ordering functions of `types`, as WebAssembly text.
let render (types: IrType list) : string list =
    types
    |> List.map (fun ty ->
        let locals, body = compareBody ty
        let valueType = CoreIrLayout.valueType ty
        [ $"(func ${compareName ty} (param $a {valueType}) (param $b {valueType}) (result i32)"
          $"  ;; {IrType.show ty}"
          yield! indent locals
          yield! indent body
          ")" ]
        |> indent
        |> String.concat "\n")
