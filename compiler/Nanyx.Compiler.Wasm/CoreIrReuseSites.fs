/// Where a `match` arm can reuse the block of an object it takes apart (see `CoreIrReuse`): the fields of the object,
/// and the first allocation of a block of its size the arm makes. Ownership inference asks too, since a function that
/// could reuse a parameter's cells owns it (see `CoreIrCounting.inferOwnership`).
module internal Transpiler.Wasm.CoreIrReuseSites

open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr

/// The fields of the one object `pattern` takes apart, a value of type `ty`, when it takes apart exactly one and binds
/// or ignores each of its fields: where each is, what binds it, and its type; and the size of the object's block (its
/// header and its fields).
let fieldsTakenApart (ty: IrType) (pattern: Pattern) : ((int * Var option * IrType) list * int) option =
    let leaf (inner: Pattern) =
        match inner with
        | PBind var -> Some (Some var)
        | PWildcard -> Some None
        | _ -> None
    match IrType.unfold ty, pattern with
    | IrList element, PList([ head ], Some rest) ->
        match leaf head, leaf rest with
        | Some headVar, Some restVar ->
            Some([ 0, headVar, element; CoreIrLayout.tailOffset element, restVar, ty ], CoreIrLayout.headerSize ty + CoreIrLayout.listCellSize element)
        | _ -> None
    | IrTags tags, PTag(name, Some inner) ->
        match tags |> List.tryFind (fst >> (=) name), leaf inner with
        | Some(_, Some payloadTy), Some payloadVar ->
            Some([ CoreIrLayout.payloadOffset, payloadVar, payloadTy ], CoreIrLayout.headerSize ty + CoreIrLayout.tagCellSize payloadTy)
        | _ -> None
    | IrRecord fields, PFields matched ->
        let leaves = matched |> List.map (fun (slot, inner) -> slot, leaf inner)
        if leaves |> List.forall (snd >> Option.isSome) then
            let taken =
                fields
                |> List.mapi (fun slot (_, fieldTy) ->
                    let bound = leaves |> List.tryFind (fst >> (=) slot) |> Option.bind snd |> Option.flatten
                    CoreIrLayout.fieldOffset fields slot, bound, fieldTy)
            Some(taken, CoreIrLayout.headerSize ty + CoreIrLayout.recordSize fields)
        else None
    | _ -> None

/// The size of the block `value` allocates, if it is an allocation that can write into a reused block.
let private allocationSize (value: Expr) =
    match value.Kind with
    | CallRuntime(name, [ head; _ ], _) when Helpers.reusingVariant name = Some Helpers.listConsAt -> Some(Header.countOnly + CoreIrLayout.listCellSize (atomType head))
    | MakeTag(_, payload) -> Some(Header.countOnly + CoreIrLayout.tagCellSize (atomType payload))
    | MakeRecord fields -> Some(Header.withInfo + CoreIrLayout.totalSize (fields |> List.map (snd >> atomType)))
    | _ -> None

/// `e` with the first allocation of a block of `size` that it makes (not one in a loop or a guard) writing into the
/// block `token` holds, if there is one. An `if` or `match` whose branches make one gives the token to each branch: a
/// branch that makes one writes into the block, and one that makes none frees it first, so the block is used or freed
/// once on every path.
let rec reuseFirst (token: Var) (size: int) (e: Expr) : Expr option =
    let inBranches (branches: Expr list) =
        let reused = branches |> List.map (reuseFirst token size)
        if reused |> List.exists Option.isSome then
            Some(List.map2 (fun reused (branch: Expr) -> reused |> Option.defaultWith (fun () -> Expr.make (FreeToken(token, branch)) branch.Type)) reused branches)
        else None
    let rec here (value: Expr) =
        match value.Kind with
        | Dup(vars, inner) -> here inner |> Option.map (fun inner -> Expr.make (Dup(vars, inner)) value.Type)
        | _ when allocationSize value = Some size -> Some(Expr.make (Reuse(token, value)) value.Type)
        | If(cond, ifTrue, ifFalse) ->
            inBranches [ ifTrue; ifFalse ]
            |> Option.map (fun branches -> Expr.make (If(cond, branches[0], branches[1])) value.Type)
        | Match(scrutinees, arms) ->
            inBranches (arms |> List.map _.Body)
            |> Option.map (fun bodies -> Expr.make (Match(scrutinees, List.map2 (fun arm body -> { arm with Body = body }) arms bodies)) value.Type)
        | _ -> None
    // The links of the chain passed on the way to the allocation, innermost first, and the link that makes it.
    let mutable passed = []
    let mutable current = e
    let mutable found = None
    let mutable searching = true
    while searching do
        let link = current
        let reused =
            match link.Kind with
            | Let(var, value, body) -> here value |> Option.map (fun value -> Expr.make (Let(var, value, body)) link.Type)
            | Do(value, rest) -> here value |> Option.map (fun value -> Expr.make (Do(value, rest)) link.Type)
            | Drop _
            | Release _ -> None
            | _ -> here link
        match reused, Chain.next link with
        | Some reused, _ ->
            found <- Some reused
            searching <- false
        | None, Some following when (match link.Kind with Dup _ -> false | _ -> true) ->
            passed <- link :: passed
            current <- following
        | None, _ -> searching <- false
    found |> Option.map (fun reused -> passed |> List.fold (fun following link -> Chain.relink link following) reused)
