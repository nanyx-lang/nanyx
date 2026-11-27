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
        | IrRecursive(_, bodies) -> bodies |> List.exists holds
        | IrSelf _
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

/// `k` of `operands`, each temporary among them held in a local, made by `newLocal`, first: so that code that reads
/// them may run before what `k` makes, which reads them too.
let private settled (newLocal: IrType -> Var) (operands: Atom list) (k: Atom list -> Expr) : Expr =
    let bindings =
        operands
        |> List.map (fun atom ->
            match atom with
            | AVar v when v.Kind = Temp -> Some (newLocal v.Type, atom)
            | _ -> None)
    let body = k (List.map2 (fun binding atom -> match binding with Some (local, _) -> AVar local | None -> atom) bindings operands)
    bindings
    |> List.choose id
    |> List.rev
    |> List.fold (fun (acc: Expr) (local, atom) -> Expr.make (Let(local, Expr.make (Atom atom) local.Type, acc)) acc.Type) body

/// `k` of `operands`, a store's (the stored value last), after the barrier `helper` promotes the stored value (see
/// `CoreIrPromotion`), when it can hold a mutable object: `regionStore` onto the region of the object, the first operand,
/// or `regionStoreOutermost` onto the outermost region. The barrier reads its operands as the store does, so each
/// temporary among them is held in a local, made by `newLocal`, first.
let withBarrier (newLocal: IrType -> Var) (helper: string) (operands: Atom list) (k: Atom list -> Expr) : Expr =
    match List.tryLast operands with
    | Some value when canHoldMutable (atomType value) ->
        settled newLocal operands (fun operands ->
            let barrierOperands = if helper = WasmRegions.Names.regionStore then [ operands.Head; List.last operands ] else [ List.last operands ]
            let stored = newLocal IrUnit
            let body = k operands
            Expr.make (Let(stored, Expr.make (CallRuntime(helper, barrierOperands, false)) IrUnit, body)) body.Type)
    | _ -> k operands

/// `k` of a new box holding `value` as a value of `held`, on the innermost region (`WasmRegions`' `regionTrack`), in a
/// local made by `newLocal`: an `MRef`'s, a captured cell's or an `MArray`'s.
let tracked (newLocal: IrType -> Var) (held: IrType) (value: Atom) (k: Atom -> Expr) : Expr =
    let boxTy = boxType held
    let box = newLocal boxTy
    let track = newLocal IrUnit
    let body = k (AVar box)
    let tracking = Expr.make (CallRuntime(WasmRegions.Names.regionTrack, [ AVar box ], false)) IrUnit
    Expr.make (Let(box, Expr.make (MakeRecord (boxFields value)) boxTy, Expr.make (Let(track, tracking, body)) body.Type)) body.Type

/// The intrinsic `name` of a mutable array, on `atoms`, of type `resultTy`, passed to `k`: an `MArray` is a box holding
/// an array (see `boxType`), so that it is on a region as an `MRef` is. One that makes an array makes it as an array's
/// intrinsic does and puts it in a new box. One that reads the array, or writes an element for the first time, is given
/// the box, and reads the array it holds with no reference of its own (see `CoreIrEmit.ArrayIntrinsic`): the box lives
/// until the intrinsic is done, and nothing puts another array in it. Counting a read of the array gave every
/// `MArray.at` and `MArray.set` a dup and a drop (#2417). `marraySet`, which changes an element, promotes the value onto
/// the box's region first (see `withBarrier`), and changes the element of the array in place.
let mutableArray (newLocal: IrType -> Var) (name: string) (atoms: Atom list) (resultTy: IrType) (k: Expr -> Expr) : Expr =
    let intrinsic = NyxCompiler.Intrinsics.asArrayIntrinsic name
    let returns = not (NyxCompiler.Intrinsics.returnsUnit name)
    let call (name: string) (atoms: Atom list) (ty: IrType) = Expr.make (CallRuntime(name, atoms, returns)) ty
    if name = NyxCompiler.Intrinsics.marrayOfList || name = NyxCompiler.Intrinsics.marrayNew then
        let arrayTy = match resultTy with RegionBox held -> held | other -> other
        let array = newLocal arrayTy
        let body = tracked newLocal arrayTy (AVar array) (fun box -> k (Expr.make (Atom box) (atomType box)))
        Expr.make (Let(array, call intrinsic atoms arrayTy, body)) body.Type
    else
        let apply (atoms: Atom list) = k (call intrinsic atoms resultTy)
        if name = NyxCompiler.Intrinsics.marraySet then withBarrier newLocal WasmRegions.Names.regionStore atoms apply else apply atoms

/// The array a mutable array's box holds, for its type `ty`, or `ty` itself when it is not a box: an array intrinsic is
/// given a mutable array's box (see `mutableArray`).
let arrayIn (ty: IrType) =
    match ty with
    | RegionBox held -> held
    | other -> other

/// Whether `name` is an intrinsic of a mutable array (see `mutableArray`).
let isMutableArrayIntrinsic (name: string) =
    [ NyxCompiler.Intrinsics.marrayOfList
      NyxCompiler.Intrinsics.marrayToList
      NyxCompiler.Intrinsics.marrayLength
      NyxCompiler.Intrinsics.marrayGet
      NyxCompiler.Intrinsics.marraySet
      NyxCompiler.Intrinsics.marrayNew
      NyxCompiler.Intrinsics.marrayWrite ]
    |> List.contains name
