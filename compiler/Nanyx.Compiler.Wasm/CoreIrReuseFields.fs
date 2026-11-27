/// A record built from the fields of another that dies there, `P(x = p.x + 1, y = p.y)`, made to reuse its block: the
/// field reads are made a `match` on the record that binds them, so that the record is released where it is taken
/// apart and its block goes to the allocation of the same size, as it does for an arm written that way (see
/// `CoreIrReuse`). On the core IR before reference counting, which then counts the match as any other (#2209).
module internal Transpiler.Wasm.CoreIrReuseFields

open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrRewrite

/// The size of the block of a record of type `ty`, if it is a record with fields that a `memory` block's region does
/// not track: a tracked object is on its region's list until it is freed (see `CoreIrRegions`), and is never reused.
let private recordBlock (ty: IrType) =
    match IrType.unfold ty with
    | IrRecord fields when not fields.IsEmpty && not (fields |> List.exists (fun (name, _) -> name = CoreIrRegions.dropField)) ->
        Some(CoreIrLayout.headerSize ty + CoreIrLayout.recordSize fields)
    | _ -> None

/// Whether `e` uses `record` only to read its fields, and makes a record whose block is the size `size`.
let private onlyReadAndRebuilt (record: Var) (size: int) (e: Expr) =
    let mutable uses = 0
    let mutable reads = 0
    let mutable rebuilt = false
    for node in allExprs e do
        uses <- uses + (directAtoms node |> List.filter (function AVar var -> var.Id = record.Id | _ -> false) |> List.length)
        uses <- uses + (directVars node |> List.filter (fun var -> var.Id = record.Id) |> List.length)
        match node.Kind with
        | Field(AVar var, _) when var.Id = record.Id -> reads <- reads + 1
        | MakeRecord _ when recordBlock node.Type = Some size -> rebuilt <- true
        | _ -> ()
    rebuilt && uses = reads

/// `e` with each read of a field of `record` the name `bound` gives for its slot.
let rec private withFieldsBound (record: Var) (bound: Map<int, Var>) (e: Expr) : Expr =
    let replaced (value: Expr) =
        match value.Kind with
        | Field(AVar var, slot) when var.Id = record.Id -> { value with Kind = Atom(AVar bound.[slot]) }
        | _ -> mapNested (withFieldsBound record bound) value
    let links, last = Chain.split e
    let links =
        links
        |> List.map (fun link ->
            match link.Kind with
            | Let(var, value, body) -> { link with Kind = Let(var, replaced value, body) }
            | Do(value, body) -> { link with Kind = Do(replaced value, body) }
            | _ -> link)
    Chain.rebuild Chain.relink links (replaced last)

/// The slots of `record` that `e` reads.
let private slotsRead (record: Var) (e: Expr) =
    allExprs e
    |> Seq.choose (fun node ->
        match node.Kind with
        | Field(AVar var, slot) when var.Id = record.Id -> Some slot
        | _ -> None)
    |> Set.ofSeq

type private Rewriter(firstId: int) =
    let mutable nextId = firstId

    member _.Fresh(name: string, ty: IrType) =
        nextId <- nextId + 1
        { Id = nextId; Name = $"__field_{name}_{nextId}"; Kind = PatternBound; Type = ty }

    /// `e`, a chain, with the values in it rewritten, and from the first link that reads a field of a record that the
    /// rest of the chain only reads the fields of and rebuilds one the size of, a match on that record.
    member this.Expr(e: Expr) : Expr =
        let links, last = Chain.split e
        let links =
            links
            |> List.map (fun link ->
                match link.Kind with
                | Let(var, value, body) -> { link with Kind = Let(var, this.Expr value, body) }
                | Do(value, body) -> { link with Kind = Do(this.Expr value, body) }
                | _ -> link)
        let last = mapNested this.Expr last
        let candidate (link: Expr) =
            match link.Kind with
            | Let(_, { Kind = Field(AVar record, _) }, _)
            | Do({ Kind = Field(AVar record, _) }, _) when record.Kind <> Temp ->
                recordBlock record.Type |> Option.map (fun size -> record, size)
            | _ -> None
        let rec from (before: Expr list) (links: Expr list) =
            match links with
            | [] -> Chain.rebuild Chain.relink (List.rev before) last
            | link :: after ->
                let rest = Chain.rebuild Chain.relink links last
                match candidate link with
                | Some(record, size) when onlyReadAndRebuilt record size rest ->
                    let fields = match IrType.unfold record.Type with IrRecord fields -> fields | _ -> []
                    let bound =
                        slotsRead record rest
                        |> Seq.map (fun slot -> slot, this.Fresh(fst fields.[slot], snd fields.[slot]))
                        |> Map.ofSeq
                    let body = withFieldsBound record bound rest
                    let pattern = PFields(bound |> Map.toList |> List.map (fun (slot, var) -> slot, PBind var))
                    let matched = Expr.make (Match([ record ], [ { Patterns = [ pattern ]; Body = body } ])) rest.Type
                    // A temporary is evaluated where it is used, in the chain it is bound in: one bound before the match
                    // and used in its arm is made a local, evaluated where it is bound (as the inliner makes one).
                    let used = allExprs body |> Seq.collect (fun node -> (directAtoms node |> List.choose (function AVar var -> Some var.Id | _ -> None)) @ (directVars node |> List.map _.Id)) |> Set.ofSeq
                    let waiting =
                        before
                        |> List.choose (fun link ->
                            match link.Kind with
                            | Let(var, _, _) when var.Kind = Temp && used.Contains var.Id -> Some var.Id
                            | _ -> None)
                        |> Set.ofList
                    let chain = Chain.rebuild Chain.relink (List.rev before) matched
                    if waiting.IsEmpty then chain
                    else mapVars (fun var -> if waiting.Contains var.Id then { var with Kind = Local } else var) id chain
                | _ -> from (link :: before) after
        from [] links

/// `functions` with each record that dies where a record of its size is built from its fields taken apart by a match.
let rewrite (functions: Function list) : Function list =
    functions
    |> List.map (fun func ->
        let firstId = func.Params |> List.fold (fun highest (var: Var) -> max highest var.Id) (maxVarId 0 func.Body)
        { func with Body = Rewriter(firstId).Expr func.Body })
