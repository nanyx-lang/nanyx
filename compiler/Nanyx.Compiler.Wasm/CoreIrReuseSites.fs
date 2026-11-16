/// Where a `match` arm can reuse the block of an object it takes apart (see `CoreIrReuse`): the fields of the object,
/// and the first allocation of a block of its size the arm makes. Ownership inference asks too, since a function that
/// could reuse a parameter's cells owns it (see `CoreIrCounting.inferOwnership`).
module internal Transpiler.Wasm.CoreIrReuseSites

open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr

/// An object a pattern takes apart.
type TakenApart =
    { /// Each field of the object: where it is, the name that binds it, if any, and its type.
      Fields: (int * Var option * IrType) list
      /// The size of the object's block: its header and its fields.
      Size: int
      /// Each object in a field that the pattern takes apart in turn, with the name that binds it.
      Inner: (Var * TakenApart) list }

module TakenApart =
    /// The sizes of the blocks of the object and of each object inside it that is taken apart.
    let rec sizes (taken: TakenApart) : int list =
        taken.Size :: (taken.Inner |> List.collect (snd >> sizes))

/// How `pattern` takes apart a value of type `ty`, when it takes apart exactly one object and each of its fields is
/// bound, ignored, tested by a pattern that binds nothing (the field is then dropped with the object), or taken apart
/// in turn, as in `[b, a, ...rest]` or `[#some(n), ...rest]`. With it, the pattern binds each object inside that it
/// takes apart to a name `fresh` makes, so that the object can be released after the one it is in.
let rec takenApart (fresh: IrType -> Var) (ty: IrType) (pattern: Pattern) : (Pattern * TakenApart) option =
    let field (fieldTy: IrType) (inner: Pattern) =
        match inner with
        | PBind var -> Some(inner, Some var, None)
        | PWildcard -> Some(inner, None, None)
        | _ ->
            match takenApart fresh fieldTy inner with
            | Some(rewritten, taken) ->
                let var = fresh fieldTy
                Some(PAll [ PBind var; rewritten ], Some var, Some(var, taken))
            | None when List.isEmpty (Pattern.binds inner) -> Some(inner, None, None)
            | None -> None
    // Each field's offset, type and pattern, the size of the block, and how to rebuild the pattern from the fields'.
    let build (fields: (int * IrType * Pattern) list) (size: int) (rebuild: Pattern list -> Pattern) =
        let parts = fields |> List.map (fun (offset, fieldTy, inner) -> field fieldTy inner |> Option.map (fun (rewritten, bound, nested) -> rewritten, (offset, bound, fieldTy), nested))
        if parts |> List.forall Option.isSome then
            let parts = parts |> List.choose id
            Some(
                rebuild (parts |> List.map (fun (rewritten, _, _) -> rewritten)),
                { Fields = parts |> List.map (fun (_, taken, _) -> taken)
                  Size = size
                  Inner = parts |> List.choose (fun (_, _, nested) -> nested) })
        else None
    match IrType.unfold ty, pattern with
    | IrList element, PList(head :: more, rest) ->
        // `[a, b, ...rest]` is `[a, ...[b, ...rest]]`, and `[a]` is `[a, ...[]]`.
        let restPattern =
            match more, rest with
            | [], Some rest -> rest
            | [], None -> PList([], None)
            | _ -> PList(more, rest)
        build
            [ 0, element, head; CoreIrLayout.tailOffset element, ty, restPattern ]
            (CoreIrLayout.headerSize ty + CoreIrLayout.listCellSize element)
            (fun parts ->
                match more, rest with
                | [], None -> PList([ parts[0] ], None)
                | _ -> PList([ parts[0] ], Some parts[1]))
    | IrTags tags, PTag(name, Some inner) ->
        match tags |> List.tryFind (fst >> (=) name) with
        | Some(_, Some payloadTy) ->
            build
                [ CoreIrLayout.payloadOffset, payloadTy, inner ]
                (CoreIrLayout.headerSize ty + CoreIrLayout.tagCellSize payloadTy)
                (fun parts -> PTag(name, Some parts[0]))
        | _ -> None
    | IrRecord fields, PFields matched ->
        let patternOf slot = matched |> List.tryFind (fst >> (=) slot) |> Option.map snd |> Option.defaultValue PWildcard
        build
            (fields |> List.mapi (fun slot (_, fieldTy) -> CoreIrLayout.fieldOffset fields slot, fieldTy, patternOf slot))
            (CoreIrLayout.headerSize ty + CoreIrLayout.recordSize fields)
            (fun parts -> PFields(matched |> List.map (fun (slot, _) -> slot, parts[slot])))
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
