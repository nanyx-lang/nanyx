/// Inserts reference counting into the core IR, as Perceus does (see "Inserting counts" in
/// `docs/src/content/work-in-progress/memory-management.md`): a `Dup` where a value is used again after something takes it over, and a `Drop` (or
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

/// How each function uses its parameters, by its name. A function not here owns all of them.
type internal Ownership = Map<string, Use list>

let private parameterUse (ownership: Ownership) (name: string) (index: int) =
    match ownership |> Map.tryFind name with
    | Some uses when index < uses.Length -> uses.[index]
    | _ -> Owned

/// The operands of an instruction, in the order its code evaluates them, with how it uses each. What a `for` loop's
/// body, an `if`'s branches and a `match`'s arms use is not among them.
let private operands (ownership: Ownership) (e: Expr) : (Atom * Use) list =
    let all (how: Use) (atoms: Atom list) = atoms |> List.map (fun atom -> atom, how)
    match e.Kind with
    | Atom atom -> [ atom, Owned ]
    | Prim(_, atoms) -> all Borrowed atoms
    | Call(name, args) -> args |> List.mapi (fun index arg -> arg, parameterUse ownership name index)
    | MakeTag(_, payload) -> [ payload, Owned ]
    | MakeRecord fields -> fields |> List.map snd |> all Owned
    | Field(record, _) -> [ record, Borrowed ]
    | UpdateRecord(record, updates) -> (record, Borrowed) :: (updates |> List.map snd |> all Owned)
    // The value is taken over by the cell, which is used after it, to drop what it held.
    | Assign(cell, value) -> [ value, Owned; AVar cell, Borrowed ]
    | SetField(record, _, value) -> [ value, Owned; AVar record, Borrowed ]
    | MakeList items -> all Owned items
    | CallRuntime(name, args, _) -> args |> List.mapi (fun index arg -> arg, Helpers.operandUse name index)
    | MakeClosure(_, _, captured) -> all Owned captured
    // The closure is the environment, which the function it holds owns. Its code reads the closure again for the
    // table slot after the arguments, so the closure counts as used last.
    | CallClosure(closure, args) -> all Owned args @ [ AVar closure, Owned ]
    | Interpolate parts -> parts |> List.choose (function Hole(hole, _) -> Some(AVar hole, Borrowed) | Text _ -> None)
    | Dbg text -> [ text, Borrowed ]
    | ForEach loop -> all Borrowed (ForLoop.sourceAtoms loop)
    | If(cond, _, _) -> [ cond, Borrowed ]
    | BoxFloat _
    | Panic _
    | While _
    | Match _
    | Let _
    | Do _
    | Dup _
    | Drop _
    | DropAfter _
    | Release _
    | Reuse _ -> []

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
                for _, guard in arm.Patterns |> List.collect Pattern.guards do
                    yield! nodes guard
                yield! nodes arm.Body
        | ForEach loop -> yield! nodes loop.Body
        | While loop ->
            yield! nodes loop.Condition
            yield! nodes loop.Body
        | Dup(_, body)
        | Drop(_, body)
        | DropAfter(body, _)
        | Release(_, body)
        | Reuse(_, body) -> yield! nodes body
        | _ -> ()
    }

/// The calls `e` makes in tail position, by the rules of `docs/src/content/work-in-progress/tail-calls.md`.
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
/// be a tail call (see "Reference counting" in `docs/src/content/work-in-progress/tail-calls.md`).
let internal inferOwnership (functions: Function list) : Ownership =
    let indirect =
        functions
        |> Seq.collect (fun f -> nodes f.Body)
        |> Seq.collect (fun e ->
            let made = match e.Kind with MakeClosure(name, _, _) -> [ name ] | _ -> []
            let static' = operands Map.empty e |> List.choose (function AStaticClosure(name, _, _), _ -> Some name | _ -> None)
            made @ static')
        |> Set.ofSeq
    let uses = Dictionary<string, Use array>()
    for f in functions do
        if not (indirect.Contains f.Name) then
            uses.[f.Name] <- f.Params |> List.map (fun p -> if isHeapType p.Type then Borrowed else Owned) |> Array.ofList
    let current () = uses |> Seq.map (fun pair -> pair.Key, List.ofArray pair.Value) |> Map.ofSeq
    let mutable changed = true
    while changed do
        changed <- false
        let ownership = current ()
        let own (name: string) (index: int) =
            match uses.TryGetValue name with
            | true, parameters when index < parameters.Length && parameters.[index] = Borrowed ->
                parameters.[index] <- Owned
                changed <- true
            | _ -> ()
        for f in functions do
            let borrowedParams = f.Params |> List.indexed |> List.filter (fun (index, _) -> parameterUse ownership f.Name index = Borrowed)
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
                    for var in arm.Patterns |> List.collect Pattern.binds do
                        vars.[var.Id] <- var
            | _ -> ()

    let sortedVars (ids: Set<int>) = ids |> Set.toList |> List.map (fun id -> vars.[id])

    /// The variables `e` uses that it does not bind itself, with each temporary standing for its value. A chain is
    /// walked from its end.
    member this.Free (e: Expr) : Set<int> =
        match free.TryGetValue e with
        | true, found -> found
        | _ ->
            let links, last = Chain.split e
            List.foldBack
                (fun (link: Expr) (following: Set<int>) ->
                    match free.TryGetValue link with
                    | true, found -> found
                    | _ ->
                        let found =
                            match link.Kind with
                            | Let(var, _, _) when var.Kind = Temp -> following
                            | Let(var, value, _) -> Set.union (this.Free value) (following |> Set.remove var.Id)
                            | Do(value, _) -> Set.union (this.Free value) following
                            | Dup(vars, _)
                            | Drop(vars, _) -> Set.union (vars |> List.map _.Id |> Set.ofList) following
                            | Release(released, _) -> following |> Set.add released.Object.Id
                            | _ -> internalError "a chain holds only its links"
                        free.[link] <- found
                        found)
                links
                (this.FreeOfLast last)

    /// The variables an expression that is not a link of a chain uses (see `Free`).
    member private this.FreeOfLast (e: Expr) : Set<int> =
        match free.TryGetValue e with
        | true, found -> found
        | _ ->
            let found =
                match e.Kind with
                | If(cond, ifTrue, ifFalse) -> Set.unionMany [ this.FreeIn cond; this.Free ifTrue; this.Free ifFalse ]
                | Match(scrutinees, arms) ->
                    let ofArm (arm: Arm) =
                        let bound = arm.Patterns |> List.collect Pattern.binds |> List.map _.Id |> Set.ofList
                        let ofGuards =
                            arm.Patterns |> List.collect Pattern.guards |> List.map (fun (var, guard) -> this.Free guard |> Set.remove var.Id)
                        Set.unionMany ((Set.difference (this.Free arm.Body) bound) :: ofGuards)
                    Set.unionMany ((scrutinees |> List.map _.Id |> Set.ofList) :: List.map ofArm arms)
                | ForEach loop ->
                    let loopVars = ForLoop.locals loop |> List.map _.Id |> Set.ofList
                    let source = operands ownership e |> List.map (fst >> this.FreeIn) |> Set.unionMany
                    Set.union source (Set.difference (this.Free loop.Body) loopVars)
                | While loop -> Set.union (this.Free loop.Condition) (this.Free loop.Body)
                | DropAfter(body, dropped) -> Set.union (dropped |> List.map _.Id |> Set.ofList) (this.Free body)
                | Reuse(_, body) -> this.Free body
                | _ -> operands ownership e |> List.map (fst >> this.FreeIn) |> Set.unionMany
            free.[e] <- found
            found

    member this.FreeIn (atom: Atom) : Set<int> =
        match atom with
        | AVar var when var.Kind = Temp -> this.Free temps.[var.Id]
        | AVar var -> Set.singleton var.Id
        | AInt _
        | ASized _
        | ABool _
        | AUnit
        | AString _
        | ATag _
        | AStaticClosure _ -> Set.empty

    /// `e` with its counting: `owned` are the variables holding a reference of their own here, and `after` those used
    /// once `e` is done. Each owned variable not used after is taken over or dropped by `e`; one `e` does not use is
    /// dropped before it.
    member this.Count (owned: Set<int>) (after: Set<int>) (e: Expr) : Expr =
        // A chain is counted a link at a time, each with what is owned once those before it are done, and rebuilt from
        // its end.
        let mutable owned = owned
        let mutable current = e
        let mutable last = None
        let rebuilds = ResizeArray<Expr -> Expr>()
        while last.IsNone do
            let dead = Set.difference (Set.difference owned (this.Free current)) after
            let ownedHere = Set.difference owned dead
            let dropDead (body: Expr) = if dead.IsEmpty then body else Expr.make (Drop(sortedVars dead, body)) body.Type
            let ty = current.Type
            match current.Kind with
            | Let(var, value, body) when var.Kind = Temp ->
                // The value is counted where the temporary is used, somewhere in the body.
                rebuilds.Add(fun body ->
                    let value =
                        match counted.TryGetValue var.Id with
                        | true, value -> value
                        | _ -> internalError "a temporary in the core IR is never used"
                    dropDead (Expr.make (Let(var, value, body)) ty))
                owned <- ownedHere
                current <- body
            | Let(var, value, body) ->
                let afterValue = Set.union (this.Free body) after
                let value = this.Count ownedHere afterValue value
                rebuilds.Add(fun body -> dropDead (Expr.make (Let(var, value, body)) ty))
                let kept = Set.intersect ownedHere afterValue
                owned <- if isHeapType var.Type then kept.Add var.Id else kept
                current <- body
            | Do(value, rest) ->
                let afterValue = Set.union (this.Free rest) after
                let value = this.Count ownedHere afterValue value
                rebuilds.Add(fun rest -> dropDead (Expr.make (Do(value, rest)) ty))
                owned <- Set.intersect ownedHere afterValue
                current <- rest
            | _ -> last <- Some (dropDead (this.Core ownedHere after current))
        Chain.rebuild (fun rebuild body -> rebuild body) (List.ofSeq rebuilds) last.Value

    member private this.Core (owned: Set<int>) (after: Set<int>) (e: Expr) : Expr =
        match e.Kind with
        | If(cond, ifTrue, ifFalse) ->
            let afterCond = Set.unionMany [ this.Free ifTrue; this.Free ifFalse; after ]
            this.Operands owned afterCond [ cond, Borrowed ] |> ignore
            let owned = Set.intersect owned afterCond
            Expr.make (If(cond, this.Count owned after ifTrue, this.Count owned after ifFalse)) e.Type
        | Match(scrutinees, arms) ->
            // An arm takes a reference to each name its patterns bind that it uses: they are fields of the scrutinee,
            // which the arm then drops if it does not use it either.
            let countArm (arm: Arm) =
                let patterns = arm.Patterns |> List.map (Pattern.mapGuards (this.Count Set.empty Set.empty))
                let bodyFree = this.Free arm.Body
                let used = arm.Patterns |> List.collect Pattern.binds |> List.filter (fun var -> isHeapType var.Type && bodyFree.Contains var.Id)
                let owned = Set.union owned (used |> List.map _.Id |> Set.ofList)
                let body = this.Count owned after arm.Body
                { Patterns = patterns
                  Body = if used.IsEmpty then body else Expr.make (Dup(used, body)) body.Type }
            Expr.make (Match(scrutinees, List.map countArm arms)) e.Type
        | ForEach loop ->
            // The body only borrows what it uses from outside, which lives on until the loop is done.
            let loopVars = ForLoop.locals loop |> List.map _.Id |> Set.ofList
            let fromOutside = Set.difference (this.Free loop.Body) loopVars |> sortedVars |> List.map (fun var -> AVar var, Borrowed)
            let dups, drops = this.Operands owned after (operands ownership e @ fromOutside)
            let body = this.Count Set.empty Set.empty loop.Body
            this.Wrap dups drops (Expr.make (ForEach { loop with Body = body }) e.Type)
        | While loop ->
            // The condition and the body run again each time round, so they only borrow what they use, which lives on
            // until the loop is done.
            let fromOutside = this.Free e |> sortedVars |> List.map (fun var -> AVar var, Borrowed)
            let dups, drops = this.Operands owned after fromOutside
            let condition = this.Count Set.empty Set.empty loop.Condition
            let body = this.Count Set.empty Set.empty loop.Body
            this.Wrap dups drops (Expr.make (While { loop with Condition = condition; Body = body }) e.Type)
        | Dup _
        | Drop _
        | DropAfter _
        | Release _
        | Reuse _ -> internalError "the core IR is counted twice"
        | _ ->
            let dups, drops = this.Operands owned after (operands ownership e)
            this.Wrap dups drops e

    /// Counts the operands of an instruction, evaluated in this order: the variables it has to take a reference to
    /// first, and those (temporaries among them) it has to drop after it.
    member private this.Operands (owned: Set<int>) (after: Set<int>) (items: (Atom * Use) list) : Var list * Var list =
        let dups = ResizeArray<Var>()
        let drops = ResizeArray<Var>()
        // The instruction reads what it borrows when it runs, after every operand is evaluated, so a variable it borrows
        // lives until then: an operand evaluated after it (a temporary, or an owned use) cannot take its last reference.
        let borrowedLater =
            items
            |> List.choose (function
                | AVar var, Borrowed when var.Kind <> Temp && isHeapType var.Type -> Some var
                | _ -> None)
            |> List.distinctBy _.Id
        let borrowedIds = borrowedLater |> List.map _.Id |> Set.ofList
        let rec go (owned: Set<int>) (remaining: (Atom * Use) list) =
            match remaining with
            | [] -> ()
            | (atom, how) :: rest ->
                let afterRest = Set.union (rest |> List.map (fst >> this.FreeIn) |> Set.unionMany) after
                let afterThis = Set.union afterRest borrowedIds
                match atom with
                | AVar temp when temp.Kind = Temp ->
                    let value = temps.[temp.Id]
                    counted.[temp.Id] <- this.Count (Set.intersect owned (Set.union (this.Free value) afterThis)) afterThis value
                    if how = Borrowed && isHeapType temp.Type then drops.Add temp
                | AVar var when isHeapType var.Type ->
                    let diesHere = owned.Contains var.Id && not (afterThis.Contains var.Id)
                    match how with
                    | Owned -> if not diesHere then dups.Add var
                    | Borrowed -> if owned.Contains var.Id && not (afterRest.Contains var.Id) then drops.Add var
                | _ -> ()
                go (Set.intersect owned afterThis) rest
        go owned items
        // A variable borrowed, and last read by an operand evaluated after it (a temporary, or an owned use, which took
        // a reference of its own), is dropped once the instruction is done.
        for var in borrowedLater do
            let droppedWhereRead =
                match items |> List.findBack (fun (atom, _) -> this.FreeIn atom |> Set.contains var.Id) with
                | AVar last, Borrowed -> last.Id = var.Id
                | _ -> false
            if not droppedWhereRead && owned.Contains var.Id && not (after.Contains var.Id) then drops.Add var
        List.ofSeq dups, List.ofSeq drops

    member private _.Wrap (dups: Var list) (drops: Var list) (e: Expr) : Expr =
        let dropped = if drops.IsEmpty then e else Expr.make (DropAfter(e, drops)) e.Type
        if dups.IsEmpty then dropped else Expr.make (Dup(dups, dropped)) e.Type

/// `functions` with reference counting inserted, by the parameters `ownership` says each owns.
let internal insert (ownership: Ownership) (functions: Function list) : Function list =
    functions
    |> List.map (fun func ->
        let owned =
            func.Params
            |> List.indexed
            |> List.filter (fun (index, parameter) -> isHeapType parameter.Type && parameterUse ownership func.Name index = Owned)
            |> List.map (snd >> _.Id)
            |> Set.ofList
        { func with Body = Counter(ownership, func).Count owned Set.empty func.Body })
