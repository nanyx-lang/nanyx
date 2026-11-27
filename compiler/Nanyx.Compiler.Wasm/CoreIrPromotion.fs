/// Promotion (#2376): a value stored where it may outlive the `memory` block it was made in, into a mutable object of an
/// outer block or as a block's result, first moves each mutable object it holds that is on an inner region onto the
/// region it is stored into (see `WasmRegions`). So a mutable object can only reach objects on its own region or an
/// outer one, and when a block ends, nothing the program can still reach refers to what is left on its region.
///
/// Each type that can hold a mutable object (`CoreIrRegions.canHoldMutable`) has a promote function, which takes a value
/// and a region and follows the value's structure, as its drop function does. A box it moves, it follows into; one
/// already on the region or an outer one it leaves, which also ends a walk around a cycle. A function value, or a boxed
/// value, whose captures it can't follow, merges every region inside the target onto it instead.
module internal Transpiler.Wasm.CoreIrPromotion

open System.Collections.Generic
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.Wat
open Transpiler.Wasm.WatText

/// The runtime helpers a store, and the result of a block, are lowered to (see `promoting`).
let private storing (name: string) =
    name = WasmRegions.Names.regionStore || name = WasmRegions.Names.regionStoreOutermost || name = WasmRegions.Names.regionExit

/// The value a store or a block's result promotes: the last operand.
let private promoted (name: string) (args: Atom list) =
    if storing name then List.tryLast args |> Option.map atomType |> Option.filter CoreIrRegions.canHoldMutable else None

/// The types `functions` promote, with the types inside them that can hold a mutable object, each once, in the order
/// they are first met.
let collect (functions: Function list) : IrType list =
    let types = ResizeArray<IrType>()
    let seen = HashSet<IrType>()
    let rec add (ty: IrType) =
        if CoreIrRegions.canHoldMutable ty && seen.Add ty then
            types.Add ty
            match IrType.unfold ty with
            | CoreIrRegions.RegionBox held -> add held
            | IrRecord fields -> fields |> List.iter (snd >> add)
            | IrTags tags -> tags |> List.iter (snd >> Option.iter add)
            | IrList element
            | IrArray element -> add element
            | _ -> ()
    // The expressions inside one, the last in tail position, so that a chain as long as a function takes no stack to
    // walk (as `CoreIrDrop.collect` walks them).
    let rec expr (e: Expr) =
        match e.Kind with
        | CallRuntime(name, args, _) -> promoted name args |> Option.iter add
        | _ -> ()
        match Expr.children e with
        | [] -> ()
        | children ->
            for child in List.take (children.Length - 1) children do
                expr child
            expr (List.last children)
    for func in functions do
        expr func.Body
    List.ofSeq types

/// The code that promotes the value `load` leaves on the stack, of type `ty`, onto the region in `$t`.
let private promoteValue (ty: IrType) (load: string list) =
    if CoreIrRegions.canHoldMutable ty then load @ [ "local.get $t"; $"call ${CoreIrRegions.promoteName ty}" ] else []

/// The body of the promote function of `ty`, which takes the value in `$p` and the region in `$t`.
let private promoteBody (tagId: string -> int option) (ty: IrType) : string list =
    match IrType.unfold ty with
    | CoreIrRegions.RegionBox held as box ->
        let fields = match box with IrRecord fields -> fields | _ -> []
        let offset = CoreIrLayout.fieldOffset fields CoreIrRegions.boxValueSlot
        [ "local.get $p"
          "local.get $t"
          $"call ${WasmRegions.Names.regionMove}"
          "if"
          yield! promoteValue held [ "local.get $p"; CoreIrLayout.load held offset ] |> List.map (fun line -> "  " + line)
          "end" ]
    | IrRecord fields ->
        fields
        |> List.mapi (fun slot (_, fieldTy) -> promoteValue fieldTy [ "local.get $p"; CoreIrLayout.load fieldTy (CoreIrLayout.fieldOffset fields slot) ])
        |> List.concat
    | IrTags tags ->
        // A code holds nothing. A cell holds the code, then the payload.
        let payload (payloadTy: IrType) = promoteValue payloadTy [ "local.get $p"; CoreIrLayout.load IrWord CoreIrLayout.payloadOffset ]
        [ "local.get $p"
          "i32.const 1"
          "i32.and"
          "if"
          "  return"
          "end"
          yield!
              tags
              |> List.choose (fun (name, payloadTy) ->
                  match payloadTy, tagId name with
                  | Some payloadTy, Some id when CoreIrRegions.canHoldMutable payloadTy -> Some (id, payloadTy)
                  | _ -> None)
              |> List.collect (fun (id, payloadTy) ->
                  [ "local.get $p"
                    "i32.load"
                    $"i32.const {tagCode id}"
                    "i32.eq"
                    "if"
                    yield! payload payloadTy |> List.map (fun line -> "  " + line)
                    "end" ]) ]
    | IrList element ->
        // Cell by cell along the list, so that a long list takes no stack.
        [ "block $done"
          "  loop $next"
          "    local.get $p"
          "    i32.eqz"
          "    br_if $done"
          yield! promoteValue element [ "local.get $p"; "i32.load" ] |> List.map (fun line -> "    " + line)
          "    local.get $p"
          $"    i32.load offset={CoreIrLayout.tailOffset element}"
          "    local.set $p"
          "    br $next"
          "  end"
          "end" ]
    | IrArray element ->
        let size = CoreIrLayout.size element
        [ "(local $at i32)"
          "(local $end i32)"
          "local.get $p"
          $"i32.const {CoreIrLayout.arrayElementOffset element 0}"
          "i32.add"
          "local.tee $at"
          "local.get $p"
          $"i32.load offset={CoreIrLayout.arrayLengthOffset}"
          $"i32.const {size}"
          "i32.mul"
          "i32.add"
          "local.set $end"
          "block $done"
          "  loop $next"
          "    local.get $at"
          "    local.get $end"
          "    i32.ge_u"
          "    br_if $done"
          yield! promoteValue element [ "local.get $at"; CoreIrLayout.load element 0 ] |> List.map (fun line -> "    " + line)
          "    local.get $at"
          $"    i32.const {size}"
          "    i32.add"
          "    local.set $at"
          "    br $next"
          "  end"
          "end" ]
    // What a closure or a boxed value holds isn't known here: every region inside the target joins it.
    | IrFunction _
    | IrBox -> [ "local.get $t"; $"call ${WasmRegions.Names.regionMerge}" ]
    | other -> internalError $"a promote function for '{IrType.show other}', which holds no mutable object"


/// The promote function of each of `types`, as WebAssembly text. `tagId` gives the number of each tag the module numbers;
/// a tag it does not is never made.
let render (tagId: string -> int option) (types: IrType list) : string list =
    types
    |> List.map (fun ty ->
        lines [
            $"(func ${CoreIrRegions.promoteName ty} (param $p i32) (param $t i32)"
            $"  ;; {IrType.show ty}"
            yield! promoteBody tagId ty |> List.map (fun line -> "  " + line)
            ")" ])

/// The runtime helpers the promote functions of `types` call.
let helpersUsed (types: IrType list) : string list =
    [ if types |> List.exists (IrType.unfold >> function CoreIrRegions.RegionBox _ -> true | _ -> false) then WasmRegions.Names.regionMove
      if types |> List.exists (IrType.unfold >> function IrFunction _ | IrBox -> true | _ -> false) then WasmRegions.Names.regionMerge ]
