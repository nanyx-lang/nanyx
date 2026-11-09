/// Drop specialisation and in-place reuse, on the counted core IR (steps 3 and 4 of "Inserting counts" in
/// `compiler/design/memory.md`).
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
/// or ignores each of its fields: where each is, what binds it, and its type.
let private fieldsTakenApart (ty: IrType) (pattern: Pattern) : (int * Var option * IrType) list option =
    let leaf (inner: Pattern) =
        match inner with
        | PBind var -> Some (Some var)
        | PWildcard -> Some None
        | _ -> None
    match ty, pattern with
    | IrList element, PList([ head ], Some rest) ->
        match leaf head, leaf rest with
        | Some headVar, Some restVar -> Some [ 0, headVar, element; 4, restVar, ty ]
        | _ -> None
    | IrTags tags, PTag(name, Some inner) ->
        match tags |> List.tryFind (fst >> (=) name), leaf inner with
        | Some(_, Some payloadTy), Some payloadVar -> Some [ 4, payloadVar, payloadTy ]
        | _ -> None
    | IrRecord fields, PFields matched ->
        let leaves = matched |> List.map (fun (slot, inner) -> slot, leaf inner)
        if leaves |> List.forall (snd >> Option.isSome) then
            fields
            |> List.mapi (fun slot (_, fieldTy) ->
                let bound = leaves |> List.tryFind (fst >> (=) slot) |> Option.bind snd |> Option.flatten
                slot * 4, bound, fieldTy)
            |> Some
        else None
    | _ -> None

/// The size of the block an object of type `ty` that a pattern takes apart has (its header and its fields), or 0 for
/// one that is not reused.
let private blockSize (ty: IrType) =
    match ty with
    | IrList _
    | IrTags _ -> 12
    | IrRecord fields -> 8 + 4 * fields.Length
    | _ -> 0

/// The size of the block `value` allocates, if it is an allocation that can write into a reused block.
let private allocationSize (value: Expr) =
    match value.Kind with
    | CallRuntime(name, _, _) when name = Helpers.listCons -> Some 12
    | MakeTag _ -> Some 12
    | MakeRecord fields -> Some(8 + 4 * fields.Length)
    | _ -> None

let private mk (kind: ExprKind) (ty: IrType) = { Kind = kind; Type = ty }

/// `e` with the first allocation of a block of `size` that it always makes (not one in a branch, a loop or a guard)
/// writing into the block `token` holds, if there is one.
let rec private reuseFirst (token: Var) (size: int) (e: Expr) : Expr option =
    let rec here (value: Expr) =
        match value.Kind with
        | Dup(vars, inner) -> here inner |> Option.map (fun inner -> mk (Dup(vars, inner)) value.Type)
        | _ when allocationSize value = Some size -> Some(mk (Reuse(token, value)) value.Type)
        | _ -> None
    match e.Kind with
    | Let(var, value, body) ->
        match here value with
        | Some value -> Some(mk (Let(var, value, body)) e.Type)
        | None -> reuseFirst token size body |> Option.map (fun body -> mk (Let(var, value, body)) e.Type)
    | Do(value, rest) ->
        match here value with
        | Some value -> Some(mk (Do(value, rest)) e.Type)
        | None -> reuseFirst token size rest |> Option.map (fun rest -> mk (Do(value, rest)) e.Type)
    | Drop(vars, body) -> reuseFirst token size body |> Option.map (fun body -> mk (Drop(vars, body)) e.Type)
    | Release(released, body) -> reuseFirst token size body |> Option.map (fun body -> mk (Release(released, body)) e.Type)
    | _ -> here e

type private Rewriter() =
    let mutable tokens = 0

    member _.NewToken() =
        tokens <- tokens + 1
        { Id = -tokens; Name = $"__reuse_{tokens}"; Kind = Scratch; Type = IrInt }

    member this.Expr(e: Expr) : Expr =
        let recur = this.Expr
        let kind =
            match e.Kind with
            | Let(var, value, body) -> Let(var, recur value, recur body)
            | Do(value, rest) -> Do(recur value, recur rest)
            | If(cond, ifTrue, ifFalse) -> If(cond, recur ifTrue, recur ifFalse)
            | Match(scrutinees, arms) -> Match(scrutinees, arms |> List.map (this.Arm scrutinees))
            | ForEach loop -> ForEach { loop with Body = recur loop.Body }
            | Dup(vars, body) -> Dup(vars, recur body)
            | Drop(vars, body) -> Drop(vars, recur body)
            | DropAfter(body, vars) -> DropAfter(recur body, vars)
            | Release(released, body) -> Release(released, recur body)
            | Reuse(token, body) -> Reuse(token, recur body)
            | other -> other
        { e with Kind = kind }

    /// An arm with each scrutinee it takes apart and drops released instead, and the first released object's block
    /// reused by an allocation of the same size, if the arm always makes one.
    member this.Arm (scrutinees: Var list) (arm: Arm) : Arm =
        let rec guarded (pattern: Pattern) =
            match pattern with
            | PGuard(var, guard) -> PGuard(var, this.Expr guard)
            | PTag(name, Some inner) -> PTag(name, Some(guarded inner))
            | PFields fields -> PFields(fields |> List.map (fun (slot, inner) -> slot, guarded inner))
            | PAll parts -> PAll(parts |> List.map guarded)
            | PList(elements, rest) -> PList(elements |> List.map guarded, rest |> Option.map guarded)
            | _ -> pattern
        let patterns = arm.Patterns |> List.map guarded
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
                            fieldsTakenApart scrutinee.Type pattern |> Option.map (fun fields -> scrutinee, fields)
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
                        |> List.map (fun (scrutinee, fields) ->
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
                        match blockSize first.Object.Type with
                        | 0 -> rest, first
                        | size ->
                            match reuseFirst token size rest with
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
