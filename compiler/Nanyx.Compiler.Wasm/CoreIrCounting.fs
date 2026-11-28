/// Inserts reference counting into the core IR, as Perceus does (see "Inserting counts" in
/// `docs/src/content/deep-dives/memory-management.md`): a `Dup` where a value is used again after something takes it over, and a `Drop` (or
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

let internal parameterUse (ownership: Ownership) (name: string) (index: int) =
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
    // The update takes the record over, to update it in place when it holds the only reference (see `CoreIrEmit`). A
    // variable is read only when the update runs, after the new values, which may read its fields, so it counts as
    // used last; a temporary is computed first.
    | UpdateRecord(record, updates) ->
        let values = updates |> List.map snd |> all Owned
        match record with
        | AVar var when var.Kind <> Temp -> values @ [ record, Owned ]
        | _ -> (record, Owned) :: values
    // The value is taken over by the cell, which is used after it, to drop what it held.
    | Assign(cell, value) -> [ value, Owned; AVar cell, Borrowed ]
    | SetField(record, _, value) -> [ value, Owned; AVar record, Borrowed ]
    | MakeList items -> all Owned items
    | CallRuntime(name, args, _) -> args |> List.mapi (fun index arg -> arg, Helpers.operandUse name index)
    | CopyArrayInto(target, at, source) -> all Borrowed [ target; at; source ]
    // A float is passed as the number in its box, which is only read.
    | CallImport(_, args) -> all Borrowed args
    | MakeClosure(_, _, captured) -> all Owned captured
    // The closure is the environment, which the entry called borrows or owns (see `CoreIrClosureEntries`), as it does
    // the context members passed after the arguments (#2559). Its code reads the closure again for the table slot after
    // the arguments, so the closure counts as used last.
    | CallClosure(closure, args, Borrowing) ->
        let arguments = args.Length - CoreIrClosureEntries.memberCount closure.Type
        (args |> List.mapi (fun index arg -> arg, if index < arguments then Owned else Borrowed)) @ [ AVar closure, Borrowed ]
    | CallClosure(closure, args, Owning) -> all Owned args @ [ AVar closure, Owned ]
    | Interpolate parts -> parts |> List.choose (function Hole(hole, _) -> Some(AVar hole, Borrowed) | Text _ -> None)
    | Dbg(_, text)
    | CrashWith text -> [ text, Borrowed ]
    | ForEach loop -> all Borrowed (ForLoop.sourceAtoms loop)
    | If(cond, _, _) -> [ cond, Borrowed ]
    // A probe before a call that never returns borrows the locals it writes, so they are still alive there: nothing
    // after it reads them, and the program ends, so keeping them costs nothing on the way out (see `CoreIrProbes`).
    | Probe(Crashing, locals) -> locals |> List.map (fun local -> AVar local.Var, Borrowed)
    | FloatConstant _
    | Crash _
    | Probe _
    | While _
    | Match _
    | Let _
    | Do _
    | Dup _
    | Drop _
    | DropAfter _
    | Release _
    | Reuse _
    | FreeToken _ -> []

/// Every expression in `e` that is a node of its own: `e`, the values and bodies of its lets, branches, arms, guards
/// and loop bodies, and so on down (see `Expr.children`). The last child is yielded in tail position, so a chain as long
/// as a function takes no stack to walk.
let rec private nodes (e: Expr) : Expr seq =
    seq {
        yield e
        match Expr.children e with
        | [] -> ()
        | children ->
            for child in List.take (children.Length - 1) children do
                yield! nodes child
            yield! nodes (List.last children)
    }

/// The expressions whose values `e` gives: the end of its chain, in each branch and arm.
let rec private results (e: Expr) : Expr list =
    match e.Kind with
    | Let(_, _, body)
    | Do(_, body) -> results body
    | If(_, ifTrue, ifFalse) -> results ifTrue @ results ifFalse
    | Match(_, arms) -> arms |> List.collect (fun arm -> results arm.Body)
    | _ -> [ e ]

/// For each function whose result can be an address in a string (see `addressFlow`), the parameters it comes from, by
/// index: a string whose bytes it is an address of, or an address the caller made.
type internal AddressResults = Map<string, int list>

/// Which strings each value in `func` is an address in, as variables of `func`: the bytes of a string (see
/// `nanyx/host.memory`'s `addressOf`), an operation on such an address, a variable that holds one, or what a call
/// gives of a function that returns one made from its arguments (`addressResults`). With `fromParams`, each parameter
/// that is not on the heap stands for the strings the caller's address is in, so that a function's result can be
/// told apart by the parameters it comes from. A variable that holds an address keeps its strings alive, so that a
/// host's address stays valid for as long as it uses it, though nothing else reads the string.
let private addressFlow (addressResults: AddressResults) (fromParams: bool) (func: Function) : (Expr -> Var list) * (Atom -> Var list) =
    let temps = Dictionary<int, Expr>()
    let held = Dictionary<int, Var list>()
    // What each temporary's value is an address in, once asked, so that asking of each temporary of a long chain in
    // turn looks at each value of the chain once (#2351).
    let ofTemps = Dictionary<int, Var list>()
    let rec ofValue (e: Expr) : Var list =
        match e.Kind with
        | CallRuntime(name, [ AVar text ], _) when name = NyxCompiler.Intrinsics.stringAddress && text.Kind <> Temp -> [ text ]
        | Call(name, args) ->
            addressResults
            |> Map.tryFind name
            |> Option.defaultValue []
            |> List.collect (fun index ->
                match args |> List.tryItem index with
                | Some (AVar text) when text.Kind <> Temp && isHeapType text.Type -> [ text ]
                | Some atom -> ofAtom atom
                | None -> [])
        | Atom atom -> ofAtom atom
        | Prim(_, atoms) -> atoms |> List.collect ofAtom
        | _ -> []
        |> List.distinctBy _.Id
    and ofAtom (atom: Atom) : Var list =
        match atom with
        | AVar var when var.Kind = Temp ->
            match ofTemps.TryGetValue var.Id with
            | true, strings -> strings
            | _ ->
                match temps.TryGetValue var.Id with
                | true, value ->
                    let strings = ofValue value
                    ofTemps.[var.Id] <- strings
                    strings
                | _ -> []
        | AVar var ->
            match held.TryGetValue var.Id with
            | true, strings -> strings
            | _ -> []
        | _ -> []
    if fromParams then
        for parameter in func.Params do
            if not (isHeapType parameter.Type) then held.[parameter.Id] <- [ parameter ]
    for e in nodes func.Body do
        match e.Kind with
        | Let(var, value, _) when var.Kind = Temp -> temps.[var.Id] <- value
        | Let(var, value, _) ->
            match ofValue value with
            | [] -> ()
            | strings -> held.[var.Id] <- strings
        | _ -> ()
    ofValue, ofAtom

/// Which functions return an address in a string made from their arguments (see `AddressResults`).
let internal inferAddressResults (functions: Function list) : AddressResults =
    let mutable found: AddressResults = Map.empty
    let mutable changed = true
    while changed do
        changed <- false
        for f in functions do
            let ofValue, _ = addressFlow found true f
            let parameters = f.Params |> List.mapi (fun index parameter -> parameter.Id, index) |> Map.ofList
            let indices =
                results f.Body
                |> List.collect ofValue
                |> List.choose (fun var -> parameters |> Map.tryFind var.Id)
                |> List.distinct
                |> List.sort
            if not indices.IsEmpty && found |> Map.tryFind f.Name <> Some indices then
                found <- found |> Map.add f.Name indices
                changed <- true
    found

/// The calls `e` makes in tail position, by the rules of `docs/src/content/deep-dives/tail-calls.md`.
let rec private tailCalls (e: Expr) : (string * Atom list) list =
    match e.Kind with
    | Let(_, _, body)
    | Do(_, body) -> tailCalls body
    | If(_, ifTrue, ifFalse) -> tailCalls ifTrue @ tailCalls ifFalse
    | Match(_, arms) -> arms |> List.collect (fun arm -> tailCalls arm.Body)
    | Call(name, args) -> [ name, args ]
    | _ -> []

/// Whether `var`, bound to the value of `source`, is a copy of it that only reads it: a `match` scrutinee
/// (`let __match_tmp_0 = xs`), or a closure read as the record of what it captures, which is how the inliner gives an
/// inlined lambda its environment (see `CoreIrInline`).
let private isReadingCopy (var: Var) (source: Var) =
    var.Kind = MatchTemp
    || (match var.Type, source.Type with
        | IrRecord _, IrFunction _ -> var.Kind = Local || var.Kind = Temp
        | _ -> false)

/// The variables of `func` whose values the borrowed variables `roots` keep alive: the roots, each copy of one that
/// only reads it (`isReadingCopy`), and each name a pattern binds in a value one of them is. They live as long as the
/// roots do, so the function borrows them too.
let private borrowedFrom (func: Function) (roots: Set<int>) : Set<int> =
    let mutable found = roots
    // A value's node comes before the nodes of what follows it, and a match's before its arms.
    for e in nodes func.Body do
        match e.Kind with
        | Let(var, { Kind = Atom(AVar source) }, _) when isReadingCopy var source && found.Contains source.Id -> found <- found.Add var.Id
        | Match(scrutinees, arms) ->
            for arm in arms do
                for scrutinee, pattern in List.zip scrutinees arm.Patterns do
                    if found.Contains scrutinee.Id then
                        for var in Pattern.binds pattern do
                            found <- found.Add var.Id
        | _ -> ()
    found

/// Whether an arm of a `match` in `func` on one of `borrowed` could reuse the block of the object it takes apart (see
/// `CoreIrReuseSites`), or of one inside it, which only a function that owns the object can.
let private couldReuse (func: Function) (borrowed: Set<int>) : bool =
    let token = CoreIrReuseSites.reuseToken 0
    let inner = CoreIrReuseSites.innerObject 0
    nodes func.Body
    |> Seq.exists (fun e ->
        match e.Kind with
        | Match(scrutinees, arms) ->
            arms
            |> List.exists (fun arm ->
                List.zip scrutinees arm.Patterns
                |> List.exists (fun (scrutinee, pattern) ->
                    borrowed.Contains scrutinee.Id
                    && match CoreIrReuseSites.takenApart inner scrutinee.Type pattern with
                       | Some(_, taken) ->
                           CoreIrReuseSites.TakenApart.sizes taken
                           |> List.exists (fun size -> (CoreIrReuseSites.reuseFirst token size arm.Body).IsSome)
                       | None -> false))
        | _ -> false)

/// Which parameters each function owns. A function called through the function table (a lifted lambda, or the
/// wrapper of a function passed as a value) borrows its environment and the context members it is called with, which
/// are its last parameters, and owns the rest, as every call through a closure passes them so; its owning entry owns
/// them all (see `CoreIrClosureEntries`). The caller holds its members throughout, so a call need not count them up and
/// down (#2559). Another function owns a parameter on
/// the heap when it takes it over somewhere (passes it on owned, stores it, returns it), and borrows it when it only
/// reads it. A parameter it matches on is borrowed as Lean 4 infers it: when nothing bound from it is taken over (see
/// `borrowedFrom`), save by a call that borrows it in turn, and no arm could reuse its block (see `couldReuse`). A
/// parameter that a tail call passes something the caller owns to is owned as well: the caller could otherwise only
/// drop it after the call, which would then not be a tail call (see "Reference counting" in
/// `docs/src/content/deep-dives/tail-calls.md`).
let internal inferOwnership (functions: Function list) : Ownership =
    let indirect = CoreIrClosureEntries.targets functions
    let owningEntries = indirect |> Set.map CoreIrClosureEntries.owningName
    let members = CoreIrClosureEntries.memberCounts functions
    let entries =
        functions
        |> List.filter (fun f -> indirect.Contains f.Name)
        |> List.map (fun f ->
            let arguments = f.Params.Length - (members |> Map.tryFind f.Name |> Option.defaultValue 0)
            f.Name, f.Params |> List.mapi (fun index _ -> if index = 0 || index >= arguments then Borrowed else Owned))
    let uses = Dictionary<string, Use array>()
    for f in functions do
        if not (indirect.Contains f.Name || owningEntries.Contains f.Name) then
            uses.[f.Name] <- f.Params |> List.map (fun p -> if isHeapType p.Type then Borrowed else Owned) |> Array.ofList
    let current () =
        Seq.append (uses |> Seq.map (fun pair -> pair.Key, List.ofArray pair.Value)) entries |> Map.ofSeq
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
            let borrowedIds = borrowedFrom f (borrowedParams |> List.map (snd >> _.Id) |> Set.ofList)
            // A copy of a borrowed variable that only reads it borrows it, and takes nothing over.
            let copies = HashSet<Expr>(HashIdentity.Reference)
            for e in nodes f.Body do
                match e.Kind with
                | Let(var, ({ Kind = Atom(AVar source) } as value), _) when isReadingCopy var source && borrowedIds.Contains var.Id -> copies.Add value |> ignore
                | _ -> ()
            let takenOver =
                nodes f.Body
                |> Seq.filter (copies.Contains >> not)
                |> Seq.collect (operands ownership)
                |> Seq.choose (function AVar var, Owned when isHeapType var.Type -> Some var.Id | _ -> None)
                |> Set.ofSeq
            for index, parameter in borrowedParams do
                let kept = borrowedFrom f (Set.singleton parameter.Id)
                if not (Set.intersect kept takenOver).IsEmpty || couldReuse f kept then own f.Name index
            for callee, args in tailCalls f.Body do
                args
                |> List.iteri (fun index arg ->
                    match arg with
                    | AVar var when isHeapType var.Type && not (borrowedIds.Contains var.Id) -> own callee index
                    | _ -> ())
    current ()

/// The variables `func` borrows, by id: its parameters on the heap that `ownership` says it borrows, and what they keep
/// alive (see `borrowedFrom`). Every other variable on the heap holds a reference of its own.
let internal borrowedIn (ownership: Ownership) (func: Function) : Set<int> =
    func.Params
    |> List.indexed
    |> List.filter (fun (index, parameter) -> isHeapType parameter.Type && parameterUse ownership func.Name index = Borrowed)
    |> List.map (snd >> _.Id)
    |> Set.ofList
    |> borrowedFrom func

/// The reuse tokens of `func`, by id: the variables a release keeps the block it gives up in (see `CoreIrReuse`).
let internal reuseTokens (func: Function) : Set<int> =
    nodes func.Body
    |> Seq.choose (fun e ->
        match e.Kind with
        | Release(released, _) -> released.Token
        | _ -> None)
    |> Seq.map _.Id
    |> Set.ofSeq

/// Counts one function's body. `addresses` gives the strings a value is an address in (see `addressFlow`), which a use
/// of the value uses too. `owningEntries` gives the owning entry of each function a closure is over that has one.
type private Counter(ownership: Ownership, addresses: Atom -> Var list, owningEntries: Map<string, string>, func: Function) =
    let temps = Dictionary<int, Expr>()
    let vars = Dictionary<int, Var>()
    let counted = Dictionary<int, Expr>()
    let free = Dictionary<Expr, Set<int>>(HashIdentity.Reference)
    let tails = HashSet<Expr>(results func.Body, HashIdentity.Reference)
    // The variables the function borrows: its borrowed parameters, and what they keep alive (see `borrowedFrom`).
    let borrowed = borrowedIn ownership func

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
                | Reuse(_, body)
                | FreeToken(_, body) -> this.Free body
                | _ -> operands ownership e |> List.map (fst >> this.FreeIn) |> Set.unionMany
            free.[e] <- found
            found

    member this.FreeIn (atom: Atom) : Set<int> =
        match atom with
        | AVar var when var.Kind = Temp -> this.Free temps.[var.Id]
        | AVar var -> addresses atom |> List.map _.Id |> Set.ofList |> Set.add var.Id
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
            // A scrutinee that copies a borrowed variable borrows it too.
            | Let(var, value, body) when var.Kind = MatchTemp && borrowed.Contains var.Id ->
                rebuilds.Add(fun body -> dropDead (Expr.make (Let(var, value, body)) ty))
                owned <- Set.intersect ownedHere (Set.union (this.Free body) after)
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
            // which the arm then drops if it does not use it either. A name bound in a value the function borrows is
            // borrowed too.
            let countArm (arm: Arm) =
                let patterns = arm.Patterns |> List.map (Pattern.mapGuards (this.Count Set.empty Set.empty))
                let bodyFree = this.Free arm.Body
                let used = arm.Patterns |> List.collect Pattern.binds |> List.filter (fun var -> isHeapType var.Type && bodyFree.Contains var.Id && not (borrowed.Contains var.Id))
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
        | Reuse _
        | FreeToken _ -> internalError "the core IR is counted twice"
        // A closure that dies at a tail call is given to the owning entry, which the caller need not drop it after, as a
        // call by name of a function a closure is over that passes it a closure that dies there is (see
        // `CoreIrClosureEntries`).
        | CallClosure(closure, args, Borrowing) when this.DiesInTailCall owned after e closure ->
            let owning = { e with Kind = CallClosure(closure, args, Owning) }
            this.Instruction owned after owning (operands ownership owning)
        | Call(name, (AVar environment :: _ as args)) when owningEntries.ContainsKey name && this.DiesInTailCall owned after e environment ->
            let owning = { e with Kind = Call(owningEntries.[name], args) }
            this.Instruction owned after owning (args |> List.map (fun arg -> arg, Owned))
        | _ -> this.Instruction owned after e (operands ownership e)

    /// Whether `e` is a call in tail position that passes `closure` as an environment, and it dies there: a temporary,
    /// or a variable with a reference of its own that nothing uses after.
    member private _.DiesInTailCall (owned: Set<int>) (after: Set<int>) (e: Expr) (closure: Var) : bool =
        tails.Contains e && (closure.Kind = Temp || owned.Contains closure.Id && not (after.Contains closure.Id))

    /// An instruction on atoms, its operands, `items`, counted.
    member private this.Instruction (owned: Set<int>) (after: Set<int>) (e: Expr) (items: (Atom * Use) list) : Expr =
        // An instruction that reads an address reads the strings it is in, last, so that they live until it is done.
        let addressed = items |> List.collect (fst >> addresses) |> List.distinctBy _.Id |> List.map (fun text -> AVar text, Borrowed)
        let dups, drops = this.Operands owned after (items @ addressed)
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
                // A copy of a borrowed variable that only reads it is read as the variable is, with no reference of
                // its own (see `isReadingCopy`).
                | AVar temp when temp.Kind = Temp && borrowed.Contains temp.Id -> counted.[temp.Id] <- temps.[temp.Id]
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

/// `func` with each probe before a call that never returns (see `ProbeKind.Crashing`) writing only the locals it can
/// read without changing what the code before it frees or reuses: not a value on the heap the code has already taken
/// over on the way there, by an owning use or as a value a `match` takes apart, which may reuse its memory in place. Reading such a
/// local would keep it alive past that use, which would copy where the code reused, in every run of a debug build, not
/// only one that crashes: a loop that maps a list it owns, before a check that crashes, would copy the list each time
/// round. The code runs in the order of its chains' links; after a branch, a local either branch took over counts as
/// taken over. Each loop's body only borrows what it reads from outside, so nothing in it takes such a local over.
let private crashingProbesTrimmed (ownership: Ownership) (func: Function) : Function =
    let takenOverBy (e: Expr) =
        operands ownership e |> List.choose (function AVar var, Owned when var.Kind <> Temp && isHeapType var.Type -> Some var.Id | _ -> None) |> Set.ofList
    // `e`, with its crash probes trimmed, where `taken` were taken over before it, and what is taken over once it's done.
    let rec walk (taken: Set<int>) (e: Expr) : Expr * Set<int> =
        let links, last = Chain.split e
        let mutable taken = taken
        let links =
            links
            |> List.map (fun link ->
                match link.Kind with
                | Let(var, value, following) ->
                    let value, after = walk taken value
                    taken <- after
                    { link with Kind = Let(var, value, following) }
                | Do(value, following) ->
                    let value, after = walk taken value
                    taken <- after
                    { link with Kind = Do(value, following) }
                | _ -> link)
        let last, after = walkLast taken last
        Chain.rebuild Chain.relink links last, after
    and walkLast (taken: Set<int>) (e: Expr) : Expr * Set<int> =
        match e.Kind with
        | Probe(Crashing, locals) -> { e with Kind = Probe(Crashing, locals |> List.filter (fun local -> not (taken.Contains local.Var.Id))) }, taken
        | If(cond, ifTrue, ifFalse) ->
            let ifTrue, afterTrue = walk taken ifTrue
            let ifFalse, afterFalse = walk taken ifFalse
            { e with Kind = If(cond, ifTrue, ifFalse) }, Set.union afterTrue afterFalse
        | Match(scrutinees, arms) ->
            // An arm may reuse the memory of a value it takes apart.
            let inArms = Set.union taken (scrutinees |> List.filter (fun var -> isHeapType var.Type) |> List.map _.Id |> Set.ofList)
            let arms, after =
                arms
                |> List.map (fun arm ->
                    let body, after = walk inArms arm.Body
                    { arm with Body = body }, after)
                |> List.unzip
            { e with Kind = Match(scrutinees, arms) }, Set.unionMany (inArms :: after)
        | ForEach loop ->
            let body, _ = walk taken loop.Body
            { e with Kind = ForEach { loop with Body = body } }, Set.union taken (takenOverBy e)
        | While loop ->
            let condition, _ = walk taken loop.Condition
            let body, _ = walk taken loop.Body
            { e with Kind = While { loop with Condition = condition; Body = body } }, taken
        | _ -> e, Set.union taken (takenOverBy e)
    let hasCrashingProbe = nodes func.Body |> Seq.exists (fun e -> match e.Kind with Probe(Crashing, _) -> true | _ -> false)
    if hasCrashingProbe then { func with Body = fst (walk Set.empty func.Body) } else func

/// `functions` with reference counting inserted, by the parameters `ownership` says each owns.
let internal insert (ownership: Ownership) (functions: Function list) : Function list =
    let addressResults = inferAddressResults functions
    let owningEntries = CoreIrClosureEntries.owningEntries functions
    functions
    |> List.map (crashingProbesTrimmed ownership)
    |> List.map (fun func ->
        let owned =
            func.Params
            |> List.indexed
            |> List.filter (fun (index, parameter) -> isHeapType parameter.Type && parameterUse ownership func.Name index = Owned)
            |> List.map (snd >> _.Id)
            |> Set.ofList
        { func with Body = Counter(ownership, snd (addressFlow addressResults false func), owningEntries, func).Count owned Set.empty func.Body })
