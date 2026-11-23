/// The mutable objects a `memory` block frees when it ends (#2232, #2376). Each `memory` block running has a region (see
/// `WasmRegions`), and each mutable object made while it runs is on the innermost region's list: counting still frees it
/// as soon as nothing refers to it, and it comes off the list then, and when the block ends it drops what each object
/// still on the list holds and frees it, which breaks any cycle through them. A value stored where it may outlive the
/// block, in an outer block's object or as the block's result, is promoted first: the objects it holds move onto the
/// outer block's region (see `CoreIrPromotion`). The layout and the helpers are described in
/// "Regions" in `docs/src/content/deep-dives/memory-management.md`.
module internal Transpiler.Wasm.CoreIrRegions

open Transpiler.Wasm.CoreIr

/// The fields in front of what a tracked object holds: the table slot of the function that drops what it holds, the
/// next object on its region's list, the region it is on, and the previous object on the list. Their names start with
/// `-`, which no field of the program's can, so they sort before every other field and are stored first, and no record
/// of the program's is taken for one.
let dropField = "-region-drop"
let nextField = "-region-next"
let ownerField = "-region-owner"
let prevField = "-region-prev"

/// The type of a link: a word, which no count follows.
let private linkType = IrSized NyxCompiler.SizedInteger.I32

/// The field a box's value is: after the links.
let boxValueField = "0"

/// The slot of a box's value among its fields.
let boxValueSlot = 4

/// The type of a box (an `MRef`'s) holding a value of `held`: a record of the links, then the value.
let boxType (held: IrType) =
    IrRecord [ dropField, linkType; nextField, linkType; ownerField, linkType; prevField, linkType; boxValueField, held ]

/// The fields of a new box holding `value`, its links not yet set: `WasmRegions`' `regionTrack` sets them.
let boxFields (value: Atom) =
    let link = ASized(NyxCompiler.SizedInteger.I32, 0L)
    [ dropField, link; nextField, link; ownerField, link; prevField, link; boxValueField, value ]

/// A box's type, with the type of what it holds.
let (|RegionBox|_|) (ty: IrType) =
    match ty with
    | IrRecord [ (drop, _); (next, _); (owner, _); (prev, _); (value, held) ] when
        drop = dropField && next = nextField && owner = ownerField && prev = prevField && value = boxValueField
        ->
        Some held
    | _ -> None

/// Whether a value of `ty` can hold a mutable object, so that storing it where it may outlive the block it was made in
/// has to promote it (see `CoreIrPromotion`): a box, a function value or a boxed value, whose captures can't be followed,
/// or a structure with one of them in it.
let canHoldMutable (ty: IrType) =
    let rec holds (ty: IrType) =
        match ty with
        | RegionBox _ -> true
        | IrRecord fields -> fields |> List.exists (snd >> holds)
        | IrTags tags -> tags |> List.exists (snd >> Option.exists holds)
        | IrList element
        | IrArray element -> holds element
        | IrFunction _
        | IrBox -> true
        // The type itself again, which holds what its body holds.
        | IrRecursive body -> holds body
        | IrSelf
        | IrInt
        | IrSized _
        | IrBool
        | IrUnit
        | IrString
        | IrFloat
        | IrF32
        | IrError
        | IrWord -> false
    holds ty

/// The function that drops what a box of type `box` holds, called through the table with the box when its region ends:
/// it takes the box and gives back 0, as an environment's drop function does.
let dropContentsName (box: IrType) = "__nanyx_region_drop_" + Identifiers.sanitizeIdentifier (IrType.mangle box)

/// The function that moves the mutable objects a value of `ty` holds onto a region, if they are on an inner one (see
/// `CoreIrPromotion`).
let promoteName (ty: IrType) = "__nanyx_promote_" + Identifiers.sanitizeIdentifier (IrType.mangle ty)

/// `k` of `operands`, a store's (the stored value last), after the barrier `helper` promotes the stored value (see
/// `CoreIrPromotion`), when it can hold a mutable object: `regionStore` onto the region of the object, the first operand,
/// or `regionStoreOutermost` onto the outermost region. The barrier reads its operands as the store does, so each
/// temporary among them is held in a local, made by `newLocal`, first.
let withBarrier (newLocal: IrType -> Var) (helper: string) (operands: Atom list) (k: Atom list -> Expr) : Expr =
    match List.tryLast operands with
    | Some value when canHoldMutable (atomType value) ->
        let settled =
            operands
            |> List.map (fun atom ->
                match atom with
                | AVar v when v.Kind = Temp -> Some (newLocal v.Type, atom)
                | _ -> None)
        let operands = List.map2 (fun binding atom -> match binding with Some (local, _) -> AVar local | None -> atom) settled operands
        let barrierOperands = if helper = WasmRegions.Names.regionStore then [ operands.Head; List.last operands ] else [ List.last operands ]
        let stored = newLocal IrUnit
        let body = k operands
        let barrier = Expr.make (Let(stored, Expr.make (CallRuntime(helper, barrierOperands, false)) IrUnit, body)) body.Type
        settled
        |> List.choose id
        |> List.rev
        |> List.fold (fun (acc: Expr) (local, atom) -> Expr.make (Let(local, Expr.make (Atom atom) local.Type, acc)) acc.Type) barrier
    | _ -> k operands
