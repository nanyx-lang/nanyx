/// Drop specialisation and in-place reuse, on the counted core IR (steps 3 and 4 of "Inserting counts" in
/// `docs/src/content/work-in-progress/memory-management.md`).
///
/// A `match` arm that takes an object apart and does not use it itself takes a reference to each name it binds, then
/// drops the object (see `CoreIrCounting`). When the arm holds the only reference, both are wasted: the names can take
/// the object's references over, and the object can be freed without touching them. A `Release` checks the count and
/// does one or the other. When the arm then allocates an object of the same size, the release hands it the block
/// instead of freeing it (a reuse token), and the allocation writes into it: mapping over a list held once rewrites its
/// cells in place. When only some branches of an `if` or `match` allocate one, the others free the block (see
/// `FreeToken`): filtering a list held once keeps the cells of the elements it keeps.
module Transpiler.Wasm.CoreIrReuse

open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrDrop
open Transpiler.Wasm.CoreIrReuseSites

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
        let last =
            match last.Kind with
            | Match(scrutinees, arms) -> { last with Kind = Match(scrutinees, arms |> List.map (this.Arm scrutinees)) }
            | _ -> Expr.map this.Expr last
        Chain.rebuild Chain.relink links last

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
                              CountOffset = CoreIrLayout.headerSize scrutinee.Type
                              Token = None })
                    let takenOver =
                        releases |> List.collect (fun released -> released.Fields |> List.choose (fun (_, bound, _) -> bound)) |> List.map _.Id |> Set.ofList
                    let releasedIds = releases |> List.map (fun released -> released.Object.Id) |> Set.ofList
                    let rest = this.Expr rest
                    let rest =
                        match dead |> List.filter (fun var -> not (releasedIds.Contains var.Id)) with
                        | [] -> rest
                        | others -> Expr.make (Drop(others, rest)) rest.Type
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
                        |> List.fold (fun body released -> Expr.make (Release(released, body)) body.Type) rest
                    match dupped |> List.filter (fun var -> not (takenOver.Contains var.Id)) with
                    | [] -> body
                    | others -> Expr.make (Dup(others, body)) body.Type
            | _ -> this.Expr arm.Body
        { Patterns = patterns; Body = body }

/// `functions` with drop specialisation and reuse.
let insert (functions: Function list) : Function list =
    functions |> List.map (fun func -> { func with Body = Rewriter().Expr func.Body })
