/// Inserts reference counting into the core IR, as Perceus does (see "Inserting counts" in
/// `compiler/design/memory.md`): a `Dup` where a value is used again after something takes it over, and a `Drop` (or
/// `DropAfter`) where a variable dies, on every branch.
///
/// Every variable holds a reference of its own (an owned one), except a parameter its function only borrows, a name
/// a pattern binds that the arm does not use, and, inside a `for` loop's body or a guard, every variable from outside
/// it (which the body runs on many times, or the guard only tests). A temporary is evaluated where it is used, so it
/// is counted there too: its value is analysed at its use, in the order the operands are evaluated.
module Transpiler.Wasm.CoreIrCounting

open System.Collections.Generic
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrDrop

/// How an instruction uses an operand: it takes the reference over (a call that owns the parameter, a field of a new
/// object, a value returned), or only borrows it while it runs.
type private Use =
    | Owned
    | Borrowed

/// Which parameters each function owns, by its name. A function not here owns all of them.
type Ownership = Map<string, bool list>

let private ownsParameter (ownership: Ownership) (name: string) (index: int) =
    match ownership |> Map.tryFind name with
    | Some owned when index < owned.Length -> owned.[index]
    | _ -> true

/// The operands of an instruction, in the order its code evaluates them, with how it uses each. What a `for` loop's
/// body, an `if`'s branches and a `match`'s arms use is not among them.
let private operands (ownership: Ownership) (e: Expr) : (Atom * Use) list =
    let all (how: Use) (atoms: Atom list) = atoms |> List.map (fun atom -> atom, how)
    match e.Kind with
    | Atom atom -> [ atom, Owned ]
    | Prim(_, atoms) -> all Borrowed atoms
    | Call(name, args) -> args |> List.mapi (fun index arg -> arg, (if ownsParameter ownership name index then Owned else Borrowed))
    | MakeTag(_, payload) -> [ payload, Owned ]
    | MakeRecord fields -> fields |> List.map snd |> all Owned
    | Field(record, _) -> [ record, Borrowed ]
    | UpdateRecord(record, _, updates) -> (record, Borrowed) :: (updates |> List.map snd |> all Owned)
    | MakeList items -> all Owned items
    | CallRuntime(name, args, _) when name = Helpers.listCons -> all Owned args
    // The first list's cells are copied, and the second becomes the tail of the copy.
    | CallRuntime(name, [ first; second ], _) when name = Helpers.listConcat -> [ first, Borrowed; second, Owned ]
    | CallRuntime(_, args, _) -> all Borrowed args
    | MakeClosure(_, _, captured) -> all Owned captured
    // The closure is the environment, which the function it holds owns. Its code reads the closure again for the
    // table slot after the arguments, so the closure counts as used last.
    | CallClosure(ClosureVar closure, args) -> all Owned args @ [ AVar closure, Owned ]
    | CallClosure(ClosureValue _, args) -> all Owned args
    | Interpolate parts -> parts |> List.choose (function Hole(hole, _) -> Some(AVar hole, Borrowed) | Text _ -> None)
    // `dbg` leaves the value it prints, or puts it in the cell of the tag it prints.
    | Dbg(value, _) -> [ value, Owned ]
    | ForEach { Source = OverList list } -> [ list, Borrowed ]
    | ForEach { Source = OverRange(first, last) } -> all Borrowed [ first; last ]
    | If(cond, _, _) -> [ cond, Borrowed ]
    | BoxFloat _
    | Panic _
    | Match _
    | Let _
    | Do _
    | Dup _
    | Drop _
    | DropAfter _
    | Release _
    | Reuse _ -> []

let rec private patternBinds (pattern: Pattern) : Var list =
    match pattern with
    | PBind var -> [ var ]
    | PTag(_, Some inner) -> patternBinds inner
    | PFields fields -> fields |> List.collect (snd >> patternBinds)
    | PAll parts -> parts |> List.collect patternBinds
    | PList(elements, rest) -> (elements |> List.collect patternBinds) @ (rest |> Option.map patternBinds |> Option.defaultValue [])
    | PTag(_, None)
    | PWildcard
    | PInt _
    | PBool _
    | PString _
    | PGuard _ -> []

let rec private mapGuards (f: Expr -> Expr) (pattern: Pattern) : Pattern =
    match pattern with
    | PGuard(var, guard) -> PGuard(var, f guard)
    | PTag(name, Some inner) -> PTag(name, Some (mapGuards f inner))
    | PFields fields -> PFields(fields |> List.map (fun (slot, inner) -> slot, mapGuards f inner))
    | PAll parts -> PAll(parts |> List.map (mapGuards f))
    | PList(elements, rest) -> PList(elements |> List.map (mapGuards f), rest |> Option.map (mapGuards f))
    | PTag(_, None)
    | PBind _
    | PWildcard
    | PInt _
    | PBool _
    | PString _ -> pattern

let rec private guards (pattern: Pattern) : (Var * Expr) list =
    match pattern with
    | PGuard(var, guard) -> [ var, guard ]
    | PTag(_, Some inner) -> guards inner
    | PFields fields -> fields |> List.collect (snd >> guards)
    | PAll parts -> parts |> List.collect guards
    | PList(elements, rest) -> (elements |> List.collect guards) @ (rest |> Option.map guards |> Option.defaultValue [])
    | PTag(_, None)
    | PBind _
    | PWildcard
    | PInt _
    | PBool _
    | PString _ -> []

/// Every expression in `e` that is a node of its own: `e`, the values and bodies of its lets, branches, arms, guards
/// and loop bodies, and so on down.
let rec private nodes (e: Expr) : Expr seq =
    seq {
        yield e
        match e.Kind with
        | Let(_, value, body)
        | Do(value, body) ->
            yield! nodes value
            yield! nodes body
        | If(_, ifTrue, ifFalse) ->
            yield! nodes ifTrue
            yield! nodes ifFalse
        | Match(_, arms) ->
            for arm in arms do
                for _, guard in arm.Patterns |> List.collect guards do
                    yield! nodes guard
                yield! nodes arm.Body
        | ForEach loop -> yield! nodes loop.Body
        | Dup(_, body)
        | Drop(_, body)
        | DropAfter(body, _)
        | Release(_, body)
        | Reuse(_, body) -> yield! nodes body
        | _ -> ()
    }

/// The calls `e` makes in tail position, by the rules of `compiler/design/tail-calls.md`.
let rec private tailCalls (e: Expr) : (string * Atom list) list =
    match e.Kind with
    | Let(_, _, body)
    | Do(_, body) -> tailCalls body
    | If(_, ifTrue, ifFalse) -> tailCalls ifTrue @ tailCalls ifFalse
    | Match(_, arms) -> arms |> List.collect (fun arm -> tailCalls arm.Body)
    | Call(name, args) -> [ name, args ]
    | _ -> []

/// Which parameters each function owns. A function called through the function table (a lifted lambda, or the
/// wrapper of a function passed as a value) owns all of them, as every call through a closure passes them owned.
/// Another function owns a parameter on the heap when it takes it over somewhere (passes it on owned, stores it,
/// returns it, matches on it), and borrows it when it only reads it. A parameter that a tail call passes something
/// the caller owns to is owned as well: the caller could otherwise only drop it after the call, which would then not
/// be a tail call (see "Reference counting" in `compiler/design/tail-calls.md`).
let inferOwnership (functions: Function list) : Ownership =
    let indirect =
        functions
        |> Seq.collect (fun f -> nodes f.Body)
        |> Seq.choose (fun e -> match e.Kind with MakeClosure(name, _, _) -> Some name | _ -> None)
        |> Set.ofSeq
    let owned = Dictionary<string, bool array>()
    for f in functions do
        if not (indirect.Contains f.Name) then
            owned.[f.Name] <- f.Params |> List.map (fun p -> not (isHeapType p.Type)) |> Array.ofList
    let current () = owned |> Seq.map (fun pair -> pair.Key, List.ofArray pair.Value) |> Map.ofSeq
    let mutable changed = true
    while changed do
        changed <- false
        let ownership = current ()
        let own (name: string) (index: int) =
            match owned.TryGetValue name with
            | true, flags when index < flags.Length && not flags.[index] ->
                flags.[index] <- true
                changed <- true
            | _ -> ()
        for f in functions do
            let borrowedParams = f.Params |> List.indexed |> List.filter (fun (index, _) -> not (ownsParameter ownership f.Name index))
            let takenOver =
                nodes f.Body
                |> Seq.collect (operands ownership)
                |> Seq.choose (function AVar var, Owned -> Some var.Id | _ -> None)
                |> Set.ofSeq
            for index, parameter in borrowedParams do
                if takenOver.Contains parameter.Id then own f.Name index
            let borrowedIds = borrowedParams |> List.map (snd >> _.Id) |> Set.ofList
            for callee, args in tailCalls f.Body do
                args
                |> List.iteri (fun index arg ->
                    match arg with
                    | AVar var when isHeapType var.Type && not (borrowedIds.Contains var.Id) -> own callee index
                    | _ -> ())
    current ()

/// Counts one function's body.
type private Counter(ownership: Ownership, func: Function) =
    let temps = Dictionary<int, Expr>()
    let vars = Dictionary<int, Var>()
    let counted = Dictionary<int, Expr>()
    let free = Dictionary<Expr, Set<int>>(HashIdentity.Reference)

    do
        for parameter in func.Params do
            vars.[parameter.Id] <- parameter
        for e in nodes func.Body do
            match e.Kind with
            | Let(var, value, _) ->
                vars.[var.Id] <- var
                if var.Kind = Temp then temps.[var.Id] <- value
            | Match(_, arms) ->
                for arm in arms do
                    for var in arm.Patterns |> List.collect patternBinds do
                        vars.[var.Id] <- var
            | _ -> ()

    let mk (kind: ExprKind) (ty: IrType) = { Kind = kind; Type = ty }

    let sortedVars (ids: Set<int>) = ids |> Set.toList |> List.map (fun id -> vars.[id])

    /// The variables `e` uses that it does not bind itself, with each temporary standing for its value.
    member this.Free (e: Expr) : Set<int> =
        match free.TryGetValue e with
        | true, found -> found
        | _ ->
            let found =
                match e.Kind with
                | Let(var, _, body) when var.Kind = Temp -> this.Free body
                | Let(var, value, body) -> Set.union (this.Free value) (this.Free body |> Set.remove var.Id)
                | Do(value, rest) -> Set.union (this.Free value) (this.Free rest)
                | If(cond, ifTrue, ifFalse) -> Set.unionMany [ this.FreeIn cond; this.Free ifTrue; this.Free ifFalse ]
                | Match(scrutinees, arms) ->
                    let ofArm (arm: Arm) =
                        let bound = arm.Patterns |> List.collect patternBinds |> List.map _.Id |> Set.ofList
                        let ofGuards =
                            arm.Patterns |> List.collect guards |> List.map (fun (var, guard) -> this.Free guard |> Set.remove var.Id)
                        Set.unionMany ((Set.difference (this.Free arm.Body) bound) :: ofGuards)
                    Set.unionMany ((scrutinees |> List.map _.Id |> Set.ofList) :: List.map ofArm arms)
                | ForEach loop ->
                    let loopVars = Set.ofList [ loop.Cursor.Id; loop.End.Id; loop.Element.Id ]
                    let source = operands ownership e |> List.map (fst >> this.FreeIn) |> Set.unionMany
                    Set.union source (Set.difference (this.Free loop.Body) loopVars)
                | Dup(dupped, body)
                | Drop(dupped, body) -> Set.union (dupped |> List.map _.Id |> Set.ofList) (this.Free body)
                | DropAfter(body, dropped) -> Set.union (dropped |> List.map _.Id |> Set.ofList) (this.Free body)
                | Release(released, body) -> this.Free body |> Set.add released.Object.Id
                | Reuse(_, body) -> this.Free body
                | _ -> operands ownership e |> List.map (fst >> this.FreeIn) |> Set.unionMany
            free.[e] <- found
            found

    member this.FreeIn (atom: Atom) : Set<int> =
        match atom with
        | AVar var when var.Kind = Temp -> this.Free temps.[var.Id]
        | AVar var -> Set.singleton var.Id
        | AInt _
        | ABool _
        | AUnit
        | AString _
        | ATag _ -> Set.empty

    /// `e` with its counting: `owned` are the variables holding a reference of their own here, and `after` those used
    /// once `e` is done. Each owned variable not used after is taken over or dropped by `e`; one `e` does not use is
    /// dropped before it.
    member this.Count (owned: Set<int>) (after: Set<int>) (e: Expr) : Expr =
        let dead = Set.difference (Set.difference owned (this.Free e)) after
        let body = this.Core (Set.difference owned dead) after e
        if dead.IsEmpty then body else mk (Drop(sortedVars dead, body)) body.Type

    member private this.Core (owned: Set<int>) (after: Set<int>) (e: Expr) : Expr =
        let ownedAfter (var: Var) (afterValue: Set<int>) =
            let kept = Set.intersect owned afterValue
            if isHeapType var.Type then kept.Add var.Id else kept
        match e.Kind with
        | Let(var, value, body) when var.Kind = Temp ->
            // The value is counted where the temporary is used, somewhere in the body.
            let body = this.Count owned after body
            let value =
                match counted.TryGetValue var.Id with
                | true, value -> value
                | _ -> failwith "Internal error: a temporary in the core IR is never used"
            mk (Let(var, value, body)) e.Type
        | Let(var, value, body) ->
            let afterValue = Set.union (this.Free body) after
            let value = this.Count owned afterValue value
            mk (Let(var, value, this.Count (ownedAfter var afterValue) after body)) e.Type
        | Do(value, rest) ->
            let afterValue = Set.union (this.Free rest) after
            let value = this.Count owned afterValue value
            mk (Do(value, this.Count (Set.intersect owned afterValue) after rest)) e.Type
        | If(cond, ifTrue, ifFalse) ->
            let afterCond = Set.unionMany [ this.Free ifTrue; this.Free ifFalse; after ]
            this.Operands owned afterCond [ cond, Borrowed ] |> ignore
            let owned = Set.intersect owned afterCond
            mk (If(cond, this.Count owned after ifTrue, this.Count owned after ifFalse)) e.Type
        | Match(scrutinees, arms) ->
            // An arm takes a reference to each name its patterns bind that it uses: they are fields of the scrutinee,
            // which the arm then drops if it does not use it either.
            let countArm (arm: Arm) =
                let patterns = arm.Patterns |> List.map (mapGuards (this.Count Set.empty Set.empty))
                let bodyFree = this.Free arm.Body
                let used = arm.Patterns |> List.collect patternBinds |> List.filter (fun var -> isHeapType var.Type && bodyFree.Contains var.Id)
                let owned = Set.union owned (used |> List.map _.Id |> Set.ofList)
                let body = this.Count owned after arm.Body
                { Patterns = patterns
                  Body = if used.IsEmpty then body else mk (Dup(used, body)) body.Type }
            mk (Match(scrutinees, List.map countArm arms)) e.Type
        | ForEach loop ->
            // The body only borrows what it uses from outside, which lives on until the loop is done.
            let loopVars = Set.ofList [ loop.Cursor.Id; loop.End.Id; loop.Element.Id ]
            let fromOutside = Set.difference (this.Free loop.Body) loopVars |> sortedVars |> List.map (fun var -> AVar var, Borrowed)
            let dups, drops = this.Operands owned after (operands ownership e @ fromOutside)
            let body = this.Count Set.empty Set.empty loop.Body
            this.Wrap dups drops (mk (ForEach { loop with Body = body }) e.Type)
        | Dup _
        | Drop _
        | DropAfter _
        | Release _
        | Reuse _ -> failwith "Internal error: the core IR is counted twice"
        | _ ->
            let dups, drops = this.Operands owned after (operands ownership e)
            this.Wrap dups drops e

    /// Counts the operands of an instruction, evaluated in this order: the variables it has to take a reference to
    /// first, and those (temporaries among them) it has to drop after it.
    member private this.Operands (owned: Set<int>) (after: Set<int>) (items: (Atom * Use) list) : Var list * Var list =
        let dups = ResizeArray<Var>()
        let drops = ResizeArray<Var>()
        let rec go (owned: Set<int>) (remaining: (Atom * Use) list) =
            match remaining with
            | [] -> ()
            | (atom, how) :: rest ->
                let afterThis = Set.union (rest |> List.map (fst >> this.FreeIn) |> Set.unionMany) after
                match atom with
                | AVar temp when temp.Kind = Temp ->
                    let value = temps.[temp.Id]
                    counted.[temp.Id] <- this.Count (Set.intersect owned (Set.union (this.Free value) afterThis)) afterThis value
                    if how = Borrowed && isHeapType temp.Type then drops.Add temp
                | AVar var when isHeapType var.Type ->
                    let diesHere = owned.Contains var.Id && not (afterThis.Contains var.Id)
                    match how with
                    | Owned -> if not diesHere then dups.Add var
                    | Borrowed -> if diesHere then drops.Add var
                | _ -> ()
                go (Set.intersect owned afterThis) rest
        go owned items
        List.ofSeq dups, List.ofSeq drops

    member private _.Wrap (dups: Var list) (drops: Var list) (e: Expr) : Expr =
        let dropped = if drops.IsEmpty then e else mk (DropAfter(e, drops)) e.Type
        if dups.IsEmpty then dropped else mk (Dup(dups, dropped)) e.Type

/// `functions` with reference counting inserted, by the parameters `ownership` says each owns.
let insert (ownership: Ownership) (functions: Function list) : Function list =
    functions
    |> List.map (fun func ->
        let owned =
            func.Params
            |> List.indexed
            |> List.filter (fun (index, parameter) -> isHeapType parameter.Type && ownsParameter ownership func.Name index)
            |> List.map (snd >> _.Id)
            |> Set.ofList
        { func with Body = Counter(ownership, func).Count owned Set.empty func.Body })
