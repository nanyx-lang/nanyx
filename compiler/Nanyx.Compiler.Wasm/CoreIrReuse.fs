/// Drop specialisation and in-place reuse, on the counted core IR (steps 3 and 4 of "Inserting counts" in
/// `docs/src/content/work-in-progress/memory-management.md`).
///
/// A `match` arm that takes an object apart and does not use it itself takes a reference to each name it binds, then
/// drops the object (see `CoreIrCounting`). When the arm holds the only reference, both are wasted: the names can take
/// the object's references over, and the object can be freed without touching them. A `Release` checks the count and
/// does one or the other. When the arm then always allocates an object of the same size, the release hands it the
/// block instead of freeing it (a reuse token), and the allocation writes into it: mapping over a list held once
/// rewrites its cells in place.
module Transpiler.Wasm.CoreIrReuse

open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrDrop

/// The fields of the one object `pattern` takes apart, a value of type `ty`, when it takes apart exactly one and binds
/// or ignores each of its fields: where each is, what binds it, and its type; and the size of the object's block (its
/// header and its fields).
let private fieldsTakenApart (ty: IrType) (pattern: Pattern) : ((int * Var option * IrType) list * int) option =
    let leaf (inner: Pattern) =
        match inner with
        | PBind var -> Some (Some var)
        | PWildcard -> Some None
        | _ -> None
    match IrType.unfold ty, pattern with
    | IrList element, PList([ head ], Some rest) ->
        match leaf head, leaf rest with
        | Some headVar, Some restVar ->
            Some([ 0, headVar, element; CoreIrLayout.tailOffset element, restVar, ty ], 4 + CoreIrLayout.listCellSize element)
        | _ -> None
    | IrTags tags, PTag(name, Some inner) ->
        match tags |> List.tryFind (fst >> (=) name), leaf inner with
        | Some(_, Some payloadTy), Some payloadVar ->
            Some([ CoreIrLayout.payloadOffset, payloadVar, payloadTy ], 4 + CoreIrLayout.tagCellSize payloadTy)
        | _ -> None
    | IrRecord fields, PFields matched ->
        let leaves = matched |> List.map (fun (slot, inner) -> slot, leaf inner)
        if leaves |> List.forall (snd >> Option.isSome) then
            let taken =
                fields
                |> List.mapi (fun slot (_, fieldTy) ->
                    let bound = leaves |> List.tryFind (fst >> (=) slot) |> Option.bind snd |> Option.flatten
                    CoreIrLayout.fieldOffset fields slot, bound, fieldTy)
            Some(taken, 8 + CoreIrLayout.recordSize fields)
        else None
    | _ -> None

/// The size of the block `value` allocates, if it is an allocation that can write into a reused block.
let private allocationSize (value: Expr) =
    match value.Kind with
    | CallRuntime(name, [ head; _ ], _) when name = Helpers.listCons -> Some(4 + CoreIrLayout.listCellSize (atomType head))
    | MakeTag(_, payload) -> Some(4 + CoreIrLayout.tagCellSize (atomType payload))
    | MakeRecord fields -> Some(8 + CoreIrLayout.totalSize (fields |> List.map (snd >> atomType)))
    | _ -> None

let private mk (kind: ExprKind) (ty: IrType) = { Kind = kind; Type = ty }

/// `e` with the first allocation of a block of `size` that it always makes (not one in a branch, a loop or a guard)
/// writing into the block `token` holds, if there is one.
let private reuseFirst (token: Var) (size: int) (e: Expr) : Expr option =
    let rec here (value: Expr) =
        match value.Kind with
        | Dup(vars, inner) -> here inner |> Option.map (fun inner -> mk (Dup(vars, inner)) value.Type)
        | _ when allocationSize value = Some size -> Some(mk (Reuse(token, value)) value.Type)
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
            | Let(var, value, body) -> here value |> Option.map (fun value -> mk (Let(var, value, body)) link.Type)
            | Do(value, rest) -> here value |> Option.map (fun value -> mk (Do(value, rest)) link.Type)
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

type private Rewriter() =
    let mutable tokens = 0

    member _.NewToken() =
        tokens <- tokens + 1
        { Id = -tokens; Name = $"__reuse_{tokens}"; Kind = Scratch; Type = IrWord }

    /// `e` rewritten: a chain's values from the first, then what it ends in.
    member this.Expr(e: Expr) : Expr =
        let links, last = Chain.split e
        let links =
            links
            |> List.map (fun link ->
                match link.Kind with
                | Let(var, value, body) -> { link with Kind = Let(var, this.Expr value, body) }
                | Do(value, rest) -> { link with Kind = Do(this.Expr value, rest) }
                | _ -> link)
        let recur = this.Expr
        let kind =
            match last.Kind with
            | If(cond, ifTrue, ifFalse) -> If(cond, recur ifTrue, recur ifFalse)
            | Match(scrutinees, arms) -> Match(scrutinees, arms |> List.map (this.Arm scrutinees))
            | ForEach loop -> ForEach { loop with Body = recur loop.Body }
            | While loop -> While { loop with Condition = recur loop.Condition; Body = recur loop.Body }
            | DropAfter(body, vars) -> DropAfter(recur body, vars)
            | Reuse(token, body) -> Reuse(token, recur body)
            | other -> other
        Chain.rebuild Chain.relink links { last with Kind = kind }

    /// An arm with each scrutinee it takes apart and drops released instead, and the first released object's block
    /// reused by an allocation of the same size, if the arm always makes one.
    member this.Arm (scrutinees: Var list) (arm: Arm) : Arm =
        let patterns = arm.Patterns |> List.map (Pattern.mapGuards this.Expr)
        let dupped, inner =
            match arm.Body.Kind with
            | Dup(vars, body) -> vars, body
            | _ -> [], arm.Body
        let body =
            match inner.Kind with
            | Drop(dead, rest) ->
                let deadIds = dead |> List.map _.Id |> Set.ofList
                let released =
                    List.zip scrutinees arm.Patterns
                    |> List.choose (fun (scrutinee, pattern) ->
                        if deadIds.Contains scrutinee.Id then
                            fieldsTakenApart scrutinee.Type pattern |> Option.map (fun taken -> scrutinee, taken)
                        else None)
                if released.IsEmpty then this.Expr arm.Body
                else
                    // A name the arm binds to a released object's field and uses takes the field's reference over, so
                    // the release takes a reference to it only when the object is shared; a name the arm does not
                    // use leaves the field to be dropped.
                    let dupped' = dupped |> List.map _.Id |> Set.ofList
                    let ownedField (bound: Var option) = bound |> Option.filter (fun var -> dupped'.Contains var.Id)
                    let releases =
                        released
                        |> List.map (fun (scrutinee, (fields, _)) ->
                            { Object = scrutinee
                              Fields = fields |> List.map (fun (offset, bound, fieldTy) -> offset, ownedField bound, fieldTy)
                              CountOffset = countOffset scrutinee.Type
                              Token = None })
                    let takenOver =
                        releases |> List.collect (fun released -> released.Fields |> List.choose (fun (_, bound, _) -> bound)) |> List.map _.Id |> Set.ofList
                    let releasedIds = releases |> List.map (fun released -> released.Object.Id) |> Set.ofList
                    let rest = this.Expr rest
                    let rest =
                        match dead |> List.filter (fun var -> not (releasedIds.Contains var.Id)) with
                        | [] -> rest
                        | others -> mk (Drop(others, rest)) rest.Type
                    // The first released object's block goes to the first allocation of its size the arm makes.
                    let first = releases.Head
                    let token = this.NewToken()
                    let rest, firstRelease =
                        match reuseFirst token (snd (snd released.Head)) rest with
                        | Some reused -> reused, { first with Token = Some token }
                        | None -> rest, first
                    let body =
                        (firstRelease :: releases.Tail)
                        |> List.rev
                        |> List.fold (fun body released -> mk (Release(released, body)) body.Type) rest
                    match dupped |> List.filter (fun var -> not (takenOver.Contains var.Id)) with
                    | [] -> body
                    | others -> mk (Dup(others, body)) body.Type
            | _ -> this.Expr arm.Body
        { Patterns = patterns; Body = body }

/// `functions` with drop specialisation and reuse.
let insert (functions: Function list) : Function list =
    functions |> List.map (fun func -> { func with Body = Rewriter().Expr func.Body })
