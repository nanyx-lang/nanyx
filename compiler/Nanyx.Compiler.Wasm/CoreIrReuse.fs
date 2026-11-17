/// Drop specialisation and in-place reuse, on the counted core IR (steps 3 and 4 of "Inserting counts" in
/// `docs/src/content/compiler-internals/memory-management.md`).
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
    let mutable inners = 0

    member _.NewToken() =
        tokens <- tokens + 1
        reuseToken tokens

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

    /// A name for an object inside a scrutinee that an arm takes apart (see `takenApart`).
    member _.NewInner(ty: IrType) =
        inners <- inners + 1
        innerObject inners ty

    /// An arm with each scrutinee it takes apart and drops released instead, and each object inside one that its
    /// pattern takes apart released after it, and each released object's block reused by the first allocation of the
    /// same size the arm always makes that no other has taken.
    member this.Arm (scrutinees: Var list) (arm: Arm) : Arm =
        let patterns = arm.Patterns |> List.map (Pattern.mapGuards this.Expr)
        let dupped, inner =
            match arm.Body.Kind with
            | Dup(vars, body) -> vars, body
            | _ -> [], arm.Body
        match inner.Kind with
        | Drop(dead, rest) ->
            let deadIds = dead |> List.map _.Id |> Set.ofList
            // Each pattern, binding the objects inside its scrutinee that it takes apart, and the scrutinee if it is
            // released.
            let taken =
                List.zip scrutinees patterns
                |> List.map (fun (scrutinee, pattern) ->
                    match (if deadIds.Contains scrutinee.Id then takenApart this.NewInner scrutinee.Type pattern else None) with
                    | Some(rewritten, taken) -> rewritten, Some(scrutinee, taken)
                    | None -> pattern, None)
            let released = taken |> List.choose snd
            if released.IsEmpty then { Patterns = patterns; Body = this.Expr arm.Body }
            else
                // A name the arm binds to a released object's field and uses takes the field's reference over, so
                // the release takes a reference to it only when the object is shared; a name the arm does not use
                // leaves the field to be dropped. The name of an object inside always takes it over, as its own
                // release gives it up.
                let rec innerVars (taken: TakenApart) = taken.Inner |> List.collect (fun (var, inner) -> var :: innerVars inner)
                let owned = (dupped @ (released |> List.collect (snd >> innerVars))) |> List.map _.Id |> Set.ofList
                let ownedField (bound: Var option) = bound |> Option.filter (fun var -> owned.Contains var.Id)
                // Each object, then those inside it, with the size of its block.
                let rec releasesOf (object: Var) (taken: TakenApart) =
                    ({ Object = object
                       Fields = taken.Fields |> List.map (fun (offset, bound, fieldTy) -> offset, ownedField bound, fieldTy)
                       CountOffset = CoreIrLayout.headerSize object.Type
                       Token = None },
                     taken.Size)
                    :: (taken.Inner |> List.collect (fun (var, inner) -> releasesOf var inner))
                let releases = released |> List.collect (fun (scrutinee, taken) -> releasesOf scrutinee taken)
                let takenOver =
                    releases |> List.collect (fun (released, _) -> released.Fields |> List.choose (fun (_, bound, _) -> bound)) |> List.map _.Id |> Set.ofList
                let releasedIds = released |> List.map (fun (scrutinee, _) -> scrutinee.Id) |> Set.ofList
                let rest = this.Expr rest
                let rest =
                    match dead |> List.filter (fun var -> not (releasedIds.Contains var.Id)) with
                    | [] -> rest
                    | others -> Expr.make (Drop(others, rest)) rest.Type
                // Each released object's block goes to the first allocation of its size the arm makes that an
                // earlier one has not taken.
                let releases, rest =
                    releases
                    |> List.mapFold
                        (fun rest (release, size) ->
                            let token = this.NewToken()
                            match reuseFirst token size rest with
                            | Some reused -> { release with Token = Some token }, reused
                            | None -> release, rest)
                        rest
                let body =
                    releases
                    |> List.rev
                    |> List.fold (fun body released -> Expr.make (Release(released, body)) body.Type) rest
                let body =
                    match dupped |> List.filter (fun var -> not (takenOver.Contains var.Id)) with
                    | [] -> body
                    | others -> Expr.make (Dup(others, body)) body.Type
                { Patterns = taken |> List.map fst; Body = body }
        | _ -> { Patterns = patterns; Body = this.Expr arm.Body }

/// `functions` with drop specialisation and reuse.
let insert (functions: Function list) : Function list =
    functions |> List.map (fun func -> { func with Body = Rewriter().Expr func.Body })
